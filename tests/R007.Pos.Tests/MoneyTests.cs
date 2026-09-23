using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;

namespace R007.Pos.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("1500.0000", 1500.0000)]
    [InlineData("0.1", 0.1)]
    [InlineData("-25.50", -25.50)]
    [InlineData("1234567890.1234", 1234567890.1234)]
    public void ParseWire_IsInvariantAndExact(string text, double expectedDouble)
    {
        var parsed = MoneyFormat.ParseWire(text);
        Assert.Equal((decimal)expectedDouble, parsed);
    }

    [Fact]
    public void ParseWire_IgnoresCurrentCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); // comma decimal
            Assert.Equal(1500.5m, MoneyFormat.ParseWire("1500.5"));
            Assert.Equal("1500.5000", MoneyFormat.ToWire(1500.5m));
            Assert.Equal("₦1,500.50", MoneyFormat.Display(1500.5m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NoBinaryFloatingPointDrift()
    {
        // 0.1 + 0.2 style errors must never appear: decimals add exactly.
        Assert.Equal(0.3m, MoneyFormat.ParseWire("0.1") + MoneyFormat.ParseWire("0.2"));
        Assert.Equal("0.3000", MoneyFormat.ToWire(MoneyFormat.ParseWire("0.1") + MoneyFormat.ParseWire("0.2")));
    }

    [Theory]
    [InlineData("2500", true, 2500)]
    [InlineData("2,500.00", true, 2500)]
    [InlineData("₦ 1500.5", true, 1500.5)]
    [InlineData("0.01", true, 0.01)]
    [InlineData("0", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("10.005", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("1e3", false, 0)]
    public void TryParseEntered_ValidatesStaffInput(string text, bool ok, double expected)
    {
        Assert.Equal(ok, MoneyFormat.TryParseEntered(text, out var amount));
        if (ok)
        {
            Assert.Equal((decimal)expected, amount);
        }
    }

    [Fact]
    public void TryParseEnteredAllowZero_AcceptsZeroFloat()
    {
        Assert.True(MoneyFormat.TryParseEnteredAllowZero("0.00", out var a));
        Assert.Equal(0m, a);
        Assert.True(MoneyFormat.TryParseEnteredAllowZero("0", out _));
        Assert.False(MoneyFormat.TryParseEnteredAllowZero("-1", out _));
    }

    [Fact]
    public void Json_MoneyIsWrittenAndReadAsDecimalStrings()
    {
        var order = new CreatePaymentRequest(Guid.NewGuid(), [new AllocationInput(Guid.NewGuid(), 1500.5m)], [new TenderInput(TenderTypes.Cash, 1500.5m, null, 2000m)]);

        var json = JsonSerializer.Serialize(order, PosJsonContext.Default.CreatePaymentRequest);

        Assert.Contains("\"amount\":\"1500.5000\"", json);
        Assert.Contains("\"tendered\":\"2000.0000\"", json);
        Assert.DoesNotContain("\"amount\":1500", json);
        var back = JsonSerializer.Deserialize(json, PosJsonContext.Default.CreatePaymentRequest)!;
        Assert.Equal(1500.5m, back.Tenders[0].Amount);
    }

    [Fact]
    public void Json_ToleratesNumbersOnRead()
    {
        var json = """{"facilityId":"00000000-0000-7000-8000-000000000101","allocations":[],"tenders":[{"tenderType":"CASH","amount":12.5}]}""";

        var request = JsonSerializer.Deserialize(json, PosJsonContext.Default.CreatePaymentRequest)!;

        Assert.Equal(12.5m, request.Tenders[0].Amount);
    }
}
