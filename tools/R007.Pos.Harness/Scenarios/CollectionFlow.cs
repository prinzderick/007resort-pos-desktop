using System.Net;
using System.Text.Json.Nodes;
using R007.Pos.Core.Api;
using R007.Pos.Core.Realtime;
using R007.Pos.ViewModels;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Harness;

/// <summary>
/// The waiter-collection flow against the REAL node: the waiter (wait1 on a checked-out tablet, played through raw HTTP) bills and collects by card machine /
/// cash; the POS (cashier2 / supervisor1 on the RESTAURANT terminal) shows the collection, confirms or rejects it, settles the order, receives cash handovers.
/// </summary>
public static class CollectionFlow
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("collect-card-confirm", "Cashier prints the pre-bill; waiter collects by card machine; inbox + chips + realtime; direct pay blocked; confirm settles + receipt; duplicate confirm idempotent", CardConfirm),
        new("collect-reject", "Waiter collection rejected by the cashier (reason required, alert); bill stays unpaid; waiter collects again and it is confirmed", Reject),
        new("collect-cash-handover", "Waiter cash (policy ALLOW): confirm, handover, receive with a big variance -> supervisor sign-off; small variance received at once", CashHandover),
        new("collect-permissions", "Waiter cannot confirm / reject / mark paid; unchecked tablet and cash policy refused; other-facility cashier refused; waiter POS has no desk", Permissions),
    ];

    private static async Task<PosRig> CashierAsync(NodeContext node, string staff = "S-0006", string facility = "RESTAURANT", bool cashSession = true)
    {
        var rig = await PosRig.EnrolAsync(node, facility);
        await rig.SignInAsync(staff);
        if (cashSession)
        {
            await rig.FreshCashSessionAsync(2000m);
        }

        return rig;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, string what, int seconds = 12)
        where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe().ConfigureAwait(false) is { } hit)
            {
                return hit;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new ScenarioFailure($"Timed out waiting for {what}.");
    }

    private static string Money(string wire) => R007.Pos.Core.Money.MoneyFormat.Display(R007.Pos.Core.Money.MoneyFormat.ParseWire(wire));

    private static async Task<CollectionRow> RowAsync(CollectionsInboxViewModel inbox, Guid paymentId)
    {
        await inbox.ActivateAsync().ConfigureAwait(false);
        return inbox.Items.FirstOrDefault(r => r.PaymentId == paymentId) ?? throw new ScenarioFailure($"payment {paymentId} is not in the 'Collected by waiters' list ({inbox.Items.Count} rows, error: {inbox.Error})");
    }

    private static async Task<JsonNode> OrderAsync(NodeContext node, PosRig rig, string orderId) =>
        (await node.Raw.GetAsync($"orders/{orderId}", rig.Auth.AccessToken, rig.DeviceToken).ConfigureAwait(false)).Ok("read order").Json;

    private static Task<RawResponse> AsCashier(PosRig rig, string path, JsonObject body, string? key = null) =>
        rig.Node.Raw.PostAsync(path, body, rig.Auth.AccessToken, rig.DeviceToken, key);

    /// <summary>Listens to the node's Reverb feed exactly like the app does (device channel + the facility's orders channel).</summary>
    private sealed class RealtimeProbe : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<RealtimeEvent> _events = [];
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _run = Task.CompletedTask;

        public static async Task<RealtimeProbe> StartAsync(PosRig rig)
        {
            var probe = new RealtimeProbe();
            rig.Ctx.Realtime.EventReceived += ev =>
            {
                lock (probe._events)
                {
                    probe._events.Add(ev);
                }
            };
            rig.Ctx.Realtime.Subscribed += () => probe._subscribed.TrySetResult();
            var info = (await rig.Ctx.CheckServerAsync().ConfigureAwait(false)).Info?.Realtime ?? throw new ScenarioFailure("the node did not advertise Reverb");
            probe._run = rig.Ctx.Realtime.RunAsync(info, rig.Ctx.Identity!.DeviceId, probe._cts.Token, [$"private-facility.{rig.Ctx.FacilityId:D}.orders"]);
            var winner = await Task.WhenAny(probe._subscribed.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
            if (winner != probe._subscribed.Task)
            {
                throw new ScenarioFailure("the realtime client did not subscribe within 15 s");
            }

            await Task.Delay(1500).ConfigureAwait(false); // the facility channel is subscribed right after the device channel
            return probe;
        }

        public async Task<RealtimeEvent> WaitAsync(string name, string? containing = null) =>
            await EventuallyAsync(() =>
            {
                lock (_events)
                {
                    return Task.FromResult(_events.FirstOrDefault(e => e.Name == name && (containing is null || e.Data.ToString().Contains(containing, StringComparison.Ordinal))));
                }
            }, $"realtime event {name}").ConfigureAwait(false);

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _run.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // cancelled
            }
        }
    }

    private static async Task CardConfirm(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        var f = rig.Ctx.Features;
        Check.True(f.CanPrintBill && f.CanConfirmCollections && f.ShowCollections, "cashier2 sees the pre-bill action and the collections desk (bill.print + payment.confirm)");
        var main = new MainViewModel(rig.Ctx, rig.Navigator);
        main.BuildItems();
        Check.True(main.Items.Any(i => i.Title == "Collected by waiters"), "the 'Collected by waiters' tab exists");
        using var realtime = await RealtimeProbe.StartAsync(rig);
        var waiter = await WaiterTablet.StartAsync(node, "RESTAURANT");

        // The cashier takes a table order to SERVED and prints the pre-bill from the Sell screen.
        var table = await Janitor.FreeTableAsync(rig);
        var tables = new TablesViewModel(rig.Ctx, rig.Navigator);
        SellTarget? target = null;
        tables.TargetChosen += (_, t) => target = t;
        await tables.ActivateAsync();
        await tables.SelectTableCommand.ExecuteAsync(tables.Tables.First(t => t.Table.Id == table.Id));
        var sell = rig.NewSell();
        await sell.SetTargetAsync(Check.NotNull(target, "table chosen"));
        var snack = rig.Product("FD-CHINCHIN"); // no prep route: goes straight to SENT/SERVED
        for (var i = 0; i < 3; i++)
        {
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(snack));
        }

        Check.Equal(2400m, sell.Order!.Total!.Value, "server total 3 x 800");
        Check.True(!sell.CanPrintBill, "a draft order has no bill yet (pay-after-service)");
        await sell.SendCommand.ExecuteAsync();
        await sell.ServeCommand.ExecuteAsync();
        Check.Equal(OrderStatuses.Served, sell.Order!.Status, "served: " + sell.Error);
        Check.True(sell.CanPrintBill, "served: the bill can be printed");
        var orderId = sell.Order.Id.ToString("D");

        await sell.PrintBillCommand.ExecuteAsync();
        Check.True(sell.Error is null, "bill printed: " + sell.Error);
        Check.True(sell.IsBilled && sell.BillText == "Bill printed x1", "order is billed and frozen: " + sell.BillText);
        var billDoc = Check.NotNull(rig.Printer.PrintedDocuments.LastOrDefault(), "pre-bill printed");
        var billText = string.Join("\n", billDoc.Lines.Select(l => l.Text));
        Check.Contains(billText, "NOT A RECEIPT", "pre-bill banner");
        Check.Contains(billText, $"Table: {table.Label}", "pre-bill table line");
        Check.Contains(billText, "2,400.00", "pre-bill total");
        Check.True(billDoc.Lines.All(l => l.Text.Length <= 48), "pre-bill fits 80mm");
        var raw = await OrderAsync(node, rig, orderId);
        Check.Equal("BILL_PRINTED", raw["billState"]!.GetValue<string>(), "node billState");
        Check.Equal("SERVED", raw["status"]!.GetValue<string>(), "order.status is unchanged by billing");
        var frozen = await Check.ThrowsApiAsync(() => rig.Api.AddLineAsync(sell.Order.Id, sell.Order.RowVersion, new OrderLineInput(snack.Id, 1), IdempotencyKeys.New()), "line on a billed order");
        Check.Equal("order_billed", frozen.Code, "billed order is frozen");
        await sell.PrintBillCommand.ExecuteAsync();
        Check.Equal("Bill printed x2", sell.BillText, "reprint counter");
        Check.Contains(string.Join("\n", rig.Printer.PrintedDocuments.Last().Lines.Select(l => l.Text)), "REPRINT #2", "reprint is marked");

        await tables.ActivateAsync();
        var tile = tables.Tables.First(t => t.Table.Id == table.Id);
        Check.Equal(OrderStateChips.BillPrinted + " | " + OrderStateChips.AwaitingPayment, tile.ChipsText, "table tile chips after the bill");

        // The waiter collects by card machine (a repeat with the same client id + key is a replay, not a second collection).
        var approval = WaiterTablet.Unique("AP");
        var slip = WaiterTablet.Unique("SL");
        var clientId = Guid.CreateVersion7();
        var key = Guid.CreateVersion7().ToString("D");
        var collected = (await waiter.CollectCardAsync(orderId, "2400.0000", approval, slip, clientId, key)).Expect(HttpStatusCode.Created, "waiter collects by card machine");
        var paymentId = Guid.Parse(collected.Json["payment"]!["id"]!.GetValue<string>());
        Check.Equal("PENDING_CONFIRMATION", collected.Json["payment"]!["status"]!.GetValue<string>(), "collection is pending");
        var replay = await waiter.CollectCardAsync(orderId, "2400.0000", approval, slip, clientId, key);
        Check.True(replay.Code is 200 or 201, $"replay of the same collection id: HTTP {replay.Code} {replay.Text}");
        Check.Equal(paymentId.ToString("D"), replay.Json["payment"]!["id"]!.GetValue<string>(), "replay returns the same payment");
        Check.Equal("0.0000", (await OrderAsync(node, rig, orderId))["amountPaid"]!.GetValue<string>(), "nothing is paid until the cashier confirms");

        var pushed = await realtime.WaitAsync("payment.collected", paymentId.ToString("D"));
        node.Log($"realtime payment.collected arrived on {pushed.Channel}");

        // The cashier's inbox
        var inbox = new CollectionsInboxViewModel(rig.Ctx, rig.Navigator);
        var row = await RowAsync(inbox, paymentId);
        Check.Equal(Money("2400.0000"), row.AmountText, "amount");
        Check.Equal("Card machine", row.TenderText, "tender");
        Check.Contains(row.ReferenceText, approval, "approval code shown");
        Check.Contains(row.ReferenceText, slip, "slip reference shown");
        Check.Equal($"Table {table.Label}", row.TableText, "table shown");
        Check.Contains(row.OrderText, raw["number"]!.GetValue<string>(), "order number shown");
        Check.Equal(AgeLevel.Normal, row.Level, "fresh collection is not near expiry");
        if (row.Info?.CollectedByName is null)
        {
            node.Log("API DEVIATION: payment.collection has no collectedByName/orderNumber/tableLabel/terminalLabel; the POS resolves them itself (fix: api PR 'payment collection display fields').");
        }

        node.Log($"row: {row.TableText} | {row.OrderText} | {row.WaiterText} | {row.TenderText} {row.AmountText} | {row.ReferenceText} | {row.AgeText}");
        await main.RefreshBadgesAsync();
        Check.True(main.CollectionsBadgeText is not null && int.Parse(main.CollectionsBadgeText) >= 1, "shell badge counts pending collections: " + main.CollectionsBadgeText);

        // chips + direct pay is blocked gracefully
        await tables.ActivateAsync();
        Check.Equal(OrderStateChips.BillPrinted + " | " + OrderStateChips.CollectedAwaitingConfirmation, tables.Tables.First(t => t.Table.Id == table.Id).ChipsText, "table tile chips after the collection");
        await sell.RefreshCommand.ExecuteAsync();
        Check.True(!sell.CanPayOrder, "the cashier cannot take payment on a bill the waiter fully collected");
        Check.Contains(sell.StatusBanner, "COLLECTED BY WAITER", "explains why");
        var dup = await Check.ThrowsApiAsync(() => rig.Api.CreatePaymentAsync(new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(sell.Order.Id, 2400m)], [new TenderInput(TenderTypes.Cash, 2400m, null, 2400m, ClientIds.New())], rig.Ctx.CashSession!.Id), IdempotencyKeys.New()), "cashier double-collection");
        Check.Equal(HttpStatusCode.Conflict, dup.Status, "double collection is a 409");
        Check.Equal("pending_collection_exists", dup.Code, "double collection code");
        Check.Contains(ScreenViewModel.Describe(dup), "confirm or reject it", "operator message");

        // Confirm with the matching reference
        var printedBefore = rig.Printer.PrintedDocuments.Count;
        rig.Navigator.Script = async m =>
        {
            var confirm = (ConfirmCollectionViewModel)m;
            confirm.ReferenceInput = approval;
            Check.True(!confirm.ReferenceMismatch, "matching reference");
            await confirm.ConfirmCommand.ExecuteAsync();
            Check.True(confirm.Confirmed, "confirmed: " + confirm.Error);
        };
        await row.ConfirmCommand.ExecuteAsync();
        Check.True(inbox.Items.All(r => r.PaymentId != paymentId), "confirmed collection left the inbox");
        var settled = await OrderAsync(node, rig, orderId);
        Check.Equal("SETTLED", settled["status"]!.GetValue<string>(), "order settled by the confirmation");
        Check.Equal("0.0000", settled["balanceDue"]!.GetValue<string>(), "no balance");
        var payment = (await node.Raw.GetAsync($"payments/{paymentId:D}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("payment").Json;
        Check.Equal("CAPTURED", payment["status"]!.GetValue<string>(), "payment captured");
        Check.Equal(approval, payment["collection"]!["matchedReference"]!.GetValue<string>(), "matched reference recorded");
        Check.True(rig.Printer.PrintedDocuments.Count == printedBefore + 1, "the receipt was printed");
        Check.Contains(string.Join("\n", rig.Printer.PrintedDocuments.Last().Lines.Select(l => l.Text)), "2,400.00", "receipt total");
        await realtime.WaitAsync("payment.confirmed", paymentId.ToString("D"));

        // Duplicate confirm: idempotent; reject-after-confirm refused
        var again = (await AsCashier(rig, $"payments/{paymentId:D}/confirm", new JsonObject())).Expect(HttpStatusCode.OK, "second confirm");
        Check.Equal(payment["receiptId"]!.GetValue<string>(), again.Json["receiptId"]!.GetValue<string>(), "same receipt, no second capture");
        var late = (await AsCashier(rig, $"payments/{paymentId:D}/reject", new JsonObject { ["reason"] = "too late" })).Expect(HttpStatusCode.Conflict, "reject after confirm");
        Check.Equal("payment_state_invalid", late.ProblemCode!, "reject-after-confirm code");
        Check.Equal("0.0000", (await OrderAsync(node, rig, orderId))["balanceDue"]!.GetValue<string>(), "still paid once");
    }

    private static async Task Reject(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        using var realtime = await RealtimeProbe.StartAsync(rig);
        var waiter = await WaiterTablet.StartAsync(node, "RESTAURANT");
        var snack = rig.Product("FD-CHINCHIN");
        var order = await waiter.ServedOrderAsync(snack.Id, 2); // 1600
        var orderId = order["id"]!.GetValue<string>();
        await waiter.PrintBillAsync(orderId);

        var approval = WaiterTablet.Unique("AP");
        var collected = (await waiter.CollectCardAsync(orderId, "1600.0000", approval, WaiterTablet.Unique("SL"))).Expect(HttpStatusCode.Created, "waiter collects");
        var paymentId = Guid.Parse(collected.Json["payment"]!["id"]!.GetValue<string>());
        var inbox = new CollectionsInboxViewModel(rig.Ctx, rig.Navigator);
        var row = await RowAsync(inbox, paymentId);
        Check.Equal("No table", row.TableText, "takeaway has no table");

        // A waiter-side cancel of a manual collection is not allowed, and a reason is mandatory for the cashier
        rig.Navigator.Script = async m =>
        {
            var reject = (RejectCollectionViewModel)m;
            Check.Contains(reject.WarningText, "alerts a supervisor", "warning");
            await reject.RejectCommand.ExecuteAsync();
            Check.True(reject.Error is not null && reject.Error.Contains("reason", StringComparison.OrdinalIgnoreCase), "a reason is required: " + reject.Error);
            reject.Reason = "Slip amount does not match";
            await reject.RejectCommand.ExecuteAsync();
            Check.True(reject.Rejected, "rejected: " + reject.Error);
        };
        await row.RejectCommand.ExecuteAsync();
        Check.True(inbox.Items.All(r => r.PaymentId != paymentId), "rejected collection left the inbox");
        var payment = (await node.Raw.GetAsync($"payments/{paymentId:D}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("payment").Json;
        Check.Equal("REJECTED", payment["status"]!.GetValue<string>(), "payment rejected");
        Check.Equal("Slip amount does not match", payment["collection"]!["decisionReason"]!.GetValue<string>(), "reason stored");
        var after = await OrderAsync(node, rig, orderId);
        Check.Equal("0.0000", after["amountPaid"]!.GetValue<string>(), "bill still unpaid");
        Check.True(after["awaitingPayment"]!.GetValue<bool>() && after["pendingCollected"]!.GetValue<string>() == "0.0000", "awaiting payment, nothing pending");
        await realtime.WaitAsync("payment.rejected", paymentId.ToString("D"));
        var alert = await realtime.WaitAsync("payment.alert");
        node.Log("supervisor alert pushed: " + alert.Data);

        // Reject is idempotent; confirming a rejected collection is refused with an explanation
        (await AsCashier(rig, $"payments/{paymentId:D}/reject", new JsonObject { ["reason"] = "Slip amount does not match" })).Ok("second reject");
        var stale = await Check.ThrowsApiAsync(() => rig.Api.ConfirmCollectionAsync(paymentId, new ConfirmCollectionRequest(), IdempotencyKeys.New()), "confirm a rejected collection");
        Check.Equal("payment_state_invalid", stale.Code, "confirm-after-reject code");

        // The waiter collects again (new slip) and the cashier confirms it
        var second = (await waiter.CollectCardAsync(orderId, "1600.0000", WaiterTablet.Unique("AP"), WaiterTablet.Unique("SL"))).Expect(HttpStatusCode.Created, "waiter collects again");
        var secondId = Guid.Parse(second.Json["payment"]!["id"]!.GetValue<string>());
        var row2 = await RowAsync(inbox, secondId);
        rig.Navigator.Script = async m => await ((ConfirmCollectionViewModel)m).ConfirmCommand.ExecuteAsync();
        await row2.ConfirmCommand.ExecuteAsync();
        Check.Equal("SETTLED", (await OrderAsync(node, rig, orderId))["status"]!.GetValue<string>(), "second collection settles the order");
    }

    private static async Task CashHandover(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        using var supervisor = await CashierAsync(node, "S-0008", cashSession: false);
        var waiter = await WaiterTablet.StartAsync(node, "RESTAURANT");
        var owner = await node.OwnerAsync();
        var policy = $"staff/{waiter.StaffId:D}/collection-policy";
        (await node.Raw.SendAsync(HttpMethod.Patch, policy, new JsonObject { ["cashHolding"] = "ALLOW" }, owner)).Ok("allow wait1 to hold cash");
        try
        {
            var snack = rig.Product("FD-CHINCHIN");
            var before = decimal.Parse((await waiter.GetAsync($"staff/{waiter.StaffId:D}/cash-in-hand")).Ok("cash in hand").Json["cashInHand"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            var inbox = new CollectionsInboxViewModel(rig.Ctx, rig.Navigator);

            // two cash collections: 1600 (handed over as 2000, change 400) and 900
            async Task<Guid> CollectAndConfirmAsync(int quantity, string amount, string tendered)
            {
                var order = await waiter.ServedOrderAsync(snack.Id, quantity);
                var orderId = order["id"]!.GetValue<string>();
                await waiter.PrintBillAsync(orderId);
                var res = (await waiter.CollectCashAsync(orderId, amount, tendered)).Expect(HttpStatusCode.Created, "waiter collects cash");
                var id = Guid.Parse(res.Json["payment"]!["id"]!.GetValue<string>());
                var row = await RowAsync(inbox, id);
                Check.Equal("Cash", row.TenderText, "cash tender");
                rig.Navigator.Script = async m =>
                {
                    var confirm = (ConfirmCollectionViewModel)m;
                    Check.True(confirm.IsCash, "cash confirmation");
                    await confirm.ConfirmCommand.ExecuteAsync();
                    Check.True(confirm.Confirmed, "cash confirmed: " + confirm.Error);
                };
                await row.ConfirmCommand.ExecuteAsync();
                Check.Equal("SETTLED", (await OrderAsync(node, rig, orderId))["status"]!.GetValue<string>(), "cash confirmation settles the order");
                return id;
            }

            await CollectAndConfirmAsync(2, "1600.0000", "2000.0000");
            var afterFirst = decimal.Parse((await waiter.GetAsync($"staff/{waiter.StaffId:D}/cash-in-hand")).Ok("cash in hand").Json["cashInHand"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            Check.Equal(before + 1600m, afterFirst, "waiter cash in hand grew by the amount collected (not the tendered)");

            // Handover 1: declared 1600, counted 600 -> variance -1000 (> limit) -> supervisor sign-off
            var declared = (await waiter.DeclareHandoverAsync("1600.0000")).Expect(HttpStatusCode.Created, "waiter declares the handover");
            var handoverId = Guid.Parse(declared.Json["id"]!.GetValue<string>());
            var desk = new CashHandoverDeskViewModel(rig.Ctx, rig.Navigator);
            await desk.ActivateAsync();
            var handoverRow = desk.Handovers.FirstOrDefault(h => h.Handover.Id == handoverId) ?? throw new ScenarioFailure("declared handover is not on the desk: " + desk.Error);
            Check.Equal("Waiting to be counted", handoverRow.StatusText, "handover state");
            node.Log($"desk: {handoverRow.WaiterText} {handoverRow.DeclaredText}; {desk.Holdings.Count} waiter(s) holding cash");
            Check.True(desk.Holdings.Count >= 1, "the waiter shows in 'waiters holding cash'");
            rig.Navigator.Script = async m =>
            {
                var receive = (ReceiveHandoverViewModel)m;
                receive.CountedText = "600.00";
                Check.Equal("SHORT by ₦1,000.00", receive.VariancePreview, "variance preview");
                await receive.ReceiveCommand.ExecuteAsync();
                Check.True(receive.Error is null, "received: " + receive.Error);
            };
            await handoverRow.ReceiveCommand.ExecuteAsync();
            var pending = desk.Handovers.First(h => h.Handover.Id == handoverId);
            Check.Equal(HandoverStatuses.PendingSignoff, pending.Handover.Status, "over the variance limit waits for sign-off");
            Check.Equal(-1000m, pending.Handover.Variance!.Value, "variance recorded");
            Check.Equal("SHORT ₦1,000.00", pending.VarianceText, "variance shown");
            Check.True(!pending.SignoffCommand.CanExecute(null) && !desk.CanSignoff, "a cashier cannot sign off");
            var twice = await Check.ThrowsApiAsync(() => rig.Api.ReceiveCashHandoverAsync(handoverId, new ReceiveHandoverRequest(1600m), IdempotencyKeys.New()), "receive twice");
            Check.Equal("already_received", twice.Code, "receive twice code");
            var receivedByCashier = (await node.Raw.GetAsync($"cash-handovers/{handoverId:D}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("handover").Json;
            Check.Equal("PENDING_SIGNOFF", receivedByCashier["status"]!.GetValue<string>(), "node status");
            var signoffByReceiver = await Check.ThrowsApiAsync(() => rig.Api.SignoffCashHandoverAsync(handoverId, new SignoffHandoverRequest("me"), IdempotencyKeys.New()), "cashier sign-off");
            Check.True(signoffByReceiver.IsPermissionDenied || signoffByReceiver.Code == "self_signoff_forbidden", "cashier sign-off refused: " + signoffByReceiver.Code);

            // The supervisor signs it off on their own POS
            var supDesk = new CashHandoverDeskViewModel(supervisor.Ctx, supervisor.Navigator);
            await supDesk.ActivateAsync();
            var supRow = supDesk.Handovers.First(h => h.Handover.Id == handoverId);
            Check.True(supDesk.CanSignoff && supRow.SignoffCommand.CanExecute(null), "supervisor can sign off");
            supDesk.Note = "Waiter accepted the shortfall";
            await supRow.SignoffCommand.ExecuteAsync();
            Check.True(supDesk.Error is null, "signed off: " + supDesk.Error);
            var signed = (await node.Raw.GetAsync($"cash-handovers/{handoverId:D}", supervisor.Auth.AccessToken, supervisor.DeviceToken)).Ok("handover").Json;
            Check.Equal("SIGNED_OFF", signed["status"]!.GetValue<string>(), "handover signed off");

            // Handover 2: small variance is received at once
            await CollectAndConfirmAsync(1, "800.0000", "800.0000");
            var second = (await waiter.DeclareHandoverAsync("800.0000")).Expect(HttpStatusCode.Created, "second handover");
            var secondId = Guid.Parse(second.Json["id"]!.GetValue<string>());
            await desk.ActivateAsync();
            rig.Navigator.Script = async m =>
            {
                var receive = (ReceiveHandoverViewModel)m;
                receive.CountedText = "700.00";
                await receive.ReceiveCommand.ExecuteAsync();
                Check.True(receive.Error is null, "received: " + receive.Error);
            };
            await desk.Handovers.First(h => h.Handover.Id == secondId).ReceiveCommand.ExecuteAsync();
            var small = (await node.Raw.GetAsync($"cash-handovers/{secondId:D}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("handover").Json;
            Check.Equal("RECEIVED", small["status"]!.GetValue<string>(), "a variance within the limit is received at once");
            Check.Equal("-100.0000", small["variance"]!.GetValue<string>(), "variance");
            var end = decimal.Parse((await waiter.GetAsync($"staff/{waiter.StaffId:D}/cash-in-hand")).Ok("cash in hand").Json["cashInHand"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            Check.Equal(before, end, "the waiter is back to the cash they held before (everything handed over)");
        }
        finally
        {
            await node.Raw.SendAsync(HttpMethod.Patch, policy, new JsonObject { ["cashHolding"] = "INHERIT" }, owner).ConfigureAwait(false);
        }
    }

    private static async Task Permissions(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        var waiter = await WaiterTablet.StartAsync(node, "RESTAURANT");
        var snack = rig.Product("FD-CHINCHIN");
        var order = await waiter.ServedOrderAsync(snack.Id, 1);
        var orderId = order["id"]!.GetValue<string>();
        await waiter.PrintBillAsync(orderId);
        var collected = (await waiter.CollectCardAsync(orderId, "800.0000", WaiterTablet.Unique("AP"), WaiterTablet.Unique("SL"))).Expect(HttpStatusCode.Created, "waiter collects");
        var paymentId = collected.Json["payment"]!["id"]!.GetValue<string>();

        // The waiter can never mark a bill paid, confirm or reject
        var confirm = await waiter.PostAsync($"payments/{paymentId}/confirm");
        Check.Equal(HttpStatusCode.Forbidden, confirm.Status, "waiter confirm");
        Check.Equal("permission_denied", confirm.ProblemCode!, "waiter confirm code");
        Check.Equal(HttpStatusCode.Forbidden, (await waiter.PostAsync($"payments/{paymentId}/reject", new JsonObject { ["reason"] = "nope nope" })).Status, "waiter reject");
        var pay = await waiter.PostAsync("payments", new JsonObject
        {
            ["facilityId"] = waiter.FacilityId.ToString("D"),
            ["allocations"] = new JsonArray(new JsonObject { ["orderId"] = orderId, ["amount"] = "800.0000" }),
            ["tenders"] = new JsonArray(new JsonObject { ["tenderType"] = "CASH", ["amount"] = "800.0000", ["tendered"] = "800.0000" }),
        });
        Check.Equal(HttpStatusCode.Forbidden, pay.Status, "waiter marking a bill paid (POST /payments)");

        // Cash holding is off by default; an un-checked-out tablet cannot collect at all
        var open = await waiter.ServedOrderAsync(snack.Id, 1);
        var openId = open["id"]!.GetValue<string>();
        await waiter.PrintBillAsync(openId);
        var cash = await waiter.CollectCashAsync(openId, "100.0000", "100.0000");
        Check.Equal(HttpStatusCode.Forbidden, cash.Status, "waiter cash");
        Check.Equal("cash_holding_not_allowed", cash.ProblemCode!, "cash policy code");
        var loose = await WaiterTablet.StartAsync(node, "RESTAURANT", checkOut: false);
        var unchecked_ = await loose.CollectCardAsync(openId, "100.0000", WaiterTablet.Unique("AP"), WaiterTablet.Unique("SL"));
        Check.Equal(HttpStatusCode.Forbidden, unchecked_.Status, "tablet not checked out");
        Check.Equal("device_not_checked_out", unchecked_.ProblemCode!, "checkout code");

        // A cashier of another facility (cashier1, RECEPTION) cannot decide this collection
        using var reception = await CashierAsync(node, "S-0005", "RECEPTION", cashSession: false);
        var elsewhere = await reception.Node.Raw.PostAsync($"payments/{paymentId}/confirm", new JsonObject(), reception.Auth.AccessToken, reception.DeviceToken);
        Check.True(elsewhere.Code is 403 or 404, $"a cashier of another facility cannot confirm: HTTP {elsewhere.Code} {elsewhere.ProblemCode}");
        Check.Equal("PENDING_CONFIRMATION", (await node.Raw.GetAsync($"payments/{paymentId}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("payment").Json["status"]!.GetValue<string>(), "still pending");

        // A POS signed in as the waiter shows no desk at all
        using var waiterPos = await PosRig.EnrolAsync(node, "RESTAURANT");
        await waiterPos.SignInAsync("S-0001");
        Check.True(!waiterPos.Ctx.Features.CanConfirmCollections && !waiterPos.Ctx.Features.ShowHandoverDesk, "waiter POS: no collections / handover desk");
        var main = new MainViewModel(waiterPos.Ctx, waiterPos.Navigator);
        main.BuildItems();
        Check.True(main.Items.All(i => i.Title is not ("Collected by waiters" or "Cash handover")), "no desk tabs for a waiter");
        var inbox = new CollectionsInboxViewModel(waiterPos.Ctx, waiterPos.Navigator);
        Check.True(!inbox.CanConfirm, "inbox refuses a waiter");
        var denied = await Check.ThrowsApiAsync(() => waiterPos.Api.ConfirmCollectionAsync(Guid.Parse(paymentId), new ConfirmCollectionRequest(), IdempotencyKeys.New()), "waiter POS confirm");
        Check.True(denied.IsPermissionDenied, "API says permission_denied: " + denied.Code);
        Check.Contains(ScreenViewModel.Describe(denied), "permission", "operator message");

        // Clean up: the cashier rejects the leftover so the RESTAURANT inbox stays tidy
        (await AsCashier(rig, $"payments/{paymentId}/reject", new JsonObject { ["reason"] = "harness cleanup" })).Ok("cleanup reject");
        await AsCashier(rig, $"orders/{openId}/bill/cancel", new JsonObject { ["reason"] = "harness cleanup" });
    }
}
