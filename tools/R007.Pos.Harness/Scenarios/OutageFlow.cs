using R007.Pos.Core.Api;
using R007.Pos.Core.Http;
using R007.Pos.Core.Offline;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>
/// The encrypted emergency queue against the REAL node: stop the node, keep selling (order + line + CASH payment), start the node, replay
/// through the API in order with the original Idempotency-Keys and client ids, and check the node's own state. Uses
/// <c>scripts/local-node.sh stop|start</c> (needs R007_NODE_SCRIPT); nothing in the API worktree is edited.
/// </summary>
public static class OutageFlow
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("offline-replay", "Node stopped: queue order + line + cash payment (encrypted); node started: ordered idempotent replay; a refused entry is surfaced", OfflineReplay, NeedsNodeControl: true),
    ];

    private static async Task OfflineReplay(NodeContext node)
    {
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        await rig.SignInAsync("S-0005");
        var session = await rig.FreshCashSessionAsync(1000m);
        var rules = rig.Ctx.Capabilities!.OperatingRules!;
        Check.True(rules.AllowOfflineOrders == true && rules.AllowOfflinePayments == OfflinePaymentPolicy.CashOnly, "Reception allows offline orders + CASH_ONLY payments (from the node)");
        var drink = rig.Product("GOODS-SPORTS-DRINK");
        var restored = 0;
        rig.Connectivity.Restored += (_, _) => restored++;

        // ---- outage ----
        await node.NodeControlAsync("stop");
        try
        {
            await node.WaitForNodeAsync(up: false, TimeSpan.FromSeconds(30));
            var sell = rig.NewSell();
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(drink));
            Check.True(sell.IsPendingConfirmation, "first item is queued: PENDING CONFIRMATION (never shown as confirmed): " + sell.Error);
            Check.True(sell.Order!.Total is null, "no server total while offline: the cart shows an estimate only");
            Check.Contains(sell.TotalText, "ESTIMATE", "labelled estimate");
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(drink));
            var orderId = sell.Order.Id;
            Check.Equal(2, sell.Order.Lines.Count, "two queued lines");
            Check.True(rig.Connectivity.IsOffline, "connectivity monitor reports offline");

            // a transfer cannot be queued: it needs live confirmation
            rig.Navigator.Script = m =>
            {
                var pay = (PaymentViewModel)m;
                pay.Method = TenderTypes.Transfer;
                pay.Reference = "TRF-OFFLINE";
                pay.AddTenderCommand.Execute(null);
                return pay.PayCommand.ExecuteAsync().ContinueWith(_ =>
                {
                    Check.True(!pay.Paid && !pay.QueuedPending, "a transfer is never queued or shown as paid");
                    Check.True(pay.Error is not null, "the cashier is told why: " + pay.Error);
                    pay.CancelCommand.Execute(null);
                });
            };
            await sell.PayCommand.ExecuteAsync();

            // cash IS queued
            var tenderTotal = "1400.00";
            rig.Navigator.Script = async m =>
            {
                var pay = (PaymentViewModel)m;
                Check.True(pay.AmountIsEstimate, "the dialog says the amount is an estimate");
                pay.Method = TenderTypes.Cash;
                pay.TenderAmountText = tenderTotal;
                pay.TenderedText = "2000";
                pay.AddTenderCommand.Execute(null);
                await pay.PayCommand.ExecuteAsync();
                Check.True(pay.QueuedPending && !pay.Paid, "cash is saved on the terminal, NOT confirmed: " + pay.Error);
            };
            await sell.PayCommand.ExecuteAsync();

            var status = await rig.Queue.GetStatusAsync();
            Check.Equal(3, status.PendingCount, "queue: create order (with its first line), add line, cash payment");
        }
        finally
        {
            // fall through to the restart in every case so the node is never left stopped
        }

        // ---- the queue file is encrypted at rest ----
        var files = Directory.GetFiles(Path.GetDirectoryName(PosRigPaths.QueuePath(rig))!, "queue*");
        Check.True(files.Length > 0, "the queue file exists");
        var raw = files.SelectMany(File.ReadAllBytes).ToArray();
        Check.True(!System.Text.Encoding.ASCII.GetString(raw).Contains("CASH", StringComparison.Ordinal), "the queue file holds no plain-text 'CASH' (AES-GCM per record)");
        Check.True(!System.Text.Encoding.ASCII.GetString(raw).Contains(drink.Id.ToString("D"), StringComparison.OrdinalIgnoreCase), "the queue file holds no plain-text product id");

        // ---- node back ----
        await node.NodeControlAsync("start");
        await node.WaitForNodeAsync(up: true, TimeSpan.FromSeconds(90));
        Check.True(await rig.Api.PingAsync(), "ping works again");
        Check.True(restored >= 1, "the connectivity monitor raised Restored (the shell drains the queue on this event)");

        var pending = await rig.Queue.GetPendingAsync();
        var orderPath = pending.First().AggregatePath!;
        var queuedOrderId = Guid.Parse(orderPath[(orderPath.LastIndexOf('/') + 1)..]);

        var result = await rig.Replay.DrainAsync();
        Check.Equal(0, result.Rejected, "nothing was refused on replay");
        Check.Equal(3, result.Replayed, "every queued request was replayed in order");
        Check.Equal(0, result.Remaining, "queue drained");

        var order = await rig.Api.GetOrderAsync(queuedOrderId);
        Check.Equal(2, order.Lines.Sum(l => l.Quantity), "the node has both queued lines");
        Check.Equal(1400m, order.Total, "the node priced the order itself on replay");
        Check.Equal(1400m, order.AmountPaid, "the queued cash payment was applied");
        Check.Equal(0m, order.BalanceDue, "nothing owed");
        var payments = (await rig.Api.ListPaymentsAsync(rig.Ctx.FacilityId, session.Id, null, 50)).Items.Where(p => p.Allocations?.Any(a => a.OrderId == queuedOrderId) == true).ToList();
        Check.Equal(1, payments.Count, "exactly one payment");
        Check.Equal(2000m, payments[0].Tendered!.Value, "tendered cash preserved");
        Check.Equal(600m, payments[0].ChangeGiven!.Value, "change computed by the node");

        // draining again (or the same delivery arriving twice) changes nothing: idempotent
        var again = await rig.Replay.DrainAsync();
        Check.Equal(0, again.Replayed + again.Remaining, "second drain is a no-op");

        // a refused entry (the node re-validates every rule on replay) is surfaced, not dropped
        var bogus = new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(Guid.CreateVersion7(), 100m)], [new TenderInput(TenderTypes.Cash, 100m, null, 100m, ClientIds.New())], session.Id);
        var queued = await rig.Emergency.QueueAsync("api/v1/payments", System.Text.Json.JsonSerializer.Serialize(bogus, PosJsonContext.Default.CreatePaymentRequest), IdempotencyKeys.New(), "Cash for an order the node does not know", null, false);
        Check.True(queued.IsQueued, "queued");
        var refused = await rig.Replay.DrainAsync();
        Check.Equal(1, refused.Rejected, "the node refused the unknown order");
        var rejected = await rig.Queue.GetRejectedAsync();
        Check.True(rejected.Count == 1 && rejected[0].Reason.Length > 0, "the refusal reason is kept for staff: " + rejected.FirstOrDefault()?.Reason);
        node.Log("rejected: " + rejected[0].Reason);
    }
}

/// <summary>Where the rig keeps its queue file (test/diagnostic access only).</summary>
internal static class PosRigPaths
{
    public static string QueuePath(PosRig rig) => rig.QueueFile;
}
