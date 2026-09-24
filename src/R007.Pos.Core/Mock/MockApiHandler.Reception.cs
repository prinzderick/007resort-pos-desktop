using System.Globalization;
using System.Security.Cryptography;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

public sealed partial class MockApiHandler
{
    private HttpResponseMessage? RouteReception(HttpRequestMessage request, string method, string[] seg, Dictionary<string, string> query, byte[] body, Caller caller)
    {
        string[] a;

        if (method == "GET" && Match(seg, "bookings/resources", out _))
        {
            return Json(200, new Page<BookableResource>(_resources, null), Ctx.PageBookableResource);
        }

        if (method == "GET" && Match(seg, "bookings/resources/{}/availability", out a))
        {
            var resource = _resources.FirstOrDefault(r => r.Id == G(a[0])) ?? throw new MockProblem(404, "not_found", "Resource not found");
            var from = DateTimeOffset.Parse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var slots = new List<Slot>();
            var start = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 8; i++)
            {
                var s = start.AddMinutes(i * resource.SlotMinutes);
                var used = BookedQuantity(resource.Id, s);
                var capacity = resource.Capacity ?? 1;
                var remaining = Math.Max(0, capacity - used);
                slots.Add(new Slot(s, s.AddMinutes(resource.SlotMinutes), remaining > 0, remaining, resource.Price));
            }

            return Json(200, new Availability(resource.Id, from, from.AddHours(8), slots), Ctx.Availability);
        }

        if (method == "POST" && Match(seg, "bookings/hold", out _))
        {
            Need(caller, Permissions.BookingCreate);
            return Hold(Read(body, Ctx.HoldRequest), caller);
        }

        if (method == "GET" && Match(seg, "bookings/{}", out a))
        {
            Need(caller, Permissions.BookingCreate);
            return Json(200, ToDto(GetBooking(a[0])), Ctx.Booking);
        }

        if (method == "POST" && Match(seg, "bookings/{}/confirm", out a))
        {
            Need(caller, Permissions.BookingCreate);
            var booking = GetBooking(a[0]);
            CheckIfMatchBooking(request, booking);
            return Confirm(booking, Read(body, Ctx.ConfirmBookingRequest), caller);
        }

        // Reception flow like the node: hold (no order yet) -> POST /orders (slot fee + rentals) -> attach here -> pay the order.
        if (method == "POST" && Match(seg, "bookings/{}/order", out a))
        {
            Need(caller, Permissions.BookingCreate);
            var booking = GetBooking(a[0]);
            CheckIfMatchBooking(request, booking);
            var orderId = Read(body, Ctx.AttachOrderRequest).OrderId;
            var order = _orders.GetValueOrDefault(orderId) ?? throw new MockProblem(404, "not_found", "Order not found");
            if (booking.Status != BookingStatuses.Held || booking.HoldExpiresAt <= Now)
            {
                throw new MockProblem(409, "hold_expired", "The hold is no longer active");
            }

            booking.OrderId = order.Id;
            booking.Status = BookingStatuses.PendingPayment;
            booking.RowVersion++;
            order.BookingId = booking.Id;
            return Json(200, ToDto(booking), Ctx.Booking);
        }

        if (method == "POST" && Match(seg, "bookings/{}/cancel", out a))
        {
            Need(caller, Permissions.BookingCreate);
            var booking = GetBooking(a[0]);
            CheckIfMatchBooking(request, booking);
            if (booking.Status is BookingStatuses.Confirmed)
            {
                throw new MockProblem(409, "order_state_invalid", "A paid booking must be refunded, not cancelled");
            }

            booking.Status = BookingStatuses.Cancelled;
            booking.RowVersion++;
            if (booking.OrderId is { } attached)
            {
                _orders[attached].Status = OrderStatuses.Voided;
            }

            return Json(200, ToDto(booking), Ctx.Booking);
        }

