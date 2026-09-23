namespace R007.Pos.Core.Configuration;

/// <summary>Non-secret POS configuration bound from <c>appsettings.json</c> / environment variables (see docs/configuration.md).</summary>
public sealed class PosOptions
{
    public const string SectionName = "Pos";

    /// <summary>
    /// Run against the built-in in-memory mock server with seeded demo data (no backend needed).
    /// Set via <c>Pos:Mock</c>, or the shorthand environment variable <c>R007_MOCK=true</c>.
    /// </summary>
    public bool Mock { get; set; }

    /// <summary>Base URL of the on-site API used as the default in the setup screen (e.g. <c>http://localhost:5080</c>).</summary>
    public Uri ApiBaseUrl { get; set; } = new("http://localhost:5080");

    /// <summary>Folder for the encrypted device identity and emergency queue. Empty = <c>%LOCALAPPDATA%\R007Pos</c>.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>HTTP timeout per attempt, in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    public DeviceRegistrationOptions DeviceRegistration { get; set; } = new();

    public OfflineOptions Offline { get; set; } = new();

    public PrinterOptions Printer { get; set; } = new();

    public ApprovalOptions Approvals { get; set; } = new();

    /// <summary>Max gap (ms) between characters for keyboard-wedge input (NFC/barcode) to count as a scan, not typing.</summary>
    public int WedgeMaxKeyIntervalMs { get; set; } = 50;

    /// <summary>
    /// Fixed sensitive stations require an NFC card <b>and</b> a PIN (architecture/06 §4); NFC alone never suffices there.
    /// The contract has no operating rule for this yet, so it is local configuration (flagged as a contract gap).
    /// </summary>
    public bool RequireNfcAndPin { get; set; }

    /// <summary>Lock (sign out) after this many idle minutes; 0 disables. Fixed tills should not stay signed in unattended.</summary>
    public int IdleLockMinutes { get; set; } = 15;

    /// <summary>Quick-add notes offered on order lines (the contract has no modifier model, only free-text line notes).</summary>
    public List<string> QuickNotes { get; set; } = ["No ice", "Extra spicy", "Well done", "Takeaway"];
}

/// <summary>Suggested defaults for enrolment. Credentials issued by the API are stored DPAPI-protected, never here.</summary>
public sealed class DeviceRegistrationOptions
{
    /// <summary>Human-friendly name suggested at registration (e.g. "Main Reception 1").</summary>
    public string? DeviceName { get; set; }
}

public sealed class OfflineOptions
{
    public int MaxEntries { get; set; } = 200;

    public int MaxAgeMinutes { get; set; } = 30;

    /// <summary>How often the connectivity probe pings the API, in seconds.</summary>
    public int ProbeIntervalSeconds { get; set; } = 10;
}

public sealed class PrinterOptions
{
    /// <summary><c>Simulated</c> (default), <c>Console</c>, <c>File</c> or <c>Windows</c> (raw ESC/POS to a Windows printer).</summary>
    public string Kind { get; set; } = "Simulated";

    /// <summary>Windows printer queue name when <see cref="Kind"/> is <c>Windows</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Output folder when <see cref="Kind"/> is <c>File</c>.</summary>
    public string? OutputDirectory { get; set; }

    public int CharactersPerLine { get; set; } = 48;
}

public sealed class ApprovalOptions
{
    /// <summary>How often to poll for a supervisor decision, in seconds (push can shorten this later).</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>Stop waiting for a supervisor after this many minutes.</summary>
    public int WaitTimeoutMinutes { get; set; } = 10;
}
