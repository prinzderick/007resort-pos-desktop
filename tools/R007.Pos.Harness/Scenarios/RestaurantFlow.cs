using System.Net;
using System.Text.Json.Nodes;
using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>Restaurant is pay-after-service: table -> order -> send -> kitchen -> served -> pay. Also tabs, idempotent replay and ETag/If-Match behaviour.</summary>
public static class RestaurantFlow
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("restaurant-order", "Table order: open table, lines, send, kitchen, serve, pay cash with change (pay-after-service)", RestaurantOrder),
        new("restaurant-tab", "Open tab, two rounds, kitchen, serve, settle the tab with a split", RestaurantTab),
        new("idempotency", "Client UUIDv7 ids: replay (same key / same id), 409 on same id + different body, If-Match 412/428", Idempotency),
    ];

    private static async Task<PosRig> CashierAsync(NodeContext node)
    {
        var rig = await PosRig.EnrolAsync(node, "RESTAURANT");
        await rig.SignInAsync("S-0006");
        await rig.FreshCashSessionAsync(2000m);
        return rig;
    }

    /// <summary>Scripts the payment dialog: one tender per entry (method, amount, tendered, reference), then Pay.</summary>
    public static void ScriptPayment(PosRig rig, string? amountToPay, params (string Method, string Amount, string? Tendered, string? Reference)[] tenders) =>
        rig.Navigator.Script = async modal =>
        {
            var pay = (PaymentViewModel)modal;
            if (amountToPay is not null)
            {
                pay.AmountToPayText = amountToPay;
            }

            foreach (var t in tenders)
            {
                pay.Method = t.Method;
                pay.TenderAmountText = t.Amount;
                pay.TenderedText = t.Tendered ?? string.Empty;
                pay.Reference = t.Reference ?? string.Empty;
                pay.AddTenderCommand.Execute(null);
                Check.True(pay.Error is null, "tender accepted by the dialog: " + pay.Error);
            }

            await pay.PayCommand.ExecuteAsync();
            Check.True(pay.Paid, "payment succeeded: " + pay.Error);
            pay.CancelCommand.Execute(null);
        };

    private static async Task RestaurantOrder(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        Check.True(rig.Ctx.Features.PayAfterService, "Restaurant is pay-after-service (paymentTiming=" + rig.Ctx.Capabilities!.OperatingRules!.PaymentTiming + ")");

        // table -> sell target through the tables screen
        await Janitor.FreeTableAsync(rig);
        var tables = new TablesViewModel(rig.Ctx, rig.Navigator);
        SellTarget? target = null;
        tables.TargetChosen += (_, t) => target = t;
        await tables.ActivateAsync();
        var free = tables.Tables.First(t => t.Status == "FREE");
        await tables.SelectTableCommand.ExecuteAsync(free);
        Check.NotNull(target, "table chosen");
        var opened = (await rig.Api.GetTablesAsync(rig.Ctx.FacilityId)).First(t => t.Id == free.Table.Id);
        Check.Equal("OCCUPIED", opened.Status, "table is occupied after opening");
        node.Log($"table {free.Label} opened");

        var sell = rig.NewSell();
        await sell.SetTargetAsync(target!);
        var food = rig.ProductNamed("Moi Moi");
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(food));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(food));
        Check.Equal(2000m, sell.Order!.Total!.Value, "server total for 2 x 1000");
        Check.Equal(free.Table.Id, sell.Order.TableId!.Value, "order is on the table");
        Check.True(!sell.CanPayOrder, "pay-after-service: cannot pay a DRAFT order (button disabled)");

        await sell.SendCommand.ExecuteAsync();
        Check.Equal(OrderStatuses.Sent, sell.Order!.Status, "order sent");
        Check.True(sell.Order.Lines.All(l => l.Status == "ROUTED"), "lines routed to the kitchen");
        Check.True(!sell.CanPayOrder && sell.CanServe, "sent: not payable yet, can be served");

        // the node refuses payment before service (the POS never even offers it) - assert the raw contract too
        var early = await rig.Node.Raw.PostAsync("payments", new JsonObject
        {
            ["facilityId"] = rig.Ctx.FacilityId.ToString("D"),
            ["cashSessionId"] = rig.Ctx.CashSession!.Id.ToString("D"),
            ["allocations"] = new JsonArray(new JsonObject { ["orderId"] = sell.Order.Id.ToString("D"), ["amount"] = "2000.0000" }),
            ["tenders"] = new JsonArray(new JsonObject { ["tenderType"] = "CASH", ["amount"] = "2000.0000", ["tendered"] = "2000.0000" }),
        }, rig.Auth.AccessToken, rig.DeviceToken);
        Check.Equal(HttpStatusCode.Conflict, early.Status, "payment before service");
        Check.Equal("order_state_invalid", early.ProblemCode!, "payment before service code");
        if (early.Json["status"]?.GetValueKind() != System.Text.Json.JsonValueKind.Number)
        {
            node.Log("API DEVIATION: problem.status is not the HTTP status integer on order_state_invalid (fixed in api-posfix ProblemRenderer); the POS tolerates it.");
        }

        // serving before the kitchen is done is refused with a message, not a crash
        await sell.ServeCommand.ExecuteAsync();
        Check.True(sell.Error is not null && sell.Order.Status != OrderStatuses.Served, "serve before ready is refused: " + sell.Error);

        var tickets = await Kitchen.CompleteOrderAsync(node, sell.Order.Id, sell.Order.Lines.Select(l => (Guid?)null).Concat(await StationsAsync(rig, sell.Order.Id)));
        Check.True(tickets >= 1, "kitchen completed the ticket(s)");

        await sell.ServeCommand.ExecuteAsync();
        Check.Equal(OrderStatuses.Served, sell.Order!.Status, "served: " + sell.Error);
        Check.True(sell.CanPayOrder, "served: payable");

        ScriptPayment(rig, null, (TenderTypes.Cash, "2000.00", "2500.00", null));
        await sell.PayCommand.ExecuteAsync();
        Check.True(sell.Info?.Contains("500.00", StringComparison.Ordinal) == true, "change of 500.00 shown: " + sell.Info);
        Check.True(!sell.HasOrder, "cart cleared after payment");

        var doc = Check.NotNull(rig.Printer.PrintedDocuments.LastOrDefault(), "receipt printed");
        Check.True(doc.OpenDrawer, "cash drawer kicked for a cash tender");
        Check.True(doc.Lines.Any(l => l.Text.Contains("Change", StringComparison.Ordinal) && l.Text.Contains("500.00", StringComparison.Ordinal)), "receipt shows the change");
    }

    private static async Task<IEnumerable<Guid?>> StationsAsync(PosRig rig, Guid orderId)
    {
        var order = await rig.Api.GetOrderAsync(orderId);
        return order.Lines.Select(l => l.PrepRoute?.StationId).Where(s => s is not null).Cast<Guid?>().ToList();
    }

    private static async Task RestaurantTab(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        var table = await Janitor.FreeTableAsync(rig);

        var tables = new TablesViewModel(rig.Ctx, rig.Navigator);
        SellTarget? target = null;
        tables.TargetChosen += (_, t) => target = t;
        rig.Navigator.Script = modal =>
        {
            var open = (OpenTabViewModel)modal;
            open.CustomerName = "Mr Bello";
            open.Table = open.FreeTables.First(t => t.Id == table.Id);
            return open.OpenCommand.ExecuteAsync();
        };
        await tables.ActivateAsync();
        await tables.OpenTabCommand.ExecuteAsync();
        var tab = Check.NotNull(target?.Tab, "tab opened through the open-tab dialog");
        Check.Equal("Mr Bello", tab.CustomerName!, "tab customer");
        Check.True(tab.IsOpen, "tab is OPEN");

        var sell = rig.NewSell();
        await sell.SetTargetAsync(target!);
        var moi = rig.ProductNamed("Moi Moi");

        // round 1
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(moi));
        await sell.SendCommand.ExecuteAsync();
        var first = sell.Order!;
        Check.Equal(first.Id, (await rig.Api.GetTabAsync(tab.Id)).OrderIds.Single(), "round 1 is on the tab");

        // round 2 (a new order on the same tab)
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(moi));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(moi));
        Check.True(sell.Order!.Id != first.Id, "the next item starts a new order (round) on the tab");
        await sell.SendCommand.ExecuteAsync();
        Check.True(sell.Error is null && sell.Order!.Status == OrderStatuses.Sent, $"round 2 sent (status {sell.Order?.Status}, error {sell.Error})");
        var second = sell.Order!;
        var current = await rig.Api.GetTabAsync(tab.Id);
        Check.Equal(2, current.OrderIds.Count, "two rounds on the tab");
        Check.Equal(1000m + 2000m, current.BalanceDue, "tab balance is the server's sum");

        // settle before service is refused by the node
        var early = await Check.ThrowsApiAsync(() => rig.Api.SettleTabAsync(tab.Id, new SettleTabRequest([new TenderInput(TenderTypes.Cash, current.BalanceDue, null, current.BalanceDue)], rig.Ctx.CashSession!.Id), IdempotencyKeys.New()), "settle before service");
        Check.Equal("order_state_invalid", early.Code, "tab settle before service");

        foreach (var order in new[] { first, second })
        {
            node.Log($"round {order.Number}: status {(await rig.Api.GetOrderAsync(order.Id)).Status}, lines {order.Lines.Count}");
            await Kitchen.CompleteOrderAsync(node, order.Id, await StationsAsync(rig, order.Id));
            var fresh = await rig.Api.GetOrderAsync(order.Id);
            await rig.Api.ServeOrderAsync(order.Id, fresh.RowVersion, IdempotencyKeys.New());
        }

        // settle: split cash + transfer through the payment dialog (tab target)
        var reference = "TRF-" + Guid.NewGuid().ToString("N")[..10];
        ScriptPayment(rig, null, (TenderTypes.Cash, "2000.00", "2000.00", null), (TenderTypes.Transfer, "1000.00", null, reference));
        await sell.SettleTabCommand.ExecuteAsync();
        Check.True(sell.Error is null, "settle tab: " + sell.Error);
        var settled = await rig.Api.GetTabAsync(tab.Id);
        Check.Equal("SETTLED", settled.Status, "tab settled");
        Check.Equal(0m, settled.BalanceDue, "tab balance is zero");
        Check.Equal(OrderStatuses.Settled, (await rig.Api.GetOrderAsync(first.Id)).Status, "round 1 settled");
        Check.Equal(OrderStatuses.Settled, (await rig.Api.GetOrderAsync(second.Id)).Status, "round 2 settled");

        // a transfer reference backs one live payment
        var dup = await Check.ThrowsApiAsync(async () =>
        {
            var o = await rig.Api.CreateOrderAsync(new CreateOrderRequest(rig.Ctx.FacilityId, Channel: OrderChannels.Counter, Lines: [new OrderLineInput(moi.Id, 1)]), IdempotencyKeys.New());
            await Kitchen.CompleteOrderAsync(node, o.Id, []);
            await rig.Api.SendOrderAsync(o.Id, o.RowVersion, IdempotencyKeys.New());
            var sent = await rig.Api.GetOrderAsync(o.Id);
            await Kitchen.CompleteOrderAsync(node, o.Id, await StationsAsync(rig, o.Id));
            var ready = await rig.Api.GetOrderAsync(o.Id);
            await rig.Api.ServeOrderAsync(o.Id, ready.RowVersion, IdempotencyKeys.New());
            await rig.Api.CreatePaymentAsync(new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(o.Id, sent.Total)], [new TenderInput(TenderTypes.Transfer, sent.Total, reference)], rig.Ctx.CashSession!.Id), IdempotencyKeys.New());
        }, "reusing a transfer reference");
        Check.Equal("duplicate_reference", dup.Code, "duplicate transfer reference code");
    }

    private static async Task Idempotency(NodeContext node)
    {
        using var rig = await CashierAsync(node);
        var moi = rig.ProductNamed("Moi Moi");
        var facility = rig.Ctx.FacilityId;

        // same client id + same body (two different Idempotency-Keys, like a queue replay after a lost response) -> the same order, no duplicate
        var orderId = ClientIds.New();
        var lineId = ClientIds.New();
        var create = new CreateOrderRequest(facility, Channel: OrderChannels.Counter, Lines: [new OrderLineInput(moi.Id, 2, null, lineId)], Id: orderId);
        var key = IdempotencyKeys.New();
        var a = await rig.Api.CreateOrderAsync(create, key);
        var replaySameKey = await rig.Api.CreateOrderAsync(create, key);
        Check.Equal(a.Id, replaySameKey.Id, "same Idempotency-Key returns the original order");
        Check.Equal(a.Number, replaySameKey.Number, "same key: same order number (not a new order)");
        var replaySameId = await rig.Api.CreateOrderAsync(create, IdempotencyKeys.New());
        Check.Equal(orderId, replaySameId.Id, "same client id + same body is a replay");
        Check.Equal(1, replaySameId.Lines.Count, "still one line");

        // same client id + different body -> 409 concurrency_conflict
        var different = create with { Lines = [new OrderLineInput(moi.Id, 5, null, lineId)] };
        var conflict = await Check.ThrowsApiAsync(() => rig.Api.CreateOrderAsync(different, IdempotencyKeys.New()), "same id, different body");
        Check.Equal(HttpStatusCode.Conflict, conflict.Status, "same id + different body status");
        Check.Equal("concurrency_conflict", conflict.Code, "same id + different body code");

        // same Idempotency-Key + different body -> refused (key reuse), never a silent second order
        var reused = await Check.ThrowsApiAsync(() => rig.Api.CreateOrderAsync(create with { Id = ClientIds.New(), Lines = [new OrderLineInput(moi.Id, 1, null, ClientIds.New())] }, key), "same key, different body");
        Check.True(reused.Status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, $"key reuse refused (got {(int)reused.Status} {reused.Code})");

        // a non-UUIDv7 client id is refused
        var v4 = await rig.Node.Raw.PostAsync("orders", new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["facilityId"] = facility.ToString("D"), ["channel"] = "COUNTER", ["lines"] = new JsonArray(new JsonObject { ["productId"] = moi.Id.ToString("D"), ["quantity"] = 1 }) }, rig.Auth.AccessToken, rig.DeviceToken);
        Check.Equal(HttpStatusCode.UnprocessableEntity, v4.Status, "non-UUIDv7 id");

        // ETag / If-Match: strong ETag on every read, stale -> 412, missing -> 428
        var read = await rig.Node.Raw.GetAsync($"orders/{orderId:D}", rig.Auth.AccessToken, rig.DeviceToken);
        Check.Equal($"\"{a.RowVersion}\"", read.ETag!, "ETag is the quoted rowVersion");
        var addLine = new JsonObject { ["productId"] = moi.Id.ToString("D"), ["quantity"] = 1 };
        var missing = await rig.Node.Raw.PostAsync($"orders/{orderId:D}/lines", addLine, rig.Auth.AccessToken, rig.DeviceToken);
        Check.Equal(HttpStatusCode.PreconditionRequired, missing.Status, "missing If-Match");
        var stale = await rig.Node.Raw.PostAsync($"orders/{orderId:D}/lines", addLine, rig.Auth.AccessToken, rig.DeviceToken, ifMatch: "\"999\"");
        Check.Equal(HttpStatusCode.PreconditionFailed, stale.Status, "stale If-Match");
        Check.Equal("concurrency_conflict", stale.ProblemCode!, "stale If-Match code");

        // the POS client's own formatting of If-Match is accepted and the rowVersion advances
        var added = await rig.Api.AddLineAsync(orderId, a.RowVersion, new OrderLineInput(moi.Id, 1, null, ClientIds.New()), IdempotencyKeys.New());
        Check.Equal(a.RowVersion + 1, added.RowVersion, "rowVersion advances by one");
        Check.Equal(3, added.Lines.Sum(l => l.Quantity), "2 + 1 items");

        // replaying that add with the same key returns the same result without adding again
        var lineKey = IdempotencyKeys.New();
        var newLine = new OrderLineInput(moi.Id, 1, null, ClientIds.New());
        var l1 = await rig.Api.AddLineAsync(orderId, added.RowVersion, newLine, lineKey);
        var l2 = await rig.Api.AddLineAsync(orderId, added.RowVersion, newLine, lineKey);
        Check.Equal(l1.RowVersion, l2.RowVersion, "line add replay returns the original result");
        Check.Equal(4, (await rig.Api.GetOrderAsync(orderId)).Lines.Sum(l => l.Quantity), "the replayed add was applied once");
    }
}