        if (method == "POST" && Match(seg, "entitlements", out _))
        {
            Need(caller, Permissions.TicketIssue);
            var req = Read(body, Ctx.IssueEntitlementRequest);
            var order = _orders.GetValueOrDefault(req.OrderId ?? Guid.Empty) ?? throw new MockProblem(404, "not_found", "Order not found");
            if (_entitlements.Values.FirstOrDefault(e => e.OrderId == order.Id) is { } existing)
            {
                return Json(201, existing, Ctx.Entitlement);
            }

            if (order.Status != OrderStatuses.Settled)
            {
                throw new MockProblem(409, "order_state_invalid", "Tickets are issued only for a paid order");
            }

            return Json(201, IssueEntitlement(order, null), Ctx.Entitlement);
        }

        if (method == "GET" && Match(seg, "entitlements", out _))
        {
            Need(caller, Permissions.TicketIssue);
            query.TryGetValue("filter[orderId]", out var byOrder);
            query.TryGetValue("filter[bookingId]", out var byBooking);
            var found = _entitlements.Values.Where(e => (byOrder is null || e.OrderId == G(byOrder)) && (byBooking is null || e.BookingId == G(byBooking))).ToList();
            return Json(200, new Page<Entitlement>(found, null), Ctx.PageEntitlement);
        }

        if (method == "GET" && Match(seg, "entitlements/{}", out a))
        {
            Need(caller, Permissions.TicketIssue);
            return Json(200, _entitlements.GetValueOrDefault(G(a[0])) ?? throw new MockProblem(404, "not_found", "Entitlement not found"), Ctx.Entitlement);
        }

