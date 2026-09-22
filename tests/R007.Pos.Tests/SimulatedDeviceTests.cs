using R007.Pos.Core.Terminal;
using R007.Pos.Devices.Printing;
using R007.Pos.Devices.Simulated;

namespace R007.Pos.Tests;

public sealed class SimulatedDeviceTests
{
    [Fact]
    public async Task ReceiptPrinter_RecordsDocuments_WhenReady()
    {
        var printer = new SimulatedReceiptPrinter();
        var receipt = new ReceiptDocument([new ReceiptLine("007 RESORT & SPA", ReceiptAlignment.Center, Bold: true)]);

        await printer.PrintAsync(receipt);

        Assert.Single(printer.PrintedDocuments);
        Assert.Equal(PrinterStatus.Ready, await printer.GetStatusAsync());
    }

    [Fact]
    public async Task ReceiptPrinter_Throws_WhenPaperOut()
    {
        var printer = new SimulatedReceiptPrinter { Status = PrinterStatus.PaperOut };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => printer.PrintAsync(new ReceiptDocument([new ReceiptLine("x")])));
    }

    [Fact]
    public async Task NfcReader_RaisesTagRead_OnlyWhileListening()
    {
        var reader = new SimulatedNfcReader();
        string? seen = null;
        reader.TagRead += (_, args) => seen = args.Uid;

        Assert.False(reader.SimulateTap("04a1b2c3"));

        await reader.StartAsync();
        Assert.True(reader.SimulateTap("04a1b2c3"));
        Assert.Equal("04A1B2C3", seen);
    }

    [Fact]
    public async Task BarcodeScanner_RaisesScan_WhenStarted()
    {
        var scanner = new SimulatedBarcodeScanner();
        string? seen = null;
        scanner.BarcodeScanned += (_, args) => seen = args.Data;

        await scanner.StartAsync();
        scanner.SimulateScan("5901234123457");

        Assert.Equal("5901234123457", seen);
    }

    [Fact]
    public async Task CashDrawer_TracksOpenState()
    {
        var drawer = new SimulatedCashDrawer();

        await drawer.OpenAsync();
        Assert.True(await drawer.IsOpenAsync());

        drawer.Close();
        Assert.False(await drawer.IsOpenAsync());
        Assert.Equal(1, drawer.OpenCount);
    }

    [Fact]
    public async Task CustomerDisplay_ShowsAndClears()
    {
        var display = new SimulatedCustomerDisplay();

        await display.ShowAsync("Total", "NGN 5,000.00");
        Assert.Equal("Total", display.Line1);

        await display.ClearAsync();
        Assert.Equal(string.Empty, display.Line2);
    }

    [Fact]
    public void TerminalContext_IsLocked_WithoutStaff()
    {
        var context = new TerminalContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.False(context.HasStaff);
        Assert.True((context with { StaffId = Guid.NewGuid() }).HasStaff);
    }
}
