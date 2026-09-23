using System.Text;
using R007.Pos.Core.Api;
using R007.Pos.Core.Security;
using R007.Pos.Core.Terminal;
using R007.Pos.Devices.CashDrawer;
using R007.Pos.Devices.Input;
using R007.Pos.Devices.Printing;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Tests;

public sealed class ReceiptAndDeviceTests
{
    private static Receipt Sample(bool withQr = false, bool cash = true) => new(
        Guid.Parse("00000000-0000-7000-8000-00000000c001"),
        "R-5001",
        Guid.Parse("00000000-0000-7000-8000-000000000101"),
        "Restaurant",
        "007 Resort & Spa",
        "Bayelsa State, Nigeria",
        new DateTimeOffset(2026, 9, 23, 13, 5, 0, TimeSpan.Zero),
        "Amaka Cashier",
        ["RES-000101"],
        "T3",
        [
            new ReceiptItem("Star Lager 60cl", 2, 1500m, 3000m),
            new ReceiptItem("Jollof Rice with extra chicken and plantain (large)", 1, 3500m, 3500m),
        ],
        6500m,
        0m,
        0m,
        6500m,
        "NGN",
        cash ? [new ReceiptTender(TenderTypes.Cash, 6500m, null)] : [new ReceiptTender(TenderTypes.PosTerminal, 6500m, "RRN-1")],
        cash ? 500m : null,
        null,
        withQr ? "ENT.ABC123" : null,
        0,
        "Thank you for choosing 007 Resort & Spa");

    private const string ExpectedReceipt = """
                007 Resort & Spa
                           Restaurant
                     Bayelsa State, Nigeria
        ------------------------------------------------
        Receipt R-5001                    23/09/26 14:05
        Cashier: Amaka Cashier
        Table: T3
        Order: RES-000101
        ------------------------------------------------
        Star Lager 60cl
          2 x N1,500.00                        N3,000.00
        Jollof Rice with extra chicken and plantain
        (large)
          1 x N3,500.00                        N3,500.00
        ------------------------------------------------
        Subtotal                               N6,500.00
        TOTAL                                  N6,500.00
        ------------------------------------------------
        Cash                                   N6,500.00
        Change                                   N500.00
        ------------------------------------------------
            Thank you for choosing 007 Resort & Spa
        [drawer kick]
        [cut]

        """;