        return null;
    }

    private int BookedQuantity(Guid resourceId, DateTimeOffset start) =>
        _bookings.Values
            .Where(b => b.Resource.Id == resourceId && b.Start == start && (b.Status == BookingStatuses.Confirmed || (b.Status == BookingStatuses.Held && b.HoldExpiresAt > Now)))
            .Sum(b => b.Resource.Mode == "WHOLE_RESOURCE" || b.Resource.Mode == "TIME_SLOT" ? b.Resource.Capacity ?? 1 : b.Quantity);

    private MBooking GetBooking(string id) =>
        _bookings.TryGetValue(G(id), out var b) ? b : throw new MockProblem(404, "not_found", "Booking not found");

    private static void CheckIfMatchBooking(HttpRequestMessage request, MBooking booking) => CheckIfMatch(request, booking.RowVersion);

    private Booking ToDto(MBooking b)
    {
        // Like the node: the booking's own total is the slot fee; rentals live on the attached order.
        var fee = b.Resource.Mode == "INDIVIDUAL_CAPACITY" ? b.Resource.Price * b.Quantity : b.Resource.Price;
        var status = b.Status is BookingStatuses.Held or BookingStatuses.PendingPayment && b.HoldExpiresAt <= Now ? BookingStatuses.Expired : b.Status;
        return new Booking(b.Id, b.Number, b.Resource.Id, b.Resource.Name, b.Resource.FacilityId, b.Start, b.End, b.Quantity, status, b.HoldExpiresAt, fee, b.Status == BookingStatuses.Confirmed ? fee : 0m, b.OrderId, b.EntitlementId, b.RowVersion);
    }

    private HttpResponseMessage Hold(HoldRequest req, Caller caller)
    {
        var resource = _resources.FirstOrDefault(r => r.Id == req.ResourceId) ?? throw new MockProblem(404, "not_found", "Resource not found");
        var quantity = req.Quantity ?? 1;
        var used = BookedQuantity(resource.Id, req.Start);
        if ((resource.Capacity ?? 1) - used < (resource.Mode == "INDIVIDUAL_CAPACITY" ? quantity : resource.Capacity ?? 1))
        {
            throw new MockProblem(409, "slot_unavailable", "That slot was just taken");
        }

        var booking = new MBooking
        {
            Id = Guid.CreateVersion7(),
            Number = $"B-{(++_bookingSeq).ToString(CultureInfo.InvariantCulture)}",
            Resource = resource,
            Start = req.Start,
            End = req.End,
            Quantity = quantity,
            HoldExpiresAt = Now.AddSeconds(300),
        };
        _bookings[booking.Id] = booking;
        return Json(201, ToDto(booking), Ctx.Booking);
    }

    private HttpResponseMessage Confirm(MBooking booking, ConfirmBookingRequest req, Caller caller)
    {
        if (booking.Status == BookingStatuses.Confirmed)
        {
            return Json(200, ToDto(booking), Ctx.Booking);
        }

        if (booking.Status is not (BookingStatuses.Held or BookingStatuses.PendingPayment))
        {
            throw new MockProblem(409, "order_state_invalid", "Booking cannot be confirmed");
        }

        if (booking.HoldExpiresAt <= Now)
        {
            throw new MockProblem(409, "hold_expired", "The hold expired; start again");
        }

        Need(caller, Permissions.PaymentTake);
        var order = booking.OrderId is { } attached ? _orders[attached] : NewSlotFeeOrder(booking, caller);
        var balance = ToDto(order).BalanceDue;
        ApplyPayment(caller, MockData.ReceptionFacilityId, [(order, balance)], req.Tenders ?? [], req.CashSessionId); // confirms the booking (see ConfirmBookingsForPaid)
        return Json(200, ToDto(booking), Ctx.Booking);
    }

    private MOrder NewSlotFeeOrder(MBooking booking, Caller caller)
    {
        var product = MockData.Products.First(p => p.Id == booking.Resource.ProductId);
        var order = new MOrder
        {
            Id = Guid.CreateVersion7(),
            Number = $"REC-{(++_orderSeq).ToString("000000", CultureInfo.InvariantCulture)}",
            FacilityId = MockData.ReceptionFacilityId,
            Channel = OrderChannels.Counter,
            CreatedAt = Now,
            CreatedBy = caller.Staff.Id,
            BookingId = booking.Id,
        };
        order.Lines.Add(new MLine { Id = Guid.CreateVersion7(), ProductId = product.Id, Name = product.Name, Quantity = booking.Resource.Mode == "INDIVIDUAL_CAPACITY" ? booking.Quantity : 1, BaseUnitPrice = product.Price, Kind = product.Kind });
        _orders[order.Id] = order;
        booking.OrderId = order.Id;
        return order;
    }

    /// <summary>Paying the attached order in full confirms the booking and issues the QR entitlement (the node does this inside the payment transaction).</summary>
    private void ConfirmBookingsForPaid(IEnumerable<MOrder> orders)
    {
        foreach (var order in orders)
        {
            if (order.BookingId is { } id && _bookings.TryGetValue(id, out var booking) && booking.Status is BookingStatuses.Held or BookingStatuses.PendingPayment && ToDto(order).BalanceDue <= 0m)
            {
                if (booking.HoldExpiresAt <= Now)
                {
                    throw new MockProblem(409, "hold_expired", "The hold expired; start again");
                }

                booking.Status = BookingStatuses.Confirmed;
                booking.RowVersion++;
                booking.EntitlementId = IssueEntitlement(order, booking).Id;
            }
        }
    }

    private Entitlement IssueEntitlement(MOrder order, MBooking? booking)
    {
        var items = order.Lines.Where(l => l.Status != "REMOVED").Select(l => new EntitlementItem(
            Guid.CreateVersion7(),
            l.Kind == ProductKinds.Rental ? "RENTAL" : l.Kind == ProductKinds.Ticket ? "ACCESS" : "ITEM",
            l.Name,
            booking?.Resource.FacilityId ?? order.FacilityId,
            l.Quantity,
            0,
            l.Kind == ProductKinds.Ticket ? "SINGLE_USE" : "NONE",
            l.Kind == ProductKinds.Rental ? "NOT_RELEASED" : null)).ToList();
        var entitlement = new Entitlement(
            Guid.CreateVersion7(),
            "ENT." + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)),
            "ACTIVE",
            order.Id,
            booking?.Id,
            order.CustomerName,
            items,
            Now);
        _entitlements[entitlement.Id] = entitlement;
        return entitlement;
    }
}
