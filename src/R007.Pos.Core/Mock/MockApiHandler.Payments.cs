using System.Globalization;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

public sealed partial class MockApiHandler
{
    private HttpResponseMessage? RoutePayments(HttpRequestMessage request, string method, string[] seg, Dictionary<string, string> query, byte[] body, Caller caller)
    {
        string[] a;

        if (method == "POST" && Match(seg, "payments", out _))
        {
            Need(caller, Permissions.PaymentTake);
            var req = Read(body, Ctx.CreatePaymentRequest);
            var allocations = req.Allocations.Select(x => (Order: _orders.GetValueOrDefault(x.OrderId) ?? throw new MockProblem(404, "not_found", "Order not found"), x.Amount)).ToList();
            var result = ApplyPayment(caller, req.FacilityId, allocations, req.Tenders, req.CashSessionId);
            return Json(201, result, Ctx.PaymentResult);
        }

        if (method == "GET" && Match(seg, "payments", out _))
        {
            Need(caller, Permissions.PaymentView);
            var facility = query.TryGetValue("filter[facilityId]", out var f) ? G(f) : (Guid?)null;
            var session = query.TryGetValue("filter[cashSessionId]", out var s) ? G(s) : (Guid?)null;
            var items = _payments.Values
                .Where(p => (facility is null || p.FacilityId == facility) && (session is null || p.CashSessionId == session))
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Select(p => p.ToDto()).ToList();
            return Json(200, new Page<Payment>(items, null), Ctx.PagePayment);
        }

        if (method == "GET" && Match(seg, "payments/{}", out a) && a[0] != "paystack")
        {
            Need(caller, Permissions.PaymentView);
            return Json(200, GetPayment(a[0]).ToDto(), Ctx.Payment);
        }

        if (method == "POST" && Match(seg, "payments/{}/refund", out a))
        {
            Need(caller, Permissions.RefundExecute);
            return Refund(request, GetPayment(a[0]), Read(body, Ctx.RefundRequest), caller);
        }

        if (method == "POST" && Match(seg, "payments/{}/reversal", out a))
        {
            Need(caller, Permissions.PaymentReversalExecute);
            return Reverse(request, GetPayment(a[0]), Read(body, Ctx.ReversalRequest), caller);
        }

        if (method == "POST" && Match(seg, "payments/paystack/initialize", out _))
        {
            Need(caller, Permissions.PaymentTake);
            return PaystackInit(Read(body, Ctx.PaystackInitRequest), caller);
        }

        if (method == "GET" && Match(seg, "payments/paystack/verify/{}", out a))
        {
            Need(caller, Permissions.PaymentView);
            var payment = _payments.Values.FirstOrDefault(p => p.ProviderReference == Uri.UnescapeDataString(a[0])) ?? throw new MockProblem(404, "not_found", "Unknown reference");
            return Json(200, payment.ToDto(), Ctx.Payment);
        }

        if (method == "POST" && Match(seg, "tabs/{}/settle", out a))
        {
            Need(caller, Permissions.OrderSettle);
            var tab = GetTab(a[0]);
            if (tab.Status != "OPEN")
            {
                throw new MockProblem(409, "order_state_invalid", "Tab is not open");
            }

            var req = Read(body, Ctx.SettleTabRequest);
            var due = tab.OrderIds.Select(id => _orders[id]).Where(o => o.Status != OrderStatuses.Voided && o.Status != OrderStatuses.PendingApproval)
                .Select(o => (Order: o, Amount: ToDto(o).BalanceDue)).Where(x => x.Amount > 0m).ToList();
            var result = ApplyPayment(caller, tab.FacilityId, due, req.Tenders, req.CashSessionId);
            if (tab.OrderIds.Select(id => _orders[id]).All(o => o.Status is OrderStatuses.Settled or OrderStatuses.Voided))
            {
                tab.Status = "SETTLED";
                tab.RowVersion++;
                if (tab.TableId is { } tableId && _tables.TryGetValue(tableId, out var table))
                {
                    _tables[tableId] = table with { Status = "FREE", OpenTabId = null };
                }
            }

            return Json(200, result, Ctx.PaymentResult);
        }

        if (method == "GET" && Match(seg, "receipts/{}", out a))
        {
            Need(caller, Permissions.ReceiptView);
            var id = G(a[0]);
            var receipt = _receipts.GetValueOrDefault(id) ?? throw new MockProblem(404, "not_found", "Receipt not found");
            if (query.GetValueOrDefault("reprint") == "true")
            {
                Need(caller, Permissions.ReceiptReprint);
                receipt = receipt with { ReprintCount = (receipt.ReprintCount ?? 0) + 1 };
                _receipts[id] = receipt;
                return Json(200, receipt with { Footer = "*** DUPLICATE ***" }, Ctx.Receipt);
            }

            return Json(200, receipt, Ctx.Receipt);
        }

        if (method == "GET" && Match(seg, "orders/{}/receipt", out a))
        {
            Need(caller, Permissions.ReceiptView);
            var orderId = G(a[0]);
            var receipt = _receiptsByOrder.GetValueOrDefault(orderId)?.LastOrDefault() ?? throw new MockProblem(404, "not_found", "No receipt for this order yet");
            var entitlement = _entitlements.Values.FirstOrDefault(e => e.OrderId == orderId);
            return Json(200, entitlement is null ? receipt : receipt with { QrPayload = entitlement.QrToken }, Ctx.Receipt);
        }

        if (method == "GET" && Match(seg, "memberships", out _))
        {
            Need(caller, Permissions.MembershipView);
            var q = query.GetValueOrDefault("q") ?? string.Empty;
            var items = _memberships.Where(m => m.HolderName.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Number.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            return Json(200, new Page<Membership>(items, null), Ctx.PageMembership);
        }

        return RouteCash(request, method, seg, query, body, caller);
    }

    private HttpResponseMessage? RouteCash(HttpRequestMessage request, string method, string[] seg, Dictionary<string, string> query, byte[] body, Caller caller)
    {
        string[] a;

        if (method == "POST" && Match(seg, "cash-sessions", out _))
        {
            Need(caller, Permissions.CashSessionOpen);
            var req = Read(body, Ctx.OpenCashSessionRequest);
            if (_cashSessions.Values.Any(c => c.Status == "OPEN" && c.StaffId == caller.Staff.Id && c.FacilityId == req.FacilityId))
            {
                throw new MockProblem(409, "order_state_invalid", "A cash session is already open for this cashier");
            }

            var session = new MCashSession { Id = Guid.CreateVersion7(), FacilityId = req.FacilityId, StaffId = caller.Staff.Id, OpeningFloat = req.OpeningFloat, OpenedAt = Now };
            _cashSessions[session.Id] = session;
            return Json(201, ToDto(session), Ctx.CashSession);
        }

        if (method == "GET" && Match(seg, "cash-sessions", out _))
        {
            Need(caller, Permissions.CashSessionView);
            var facility = query.TryGetValue("filter[facilityId]", out var f) ? G(f) : (Guid?)null;
            var staff = query.TryGetValue("filter[staffId]", out var s) ? G(s) : (Guid?)null;
            var status = query.GetValueOrDefault("filter[status]");
            var items = _cashSessions.Values
                .Where(c => (facility is null || c.FacilityId == facility) && (staff is null || c.StaffId == staff) && (status is null || c.Status == status))
                .OrderByDescending(c => c.OpenedAt).Select(ToDto).ToList();
            return Json(200, new Page<CashSession>(items, null), Ctx.PageCashSession);
        }

        if (method == "POST" && Match(seg, "cash-sessions/{}/close", out a))
        {
            Need(caller, Permissions.CashSessionClose);
            var session = _cashSessions.GetValueOrDefault(G(a[0])) ?? throw new MockProblem(404, "not_found", "Cash session not found");
            if (session.Status != "OPEN")
            {
                throw new MockProblem(409, "order_state_invalid", "Cash session already closed");
            }

            session.Counted = Read(body, Ctx.CloseCashSessionRequest).CountedCash;
            session.Status = "CLOSED";
            session.ClosedAt = Now;
            return Json(200, ToDto(session), Ctx.CashSession);
        }

        if (method == "GET" && Match(seg, "reports/cashier-shift/{}", out a))
        {
            Need(caller, Permissions.ReportView);
            var session = _cashSessions.GetValueOrDefault(G(a[0])) ?? throw new MockProblem(404, "not_found", "Shift not found");
            var dto = ToDto(session);
            var byTender = _payments.Values.Where(p => p.CashSessionId == session.Id && p.Status is PaymentStatuses.Captured or PaymentStatuses.PartiallyRefunded or PaymentStatuses.Refunded)
                .GroupBy(p => p.Tender).Select(g => new ShiftTenderTotal(g.Key, g.Sum(p => p.Amount - p.Refunded), g.Count())).ToList();
            var refunds = _payments.Values.Where(p => p.CashSessionId == session.Id).Sum(p => p.Refunded);
            var staff = _staff.First(s => s.Id == session.StaffId);
            return Json(200, new CashierShiftReport(session.Id, session.StaffId, staff.Display, session.FacilityId, session.OpenedAt, session.ClosedAt, session.OpeningFloat, byTender, dto.ExpectedCash ?? 0m, dto.CountedCash, dto.Variance, refunds, 0, new Freshness(Now, "local", null, false, null, 0, 300)), Ctx.CashierShiftReport);
        }

        return null;
    }

    private CashSession ToDto(MCashSession c)
    {
        var cash = _payments.Values.Where(p => p.CashSessionId == c.Id && p.Tender == TenderTypes.Cash).Sum(p => p.Amount - p.Refunded);
        var expected = c.OpeningFloat + cash;
        return new CashSession(c.Id, c.FacilityId, c.StaffId, c.Status, c.OpeningFloat, expected, c.Counted, c.Counted is { } counted ? counted - expected : null, c.OpenedAt, c.ClosedAt);
    }

    private MPayment GetPayment(string id) =>
        _payments.TryGetValue(G(id), out var payment) ? payment : throw new MockProblem(404, "not_found", "Payment not found");

    private PaymentResult ApplyPayment(Caller caller, Guid facilityId, List<(MOrder Order, decimal Amount)> allocations, IReadOnlyList<TenderInput> tenders, Guid? cashSessionId)
    {
        if (tenders.Count == 0)
        {
            throw new MockProblem(422, "validation_failed", "At least one tender is required");
        }

        if (tenders.Count > 1)
        {
            Need(caller, Permissions.PaymentSplit);
        }

        foreach (var t in tenders)
        {
            RequireV7(t.Id);
        }

        // Idempotent by client tender id: same id returns the original result.
        if (tenders[0].Id is { } firstId && _payments.TryGetValue(firstId, out var prior))
        {
            var group = _payments.Values.Where(p => p.GroupId == prior.GroupId).OrderBy(p => p.CreatedAt).ToList();
            return new PaymentResult([.. group.Select(p => p.ToDto())], [.. group.SelectMany(p => p.Allocations).Select(x => Summary(_orders[x.OrderId])).DistinctBy(s => s.Id)], group[0].ReceiptId, group.Sum(p => p.Change ?? 0m));
        }

        if (tenders.Any(t => t.Amount <= 0m))
        {
            throw new MockProblem(422, "validation_failed", "Tender amounts must be positive");
        }

        if (tenders.Sum(t => t.Amount) != allocations.Sum(x => x.Amount))
        {
            throw new MockProblem(422, "amount_mismatch", "Tenders must add up to the amount allocated to orders");
        }

        var toPay = new List<(MOrder Order, decimal Amount)>();
        foreach (var (order, amount) in allocations)
        {
            if (order.Status is OrderStatuses.Voided or OrderStatuses.PendingApproval or OrderStatuses.Settled)
            {
                throw new MockProblem(409, "order_state_invalid", $"Order {order.Number} cannot take payment ({order.Status})");
            }

            if (_paymentTiming.TryGetValue(order.FacilityId, out var timing) && timing != PaymentTimings.PayFirst && order.Status != OrderStatuses.Served)
            {
                throw new MockProblem(409, "order_state_invalid", $"This facility takes payment after service; the order is {order.Status}.");
            }

            if (amount <= 0m || amount > ToDto(order).BalanceDue)
            {
                throw new MockProblem(409, "balance_changed", $"Order {order.Number} balance is now {ToDto(order).BalanceDue.ToString("0.00", CultureInfo.InvariantCulture)}");
            }

            toPay.Add((order, amount));
        }

        var caps = _facilityCaps[facilityId];
        if (tenders.Any(t => t.TenderType == TenderTypes.Cash) && caps.OperatingRules?.RequireCashSession == true)
        {
            var open = cashSessionId is { } sid && _cashSessions.TryGetValue(sid, out var cs) && cs.Status == "OPEN" && cs.StaffId == caller.Staff.Id;
            if (!open)
            {
                throw new MockProblem(422, "cash_session_required", "Open a cash session before taking cash");
            }
        }

        foreach (var t in tenders)
        {
            if (t.TenderType == TenderTypes.Cash && (t.Tendered ?? t.Amount) < t.Amount)
            {
                throw new MockProblem(422, "validation_failed", "Cash tendered is less than the amount");
            }

            if (TenderTypes.NeedsReference(t.TenderType) && string.IsNullOrWhiteSpace(t.Reference))
            {
                throw new MockProblem(422, "validation_failed", $"{t.TenderType} needs a reference");
            }
        }

        // Effects (validated above, so this cannot half-apply)
        var groupId = Guid.CreateVersion7();
        var pool = new Queue<(MOrder Order, decimal Left)>(toPay.Select(x => (x.Order, x.Amount)));
        var payments = new List<MPayment>();
        foreach (var t in tenders)
        {
            var payment = new MPayment
            {
                Id = t.Id ?? Guid.CreateVersion7(),
                GroupId = groupId,
                FacilityId = facilityId,
                Tender = t.TenderType,
                Reference = t.Reference,
                Amount = t.Amount,
                Tendered = t.TenderType == TenderTypes.Cash ? t.Tendered ?? t.Amount : null,
                Change = t.TenderType == TenderTypes.Cash ? (t.Tendered ?? t.Amount) - t.Amount : null,
                CashSessionId = t.TenderType == TenderTypes.Cash ? cashSessionId : null,
                TakenBy = caller.Staff.Id,
                CreatedAt = Now,
            };

            var remaining = t.Amount;
            while (remaining > 0m)
            {
                var (order, left) = pool.Peek();
                var take = Math.Min(remaining, left);
                payment.Allocations.Add(new PaymentAllocation(order.Id, take));
                order.AmountPaid += take;
                remaining -= take;
                pool.Dequeue();
                if (left - take > 0m)
                {
                    var rest = new Queue<(MOrder, decimal)>();
                    rest.Enqueue((order, left - take));
                    foreach (var x in pool)
                    {
                        rest.Enqueue(x);
                    }

                    pool = rest;
                }
            }

            _payments[payment.Id] = payment;
            payments.Add(payment);
        }

        var touched = toPay.Select(x => x.Order).Distinct().ToList();
        foreach (var order in touched)
        {
            SettleIfPaid(order);
        }

        ConfirmBookingsForPaid(touched);

        var receipt = BuildReceipt(caller, facilityId, touched, payments);
        foreach (var p in payments)
        {
            p.ReceiptId = receipt.Id;
        }

        return new PaymentResult([.. payments.Select(p => p.ToDto())], [.. touched.Select(Summary)], receipt.Id, payments.Sum(p => p.Change ?? 0m));
    }

    private void SettleIfPaid(MOrder order)
    {
        var dto = ToDto(order);
        order.RowVersion++;
        var keepsStatus = _paymentTiming.TryGetValue(order.FacilityId, out var timing) && timing == PaymentTimings.PayFirst && order.Status != OrderStatuses.Served;
        if (dto.BalanceDue <= 0m && dto.Total > 0m && !keepsStatus)
        {
            order.Status = OrderStatuses.Settled;
            ReleaseTable(order);
        }
    }

    private Receipt BuildReceipt(Caller caller, Guid facilityId, List<MOrder> orders, List<MPayment> payments)
    {
        var dtos = orders.Select(ToDto).ToList();
        var receipt = new Receipt(
            Guid.CreateVersion7(),
            $"R-{(++_receiptSeq).ToString(CultureInfo.InvariantCulture)}",
            facilityId,
            MockData.FacilityName(facilityId),
            "007 Resort & Spa",
            "Site address (mock)",
            Now,
            caller.Staff.Display,
            [.. orders.Select(o => o.Number)],
            orders.Select(Summary).Select(s => s.TableLabel).FirstOrDefault(l => l is not null),
            [.. dtos.SelectMany(o => o.Lines).Select(l => new ReceiptItem(l.Name, l.Quantity, l.UnitPrice, l.LineTotal))],
            dtos.Sum(o => o.Subtotal),
            dtos.Sum(o => o.DiscountTotal),
            dtos.Sum(o => o.TaxTotal),
            dtos.Sum(o => o.Total),
            "NGN",
            [.. payments.Select(p => new ReceiptTender(p.Tender, p.Amount, p.Reference))],
            payments.Sum(p => p.Change ?? 0m) is var change and > 0m ? change : null,
            null,
            null,
            0,
            "Thank you for choosing 007 Resort & Spa");
        _receipts[receipt.Id] = receipt;
        foreach (var o in orders)
        {
            if (!_receiptsByOrder.TryGetValue(o.Id, out var list))
            {
                _receiptsByOrder[o.Id] = list = [];
            }

            list.Add(receipt);
        }

        return receipt;
    }

    private HttpResponseMessage Refund(HttpRequestMessage request, MPayment payment, RefundRequest req, Caller caller)
    {
        if (payment.Status is not (PaymentStatuses.Captured or PaymentStatuses.PartiallyRefunded))
        {
            throw new MockProblem(409, "payment_state_invalid", "Payment cannot be refunded");
        }

        if (req.Amount <= 0m || req.Amount > payment.Amount - payment.Refunded)
        {
            throw new MockProblem(422, "amount_mismatch", "Refund exceeds what is refundable");
        }

        var refund = new Refund(Guid.CreateVersion7(), payment.Id, req.Amount, req.Reason, "COMPLETED", null, Now);

        void Apply()
        {
            payment.Refunded += req.Amount;
            payment.Status = payment.Refunded >= payment.Amount ? PaymentStatuses.Refunded : PaymentStatuses.PartiallyRefunded;
        }

        if (IsPreApproved(request, caller, Permissions.RefundApprove))
        {
            Apply();
            return Json(201, refund, Ctx.Refund);
        }

        var approval = RequestApproval(caller, "payment.refund", "payment", payment.Id, payment.FacilityId, req.Reason, req.Amount, $"Refund {req.Amount.ToString("0.00", CultureInfo.InvariantCulture)} on payment", Permissions.RefundApprove, Apply, () => { }, null);
        return Json(201, refund with { Status = "PENDING_APPROVAL", ApprovalId = approval.Id }, Ctx.Refund);
    }

    private HttpResponseMessage Reverse(HttpRequestMessage request, MPayment payment, ReversalRequest req, Caller caller)
    {
        if (payment.Status != PaymentStatuses.Captured)
        {
            throw new MockProblem(409, "payment_state_invalid", "Only a captured payment can be reversed");
        }

        var reversal = new PaymentReversal(Guid.CreateVersion7(), payment.Id, req.Reason, "COMPLETED", null, Now);

        void Apply()
        {
            payment.Status = PaymentStatuses.Reversed;
            foreach (var alloc in payment.Allocations)
            {
                var order = _orders[alloc.OrderId];
                order.AmountPaid -= alloc.Amount;
                if (order.Status == OrderStatuses.Settled)
                {
                    order.Status = OrderStatuses.Sent;
                }

                order.RowVersion++;
            }
        }

        if (IsPreApproved(request, caller, Permissions.PaymentReversalApprove))
        {
            Apply();
            return Json(201, reversal, Ctx.PaymentReversal);
        }

        var approval = RequestApproval(caller, "payment.reversal", "payment", payment.Id, payment.FacilityId, req.Reason, payment.Amount, "Reverse payment", Permissions.PaymentReversalApprove, Apply, () => { }, null);
        return Json(201, reversal with { Status = "PENDING_APPROVAL", ApprovalId = approval.Id }, Ctx.PaymentReversal);
    }

    private HttpResponseMessage PaystackInit(PaystackInitRequest req, Caller caller)
    {
        var orders = (req.OrderIds ?? []).Select(id => _orders.GetValueOrDefault(id) ?? throw new MockProblem(404, "not_found", "Order not found")).ToList();
        var reference = "PSK-" + Guid.CreateVersion7().ToString("N")[..12].ToUpperInvariant();
        var payment = new MPayment
        {
            Id = Guid.CreateVersion7(),
            GroupId = Guid.CreateVersion7(),
            FacilityId = orders.FirstOrDefault()?.FacilityId ?? MockData.RestaurantFacilityId,
            Tender = TenderTypes.Card,
            Provider = "PAYSTACK",
            ProviderReference = reference,
            Status = PaymentStatuses.Authorizing,
            Amount = req.Amount,
            TakenBy = caller.Staff.Id,
            CreatedAt = Now,
        };

        var remaining = req.Amount;
        foreach (var order in orders)
        {
            var take = Math.Min(remaining, ToDto(order).BalanceDue);
            if (take > 0m)
            {
                payment.Allocations.Add(new PaymentAllocation(order.Id, take));
                remaining -= take;
            }
        }

        _payments[payment.Id] = payment;
        return Json(201, new PaystackInitResult(payment.Id, reference, $"https://checkout.paystack.example/{reference}", "mock-access-code"), Ctx.PaystackInitResult);
    }
}
