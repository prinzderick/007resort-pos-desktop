using System.Net;
using System.Text.Json.Nodes;
using R007.Pos.Core.Api;
using R007.Pos.Devices.Printing;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>
/// Reception is PAY_FIRST. Sports flow: booking hold -> order (slot fee + rentals) -> POST /bookings/{id}/order -> pay -> QR entitlement,
/// and the QR token read back for the printed receipt. Pool tickets: one QR per individual ticket.
/// </summary>
public static class ReceptionFlow
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("reception-booking", "Tennis court hold -> slot fee + 2 rackets order -> attach -> pay cash -> QR receipt; QR verified at the gate", Booking),
        new("reception-race", "Losing the race for a slot: 409 slot_unavailable is explained, slots refresh; cancelled hold frees the slot", Race),
        new("reception-pool-tickets", "3 adult + 2 child pool tickets, cash: five QR entitlements, five slips", PoolTickets),
    ];

    private static async Task<PosRig> ReceptionAsync(NodeContext node)
    {
        var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        await rig.SignInAsync("S-0005");
        await rig.FreshCashSessionAsync(5000m);
        return rig;
    }

    /// <summary>Moves the screen to a random future day so repeated runs never collide with earlier bookings.</summary>
    private static async Task<ReceptionViewModel> PickSlotAsync(PosRig rig, string resourceName, Random random)
    {
        var reception = new ReceptionViewModel(rig.Ctx, rig.Navigator);
        await reception.ActivateAsync();
        var resource = reception.Resources.FirstOrDefault(r => r.Name == resourceName) ?? throw new ScenarioFailure($"No bookable resource {resourceName}");
        reception.Resource = resource;
        for (var i = 0; i < 3 + random.Next(30); i++)
        {
            await reception.NextDayCommand.ExecuteAsync();
        }

        await reception.LoadSlotsCommand.ExecuteAsync();
        var free = reception.Slots.Where(s => s.CanBook).ToList();
        Check.True(free.Count > 0, "the resource has free slots on the chosen day");
        reception.Slot = free[random.Next(free.Count)];
        return reception;
    }

    private static async Task Booking(NodeContext node)
    {
        using var rig = await ReceptionAsync(node);
        var reception = await PickSlotAsync(rig, "Lawn Tennis Court 1", new Random());
        reception.CustomerName = "Mr Adebayo";
        var slot = reception.Slot!.Slot;

        await reception.HoldCommand.ExecuteAsync();
        Check.True(reception.Error is null, "hold: " + reception.Error);
        Check.True(reception.HasBooking, "booking held");
        var booking = reception.Booking!;
        Check.Equal(BookingStatuses.PendingPayment, booking.Status, "booking waits for payment once its order is attached");
        Check.True(booking.OrderId is not null, "attached order id");
        var orderId = booking.OrderId!.Value;
        Check.Contains(reception.BookingText, "5,000.00 to pay", "slot fee from the node");

        var order = await rig.Api.GetOrderAsync(orderId);
        Check.Equal(OrderStatuses.Draft, order.Status, "the paying order is a draft (PAY_FIRST)");
        Check.Equal(rig.Product("FEE-TENNIS").Id, order.Lines.Single().ProductId, "the slot-fee product is the resource's product");

        // rentals are further lines on the same order
        var racket = reception.Rentals.First(r => r.Name.Contains("racket", StringComparison.OrdinalIgnoreCase));
        await reception.AddRentalCommand.ExecuteAsync(racket);
        await reception.AddRentalCommand.ExecuteAsync(racket);
        Check.True(reception.Error is null, "rentals: " + reception.Error);
        Check.Contains(reception.BookingText, "8,000.00 to pay", "5000 slot + 2 x 1500 rackets, priced by the node");

        // pay: cash with change through the payment dialog; PAY_FIRST lets a draft order be paid
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "8000.00", "10000.00", null));
        await reception.PayCommand.ExecuteAsync();
        Check.True(reception.Error is null, "pay: " + reception.Error);
        Check.True(!reception.HasBooking, "screen reset after payment");
        Check.Contains(reception.Info ?? string.Empty, "Booking confirmed", "info");

        var confirmed = await rig.Api.GetBookingAsync(booking.Id);
        Check.Equal(BookingStatuses.Confirmed, confirmed.Status, "booking CONFIRMED by the payment");
        Check.True(confirmed.EntitlementId is not null, "entitlement issued");
        var entitlement = await rig.Api.GetEntitlementAsync(confirmed.EntitlementId!.Value);
        Check.True(entitlement.QrToken.StartsWith("R7.", StringComparison.Ordinal), "QR token format R7.<id>.<sig>");
        Check.Equal(3, entitlement.Items.Count, "one ACCESS item and one RENTAL item per rental line (2 lines of 1 racket)");
        Check.True(entitlement.Items.Any(i => i.Kind == "ACCESS" && i.Name.Contains("Tennis", StringComparison.Ordinal)), "access to the court");
        Check.Equal(2, entitlement.Items.Where(i => i.Kind == "RENTAL").Sum(i => i.Quantity), "2 rackets");

        // the printed receipt: API receipt + the QR from the entitlement (the receipt's own qrPayload is null for bookings)
        var doc = rig.Printer.PrintedDocuments.Last();
        Check.Equal(entitlement.QrToken, doc.QrData!, "the receipt carries the entitlement QR token");
        Check.True(doc.OpenDrawer, "cash drawer");
        Check.True(doc.Lines.Any(l => l.Text.Contains("Change", StringComparison.Ordinal) && l.Text.Contains("2,000.00", StringComparison.Ordinal)), "receipt shows the 2000.00 change");
        var text = EscPosRenderer.RenderText(doc);
        Check.Contains(text, "[QR: " + entitlement.QrToken + "]", "text preview shows the QR payload");
        var bytes = EscPosRenderer.Render(doc);
        Check.True(ContainsAscii(bytes, entitlement.QrToken), "ESC/POS bytes carry the QR data");
        node.Log("QR " + entitlement.QrToken);

        // the gate reads the same token (ticket.view): known, ACTIVE, items match
        var gate = (await rig.Node.Raw.GetAsync($"entitlement-tokens/{entitlement.QrToken}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("gate lookup").Json;
        Check.Equal(entitlement.Id.ToString("D"), gate["id"]!.GetValue<string>(), "gate resolves the token to the entitlement");

        // the slot is gone for the next customer
        await reception.LoadSlotsCommand.ExecuteAsync();
        Check.True(reception.Slots.Any(s => s.Slot.Start == slot.Start && !s.CanBook), "the booked slot shows as full");
    }

    private static async Task Race(NodeContext node)
    {
        using var rig = await ReceptionAsync(node);
        var reception = await PickSlotAsync(rig, "Lawn Tennis Court 2", new Random());
        var slot = reception.Slot!.Slot;
        var resource = reception.Resource!;

        // someone else (a second terminal) takes the slot first
        using var other = await PosRig.EnrolAsync(node, "RECEPTION");
        await other.SignInAsync("S-0005");
        var theirs = await other.Api.HoldBookingAsync(new HoldRequest(resource.Id, slot.Start, slot.End, null, new CustomerInput("Other guest")), IdempotencyKeys.New());

        await reception.HoldCommand.ExecuteAsync();
        Check.True(!reception.HasBooking, "no booking: the slot was taken");
        Check.Contains(reception.Error ?? string.Empty, "just taken", "the cashier is told why");
        Check.True(!reception.Slots.First(s => s.Slot.Start == slot.Start).CanBook, "slots refreshed: the slot now shows as unavailable");

        // releasing the other hold frees the slot again
        await other.Api.CancelBookingAsync(theirs.Id, theirs.RowVersion, new CancelBookingRequest("test"), IdempotencyKeys.New());
        await reception.LoadSlotsCommand.ExecuteAsync();
        reception.Slot = reception.Slots.First(s => s.Slot.Start == slot.Start);
        Check.True(reception.Slot.CanBook, "freed after cancel");
        await reception.HoldCommand.ExecuteAsync();
        Check.True(reception.HasBooking, "now the hold succeeds: " + reception.Error);

        // cashier cancels their own hold (no payment) and the slot is free again
        await reception.CancelHoldCommand.ExecuteAsync();
        Check.True(!reception.HasBooking, "hold cancelled");
        await reception.LoadSlotsCommand.ExecuteAsync();
        Check.True(reception.Slots.First(s => s.Slot.Start == slot.Start).CanBook, "slot free after the cashier's cancel");
    }

    private static async Task PoolTickets(NodeContext node)
    {
        using var rig = await ReceptionAsync(node);
        var sell = rig.NewSell();
        var adult = rig.Product("POOL-ADULT");
        var child = rig.Product("POOL-CHILD");
        for (var i = 0; i < 3; i++)
        {
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(adult));
        }

        for (var i = 0; i < 2; i++)
        {
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(child));
        }

        Check.Equal(3 * 3000m + 2 * 1500m, sell.Order!.Total!.Value, "server total for 3 adults + 2 children");
        var orderId = sell.Order.Id;

        var before = rig.Printer.PrintedDocuments.Count;
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "12000.00", "15000.00", null));
        await sell.PayCommand.ExecuteAsync();
        Check.True(sell.Error is null, "pay: " + sell.Error);

        var entitlements = await rig.Api.ListEntitlementsAsync(orderId);
        Check.Equal(5, entitlements.Count, "individual tickets: one entitlement per ticket");
        Check.Equal(5, entitlements.Select(e => e.QrToken).Distinct().Count(), "five distinct QR tokens");
        Check.True(entitlements.All(e => e.Status == "ACTIVE" && e.Items.Single().Kind == "ACCESS"), "each is an ACTIVE single ACCESS item");

        var printed = rig.Printer.PrintedDocuments.Skip(before).ToList();
        Check.Equal(6, printed.Count, "one receipt + five QR slips");
        Check.True(printed[0].QrData is null, "with several tickets the receipt itself carries no single QR");
        var slipTokens = printed.Skip(1).Select(d => d.QrData!).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Check.True(slipTokens.SequenceEqual(entitlements.Select(e => e.QrToken).OrderBy(t => t, StringComparer.Ordinal)), "each slip prints one entitlement's QR");

        // POST /entitlements is idempotent per order and reports the group
        var again = await rig.Api.IssueEntitlementAsync(new IssueEntitlementRequest(orderId), IdempotencyKeys.New());
        Check.Equal(5, again.GroupEntitlementIds?.Count ?? 0, "issue is idempotent: same five, listed as the group");
    }

    private static bool ContainsAscii(byte[] haystack, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