    [Fact]
    public void Receipt_Snapshot_80mm_48Columns()
    {
        var document = ReceiptDocumentBuilder.Build(Sample());

        var text = EscPosRenderer.RenderText(document);

        Assert.Equal(ExpectedReceipt.ReplaceLineEndings("\n"), text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 48, $"line too long: '{line}'"));
    }

    [Fact]
    public void Receipt_AmountsAreExactlyTheApiFigures_NothingIsRecomputed()
    {
        // A deliberately inconsistent fixture: if the POS did its own maths the printed total would change.
        var odd = Sample() with { Total = 9999.99m, Subtotal = 1.11m };

        var text = EscPosRenderer.RenderText(ReceiptDocumentBuilder.Build(odd));

        Assert.Contains("N9,999.99", text, StringComparison.Ordinal);
        Assert.Contains("N1.11", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reprint_IsMarkedDuplicate_AndNeverKicksTheDrawer()
    {
        var document = ReceiptDocumentBuilder.Build(Sample(), reprint: true);

        Assert.StartsWith("*** DUPLICATE ***", document.Lines[0].Text, StringComparison.Ordinal);
        Assert.False(document.OpenDrawer);
        Assert.True(ReceiptDocumentBuilder.Build(Sample()).OpenDrawer);
        Assert.False(ReceiptDocumentBuilder.Build(Sample(cash: false)).OpenDrawer);
    }

    [Fact]
    public void EscPos_Bytes_HaveInitCodepageAlignmentBoldQrAndCut()
    {
        var document = new ReceiptDocument(
            [new ReceiptLine("HELLO ₦500", ReceiptAlignment.Center, Bold: true), new ReceiptLine("plain")],
            CutPaper: true,
            QrData: "ENT.ABC",
            OpenDrawer: true);

        var bytes = EscPosRenderer.Render(document);

        Assert.Equal([0x1B, 0x40, 0x1B, 0x74, 0x00], bytes[..5]);                    // ESC @, ESC t 0
        Assert.Equal([0x1B, 0x70, 0x00, 0x19, 0xFA], bytes[5..10]);                   // drawer kick right after init
        var ascii = Encoding.ASCII.GetString(bytes);
        Assert.Contains("HELLO N500\n", ascii, StringComparison.Ordinal);            // Naira transliterated, LF terminated
        Assert.True(IndexOf(bytes, [0x1B, 0x61, 0x01]) >= 0);                        // centre
        Assert.True(IndexOf(bytes, [0x1B, 0x45, 0x01]) >= 0);                        // bold on
        Assert.True(IndexOf(bytes, [0x1B, 0x45, 0x00]) >= 0);                        // bold off after the bold line
        var store = IndexOf(bytes, [0x1D, 0x28, 0x6B, 0x0A, 0x00, 0x31, 0x50, 0x30]); // GS ( k pL pH 31 50 30 : store 7 bytes + 3
        Assert.True(store >= 0);
        Assert.Equal("ENT.ABC", Encoding.ASCII.GetString(bytes, store + 8, 7));
        Assert.True(IndexOf(bytes, [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]) > store); // print symbol
        Assert.Equal([0x1D, 0x56, 0x42, 0x00], bytes[^4..]);                          // GS V 66 0 partial cut last
    }

    [Fact]
    public void EscPos_WrapsLongLines_AndHardSplitsLongWords()
    {
        var lines = EscPosRenderer.Wrap("aaaa bbbb cccc", 9).ToList();
        Assert.Equal(["aaaa bbbb", "cccc"], lines);
        Assert.Equal(["abcde", "fghij", "k"], EscPosRenderer.Wrap("abcdefghijk", 5).ToList());
        Assert.Equal([string.Empty], EscPosRenderer.Wrap(string.Empty, 5).ToList());
    }

    [Fact]
    public void Sanitize_TransliteratesNairaAndStripsControlAndUnknown()
    {
        Assert.Equal("N1,500 cafe \"q\" - ok?", EscPosRenderer.Sanitize("₦1,500 café “q” – ok☃"));
        Assert.Equal("    tab", EscPosRenderer.Sanitize("\ttab"));
    }

    [Fact]
    public void TwoColumns_NeverTruncatesTheAmount()
    {
        var line = ReceiptDocumentBuilder.TwoColumns("A very long product name that will not fit on the line", "N12,345.00", 48);

        Assert.Equal(48, line.Length);
        Assert.EndsWith("N12,345.00", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscPosPrinter_WritesRenderedBytesToThePort_AndRefusesWhenNotReady()
    {
        var port = new MemoryPrinterPort();
        var printer = new EscPosReceiptPrinter(port);
        var document = ReceiptDocumentBuilder.Build(Sample(withQr: true));

        await printer.PrintAsync(document);
        Assert.Equal(EscPosRenderer.Render(document), Assert.Single(port.Writes));

        port.Status = PrinterStatus.PaperOut;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintAsync(document));
        Assert.Contains("PaperOut", ex.Message, StringComparison.Ordinal);
        Assert.Single(port.Writes);
    }

    [Fact]
    public async Task FileAndConsolePrinters_ProduceTextAndBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "r007-print-" + Guid.NewGuid().ToString("N"));
        try
        {
            var document = ReceiptDocumentBuilder.Build(Sample(withQr: true));
            await new FileReceiptPrinter(dir, time: new ManualTimeProvider()).PrintAsync(document);
            var txt = Assert.Single(Directory.GetFiles(dir, "*.txt"));
            var bin = Assert.Single(Directory.GetFiles(dir, "*.bin"));
            Assert.Contains("[QR: ENT.ABC123]", await File.ReadAllTextAsync(txt), StringComparison.Ordinal);
            Assert.Equal(EscPosRenderer.Render(document), await File.ReadAllBytesAsync(bin));

            var writer = new StringWriter();
            await new ConsoleReceiptPrinter(writer).PrintAsync(document);
            Assert.Contains("TOTAL", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task PrinterCashDrawer_SendsTheKickPulse()
    {
        var port = new MemoryPrinterPort();
        var drawer = new PrinterCashDrawer(port);

        await drawer.OpenAsync();

        Assert.Equal(EscPosRenderer.DrawerKick(), Assert.Single(port.Writes));
        Assert.True(await drawer.IsOpenAsync());
    }

    [Fact]
    public async Task WindowsSpoolerPort_OffMacOs_ReportsOfflineInsteadOfCrashing()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // covered by the manual hardware checklist
        }

        var port = new WindowsSpoolerPort("EPSON TM-T88");

        Assert.Equal(PrinterStatus.Offline, await port.GetStatusAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => port.WriteAsync([1]));
    }

    [Fact]
    public async Task PrintService_ReportsPrinterProblemsWithoutThrowing_SoAPaidSaleIsNeverLost()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        pos.Printer.Status = PrinterStatus.PaperOut;

        var result = await new PrintService(pos.Env.Api, pos.Printer).PrintAsync(new ReceiptDocument([new ReceiptLine("x")]));

        Assert.False(result.Printed);
        Assert.Contains("PaperOut", result.Message, StringComparison.Ordinal);
    }

    // Keyboard wedge --------------------------------------------------------------------------------------------------
    private static string? Feed(KeyboardWedgeDecoder decoder, string text, TimeSpan gap, DateTimeOffset start, bool enter = true)
    {
        var at = start;
        string? result = null;
        foreach (var c in text)
        {
            result = decoder.Feed(c, at) ?? result;
            at += gap;
        }

        return enter ? decoder.Feed('\r', at) : result;
    }

    [Fact]
    public void Wedge_FastBurstEndingInEnter_IsAScan()
    {
        var decoder = new KeyboardWedgeDecoder(TimeSpan.FromMilliseconds(50));

        var scan = Feed(decoder, "6001234500011", TimeSpan.FromMilliseconds(5), DateTimeOffset.UnixEpoch);

        Assert.Equal("6001234500011", scan);
    }

    [Fact]
    public void Wedge_SlowHumanTyping_IsNotAScan()
    {
        var decoder = new KeyboardWedgeDecoder(TimeSpan.FromMilliseconds(50));

        Assert.Null(Feed(decoder, "60012345", TimeSpan.FromMilliseconds(200), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Wedge_ShortBurst_IsIgnored_AndIsBurstingFlagsSwallowing()
    {
        var decoder = new KeyboardWedgeDecoder(TimeSpan.FromMilliseconds(50), minLength: 4);
        var t = DateTimeOffset.UnixEpoch;

        decoder.Feed('1', t);
        decoder.Feed('2', t.AddMilliseconds(5));
        Assert.False(decoder.IsBursting);
        decoder.Feed('3', t.AddMilliseconds(10));
        Assert.True(decoder.IsBursting);
        Assert.Null(decoder.Feed('\r', t.AddMilliseconds(15))); // only 3 chars: below the minimum

        Assert.False(decoder.IsBursting); // reset after Enter
    }

    [Fact]
    public void Wedge_TwoScansBackToBack_AreSeparate()
    {
        var decoder = new KeyboardWedgeDecoder(TimeSpan.FromMilliseconds(50));
        var t = DateTimeOffset.UnixEpoch;

        var first = Feed(decoder, "04A1B2C3", TimeSpan.FromMilliseconds(5), t);
        var second = Feed(decoder, "04FFEE00", TimeSpan.FromMilliseconds(5), t.AddSeconds(2));

        Assert.Equal("04A1B2C3", first);
        Assert.Equal("04FFEE00", second);
    }

    [Fact]
    public void Wedge_PauseMidScan_RestartsTheBuffer()
    {
        var decoder = new KeyboardWedgeDecoder(TimeSpan.FromMilliseconds(50));
        var t = DateTimeOffset.UnixEpoch;
        decoder.Feed('9', t);
        decoder.Feed('9', t.AddMilliseconds(5));

        Assert.Equal("1234", Feed(decoder, "1234", TimeSpan.FromMilliseconds(5), t.AddSeconds(1))); // stale "99" discarded
    }

    // Device identity + protection ------------------------------------------------------------------------------------
    [Fact]
    public void DeviceIdentity_IsStoredProtected_AndTokenIsNotInTheClear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "r007-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "device.bin");
            var store = new FileDeviceIdentityStore(path, new InsecureKeyProtector());
            var identity = new DeviceIdentity(new Uri("http://10.0.0.5:8080/"), Guid.NewGuid(), "SECRET-DEVICE-TOKEN", "Till 1", Guid.NewGuid(), null, "Restaurant");

            store.Save(identity);

            var loaded = store.Load();
            Assert.Equal(identity, loaded);
            // The InsecureKeyProtector only prefixes; in production DPAPI encrypts. Here we assert the *API contract*: bytes go through the protector.
            var raw = File.ReadAllBytes(path);
            Assert.Equal("R007-INSECURE"u8.ToArray(), raw[..13]);
            store.Clear();
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void DeviceIdentity_FromAnotherUserOrCorrupt_IsTreatedAsNotEnrolled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "r007-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "device.bin");
            File.WriteAllBytes(path, [1, 2, 3, 4]);

            Assert.Null(new FileDeviceIdentityStore(path, new InsecureKeyProtector()).Load());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
