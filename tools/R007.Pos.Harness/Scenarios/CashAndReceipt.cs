using System.Globalization;
using System.Text.Json.Nodes;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.Devices.Printing;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Harness;

/// <summary>Cash session open/close with a blind count and variance; the receipt payload rendered to 80 mm ESC/POS and checked against the node's snapshot.</summary>
public static class CashAndReceipt
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("cash-session", "Open with float, cash sale, blind-count close, variance and shift report from the node", CashSession),
        new("receipt-render", "Receipt payload -> 80 mm layout -> ESC/POS bytes; every figure equals the node's snapshot; reprint is DUPLICATE", ReceiptRender),
    ];

    private static async Task CashSession(NodeContext node)
    {
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        await rig.SignInAsync("S-0005");
        if (rig.Ctx.CashSession is { } stale)
        {
            await rig.Api.CloseCashSessionAsync(stale.Id, new CloseCashSessionRequest(stale.ExpectedCash ?? 0m), IdempotencyKeys.New());
            await rig.Ctx.RefreshCashSessionAsync();
        }

        var vm = new CashSessionViewModel(rig.Ctx);
        await vm.ActivateAsync();
        Check.True(!vm.IsOpen, "no session open");

        // taking cash without a session is refused by the node (and the dialog)
        var sell = rig.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(rig.Product("GOODS-SPORTS-DRINK")));
        var noSession = await Check.ThrowsApiAsync(() => rig.Api.CreatePaymentAsync(new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(sell.Order!.Id, 700m)], [new TenderInput(TenderTypes.Cash, 700m, null, 700m)]), IdempotencyKeys.New()), "cash without a session");
        Check.True(noSession.Status is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.UnprocessableEntity, $"cash without a cash session refused ({noSession.Status} {noSession.Code})");
        node.Log($"cash without a session -> {(int)noSession.Status} {noSession.Code}");

        vm.OpeningFloatText = "5000.00";
        await vm.OpenCommand.ExecuteAsync();
        Check.True(vm.IsOpen && rig.Ctx.HasOpenCashSession, "session opened: " + vm.Error);
        Check.Equal(5000m, rig.Ctx.CashSession!.OpeningFloat, "opening float");

        var second = await Check.ThrowsApiAsync(() => rig.Api.OpenCashSessionAsync(new OpenCashSessionRequest(rig.Ctx.FacilityId, 100m), IdempotencyKeys.New()), "second open session");
        Check.Equal(System.Net.HttpStatusCode.Conflict, second.Status, "one open session per cashier/device");

        // one cash sale of 2100 (change given is not drawer money)
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(rig.Product("GOODS-SPORTS-DRINK")));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(rig.Product("GOODS-SPORTS-DRINK")));
        Check.Equal(2100m, sell.Order!.Total!.Value, "3 drinks");
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "2100.00", "3000.00", null));
        await sell.PayCommand.ExecuteAsync();
        Check.True(sell.Error is null, "sale paid: " + sell.Error);

        // blind close: the cashier counts 7000 (float 5000 + sales 2100 = 7100 expected) -> variance -100
        vm.CountedText = "7000.00";
        vm.Note = "one 100 note short";
        await vm.CloseCommand.ExecuteAsync();
        Check.True(vm.Error is null, "closed: " + vm.Error);
        Check.True(!rig.Ctx.HasOpenCashSession, "session closed");
        Check.Contains(vm.VarianceText, "7,100.00", "the system figure appears only after closing");
        Check.Contains(vm.VarianceText, "-₦100.00", "variance -100.00");
        Check.True(!rig.Ctx.Features.CanViewShiftReport, "a cashier lacks report.view, so the POS shows the session summary instead of the (403) shift report");
        Check.True(vm.ReportLines.Any(l => l.StartsWith("Expected cash", StringComparison.Ordinal) && l.Contains("7,100.00", StringComparison.Ordinal)), "summary expected cash: " + string.Join(" | ", vm.ReportLines));
        Check.True(vm.ReportLines.Any(l => l.StartsWith("CASH sales", StringComparison.Ordinal) && l.Contains("2,100.00", StringComparison.Ordinal)), "summary cash sales: " + string.Join(" | ", vm.ReportLines));
        await vm.PrintReportCommand.ExecuteAsync();
        Check.True(rig.Printer.PrintedDocuments.Last().Lines.Any(l => l.Text.Contains("Variance", StringComparison.Ordinal)), "the summary prints");

        // a manager (report.view) gets the full shift report from the node
        var manager = await rig.Node.Raw.LoginPinAsync("S-0011");
        var report = (await rig.Node.Raw.GetAsync($"reports/cashier-shift/{vm.LastClosedId:D}", manager)).Ok("shift report").Json;
        Check.Equal("-100.0000", report["variance"]!.GetValue<string>(), "shift report variance");
        Check.Equal("7100.0000", report["expectedCash"]!.GetValue<string>(), "shift report expected cash");
        var typed = await rig.Api.GetShiftReportAsync(vm.LastClosedId).ContinueWith(t => t);
        Check.True(typed.IsFaulted, "the cashier's own token is refused the shift report (report.view)");

        // a closed session takes no more payments
        var closed = await rig.Api.GetOpenCashSessionAsync(rig.Ctx.FacilityId, rig.Auth.Staff!.Id);
        Check.True(closed is null, "no open session after close");
    }

    private static async Task ReceiptRender(NodeContext node)
    {
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        await rig.SignInAsync("S-0005");
        await rig.FreshCashSessionAsync(1000m);
        var sell = rig.NewSell();
        var drink = rig.Product("GOODS-SPORTS-DRINK");
        var balls = rig.Product("GOODS-TENNIS-BALLS");
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(drink));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(drink));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(balls));
        var orderId = sell.Order!.Id;
        Check.Equal(3900m, sell.Order.Total!.Value, "2 x 700 + 2500");

        // partial: cash 2000 (tendered 5000) -> the receipt must show change and BALANCE DUE
        RestaurantFlow.ScriptPayment(rig, "2000.00", (TenderTypes.Cash, "2000.00", "5000.00", null));
        await sell.PayCommand.ExecuteAsync();
        var payment = (await rig.Api.ListPaymentsAsync(rig.Ctx.FacilityId, rig.Ctx.CashSession!.Id, null, 50)).Items.First(p => p.Allocations?.Any(a => a.OrderId == orderId) == true);
        var receiptId = payment.ReceiptId!.Value;

        // 1) the wire snapshot, straight from the node
        var raw = (await rig.Node.Raw.GetAsync($"receipts/{receiptId:D}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("receipt").Json;
        var receipt = await rig.Api.GetReceiptAsync(receiptId);
        Check.Equal(3900m, receipt.Total, "receipt total");
        Check.Equal(1900m, receipt.BalanceDue!.Value, "receipt balance due (partial payment)");
        Check.Equal(2000m, receipt.AmountPaid!.Value, "receipt amount paid");
        Check.Equal(3000m, receipt.ChangeGiven!.Value, "change given");
        Check.Equal(3, receipt.Lines.Sum(l => l.Quantity), "three items");
        Check.True(receipt.Number.StartsWith("RCP-", StringComparison.Ordinal), "receipt number format");

        // 2) layout for 80 mm: only formatting; every amount is the node's
        var document = ReceiptDocumentBuilder.Build(receipt);
        var text = EscPosRenderer.RenderText(document);
        node.Log(Environment.NewLine + text);
        foreach (var line in text.Split('\n'))
        {
            Check.True(line.Length <= 48 + 8 || line.StartsWith("[", StringComparison.Ordinal), "a rendered line fits the 48-column paper: '" + line + "'");
        }

        foreach (var expected in new[] { receipt.Number, "Main Reception", "Sports drink", "Tennis balls", MoneyFormat.Display(3900m).Replace("₦", "N", StringComparison.Ordinal), "N2,000.00", "N5,000.00", "N3,000.00", "BALANCE DUE", "N1,900.00", "Ngozi Eze", raw["footer"]!.GetValue<string>() })
        {
            Check.Contains(text, expected, "rendered receipt");
        }

        // every number the node printed on its own 48-col text also appears in the POS layout (same figures, different layout)
        var serverAmounts = raw["printLines"]!.AsArray().SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l!.GetValue<string>(), @"N[\d,]+\.\d\d").Select(m => m.Value)).Distinct().ToList();
        Check.True(serverAmounts.Count >= 4, "the node's printLines carry amounts");
        foreach (var amount in serverAmounts)
        {
            Check.Contains(text, amount, "an amount from the node's printLines is on the POS receipt");
        }

        // 3) ESC/POS bytes
        var bytes = EscPosRenderer.Render(document);
        Check.True(bytes.Length > 200 && bytes[0] == 0x1B && bytes[1] == 0x40, "starts with ESC @ (initialise)");
        var kick = EscPosRenderer.DrawerKick();
        Check.True(bytes.Where((b, i) => b >= 0x80 && !(i >= 0 && i < bytes.Length && IsInside(bytes, i, kick))).Count() == 0, "7-bit ASCII only outside the drawer-kick pulse parameters (the Naira sign is transliterated)");
        Check.True(ContainsSeq(bytes, [0x1D, 0x56, 0x42, 0x00]), "partial cut command");
        Check.True(ContainsSeq(bytes, EscPosRenderer.DrawerKick()), "cash drawer kick for a cash tender");
        Check.True(document.OpenDrawer, "document asks for the drawer");

        // 4) the snapshot is immutable: refetching gives the same document, even after another payment on the same order
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Transfer, "1900.00", null, "TRF-" + Guid.NewGuid().ToString("N")[..10]));
        await sell.PayCommand.ExecuteAsync();
        var again = (await rig.Node.Raw.GetAsync($"receipts/{receiptId:D}", rig.Auth.AccessToken, rig.DeviceToken)).Ok("receipt again").Json;
        Check.Equal(Strip(raw), Strip(again), "the receipt snapshot did not change after the order was paid off");

        // 5) reprint through the history screen: counted, marked DUPLICATE, no drawer kick
        var history = new HistoryViewModel(rig.Ctx, rig.Navigator);
        await history.ActivateAsync();
        var row = history.Rows.First(r => r.Payment.Id == payment.Id);
        var before = rig.Printer.PrintedDocuments.Count;
        await row.ReprintCommand.ExecuteAsync();
        var duplicate = rig.Printer.PrintedDocuments[before];
        Check.True(duplicate.Lines.Any(l => l.Text.Contains("DUPLICATE", StringComparison.Ordinal)), "reprint is marked DUPLICATE");
        Check.True(!duplicate.OpenDrawer, "a reprint never kicks the drawer");
        var afterReprint = await rig.Api.GetReceiptAsync(receiptId);
        Check.True((afterReprint.ReprintCount ?? 0) >= 1, "the node counted the reprint");

        // 6) QR on the receipt: none for plain goods
        Check.True(string.IsNullOrEmpty(receipt.QrPayload) && document.QrData is null, "no QR on a goods-only receipt");
    }

    private static string Strip(JsonNode node)
    {
        var copy = JsonNode.Parse(node.ToJsonString())!.AsObject();
        copy.Remove("reprintCount");
        copy.Remove("duplicate");
        return copy.ToJsonString();
    }

    private static bool IsInside(byte[] bytes, int index, byte[] sequence)
    {
        for (var start = Math.Max(0, index - sequence.Length + 1); start <= index && start + sequence.Length <= bytes.Length; start++)
        {
            if (bytes.AsSpan(start, sequence.Length).SequenceEqual(sequence))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSeq(byte[] haystack, byte[] needle)
    {
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
