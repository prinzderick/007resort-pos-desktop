using Microsoft.Extensions.Configuration;

namespace R007.Pos.Core.Configuration;

/// <summary>Turns configuration (appsettings + environment) into <see cref="PosOptions"/>, including the <c>R007_MOCK</c> shorthand.</summary>
public static class PosOptionsLoader
{
    /// <summary>
    /// <c>Pos:Mock</c> (env <c>R007_Pos__Mock</c>) or the shorthand root key <c>MOCK</c> (env <c>R007_MOCK</c>, the
    /// <c>R007_</c> prefix is stripped by the environment provider) turns mock mode on. Invalid numbers fall back to safe minimums.
    /// </summary>
    public static PosOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(PosOptions.SectionName).Get<PosOptions>() ?? new PosOptions();
        options.Mock |= configuration.GetValue<bool>("MOCK");
        options.RequestTimeoutSeconds = Math.Max(2, options.RequestTimeoutSeconds);
        options.Offline.MaxEntries = Math.Max(1, options.Offline.MaxEntries);
        options.Offline.MaxAgeMinutes = Math.Max(1, options.Offline.MaxAgeMinutes);
        options.Offline.ProbeIntervalSeconds = Math.Max(1, options.Offline.ProbeIntervalSeconds);
        options.Approvals.PollIntervalSeconds = Math.Max(1, options.Approvals.PollIntervalSeconds);
        options.Approvals.WaitTimeoutMinutes = Math.Max(1, options.Approvals.WaitTimeoutMinutes);
        options.Printer.CharactersPerLine = Math.Clamp(options.Printer.CharactersPerLine, 24, 80);
        return options;
    }
}
