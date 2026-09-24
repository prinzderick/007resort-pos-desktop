using System.Text;
using Microsoft.Extensions.Configuration;
using R007.Pos.Core.Configuration;

namespace R007.Pos.Tests;

public sealed class ConfigurationTests
{
    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_AreSafe_AndMockIsOff()
    {
        var options = PosOptionsLoader.Load(Build([]));

        Assert.False(options.Mock);
        Assert.False(options.RequireNfcAndPin);
        Assert.Equal("File", options.Printer.Kind);
        Assert.Equal(48, options.Printer.CharactersPerLine);
        Assert.Equal(200, options.Offline.MaxEntries);
        Assert.Equal(30, options.Offline.MaxAgeMinutes);
    }

    [Theory]
    [InlineData("MOCK", "true")]        // R007_MOCK=true (the environment provider strips the R007_ prefix)
    [InlineData("Pos:Mock", "true")]    // R007_Pos__Mock=true or appsettings
    public void MockMode_CanBeTurnedOn_EitherWay(string key, string value)
    {
        Assert.True(PosOptionsLoader.Load(Build(new() { [key] = value })).Mock);
    }

    [Fact]
    public void EnvironmentVariables_WithThePrefix_MapToOptions()
    {
        Environment.SetEnvironmentVariable("R007_MOCK", "true");
        Environment.SetEnvironmentVariable("R007_Pos__Printer__Kind", "Windows");
        Environment.SetEnvironmentVariable("R007_Pos__Printer__Name", "EPSON TM-T88");
        Environment.SetEnvironmentVariable("R007_Pos__RequireNfcAndPin", "true");
        try
        {
            var options = PosOptionsLoader.Load(new ConfigurationBuilder().AddEnvironmentVariables(prefix: "R007_").Build());

            Assert.True(options.Mock);
            Assert.Equal("Windows", options.Printer.Kind);
            Assert.Equal("EPSON TM-T88", options.Printer.Name);
            Assert.True(options.RequireNfcAndPin);
        }
        finally
        {
            Environment.SetEnvironmentVariable("R007_MOCK", null);
            Environment.SetEnvironmentVariable("R007_Pos__Printer__Kind", null);
            Environment.SetEnvironmentVariable("R007_Pos__Printer__Name", null);
            Environment.SetEnvironmentVariable("R007_Pos__RequireNfcAndPin", null);
        }
    }

    [Fact]
    public void ShippedAppSettings_BindWithoutSurprises_QuickNotesAreNotDuplicated()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.shipped.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        var options = PosOptionsLoader.Load(config);

        Assert.Equal(["No ice", "Extra spicy", "Well done", "Takeaway"], options.QuickNotes);
        Assert.False(options.Mock);
        Assert.Equal(new Uri("http://localhost:8080"), options.ApiBaseUrl);
        Assert.Equal(15, options.IdleLockMinutes);
    }

    [Fact]
    public void Nonsense_IsClampedToSafeMinimums()
    {
        var options = PosOptionsLoader.Load(Build(new()
        {
            ["Pos:RequestTimeoutSeconds"] = "0",
            ["Pos:Offline:MaxEntries"] = "-5",
            ["Pos:Approvals:PollIntervalSeconds"] = "0",
            ["Pos:Printer:CharactersPerLine"] = "500",
        }));

        Assert.Equal(2, options.RequestTimeoutSeconds);
        Assert.Equal(1, options.Offline.MaxEntries);
        Assert.Equal(1, options.Approvals.PollIntervalSeconds);
        Assert.Equal(80, options.Printer.CharactersPerLine);
    }

    [Fact]
    public void Configuration_HoldsNoSecrets()
    {
        var text = new StringBuilder();
        text.Append(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.shipped.json")));

        Assert.DoesNotContain("token", text.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
