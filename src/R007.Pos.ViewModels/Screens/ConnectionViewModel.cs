using R007.Pos.Core.Terminal;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>One server the terminal can be pointed at.</summary>
public sealed class ServerChoice
{
    public ServerChoice(string label, Uri url, bool isCurrent, bool isEnrolled)
    {
        Label = label;
        Url = url;
        IsCurrent = isCurrent;
        Status = isCurrent ? "In use" : isEnrolled ? "Enrolled" : "Needs a registration code";
    }

    public string Label { get; }

    public Uri Url { get; }

    public string UrlText => Url.AbsoluteUri.TrimEnd('/');

    public bool IsCurrent { get; }

    public string Status { get; }
}

/// <summary>
/// The hidden Connection dialog (click the title 7 times): switch this terminal between the property server and the
/// online server without losing its enrolment on either. Optionally behind a PIN (<c>Pos:ConnectionPin</c>).
/// </summary>
public sealed class ConnectionViewModel : ModalViewModel
{
    private readonly string _pin;
    private bool _unlocked;
    private string _enteredPin = string.Empty;
    private string _newUrl = string.Empty;

    public ConnectionViewModel(PosContext ctx)
    {
        _pin = ctx.Options.ConnectionPin ?? string.Empty;
        _unlocked = _pin.Length == 0;
        CurrentText = ctx.Endpoint.Current.AbsoluteUri.TrimEnd('/');
        Choices = Build(ctx);
        UnlockCommand = new RelayCommand(Unlock);
        ChooseCommand = new RelayCommand(p =>
        {
            if (p is ServerChoice { IsCurrent: false } choice)
            {
                Choose(choice.Url);
            }
        });
        UseNewCommand = new RelayCommand(UseNew);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public override string Title => "Connection";

    public string CurrentText { get; }

    public IReadOnlyList<ServerChoice> Choices { get; }

    /// <summary>The server picked, or null when the dialog was cancelled.</summary>
    public Uri? Chosen { get; private set; }

    public bool IsLocked => !_unlocked;

    public bool IsUnlocked => _unlocked;

    public string EnteredPin
    {
        get => _enteredPin;
        set => SetProperty(ref _enteredPin, value);
    }

    public string NewUrl
    {
        get => _newUrl;
        set => SetProperty(ref _newUrl, value);
    }

    public RelayCommand UnlockCommand { get; }

    public RelayCommand ChooseCommand { get; }

    public RelayCommand UseNewCommand { get; }

    public RelayCommand CancelCommand { get; }

    private static List<ServerChoice> Build(PosContext ctx)
    {
        var current = IdentityBook.Key(ctx.Endpoint.Current);
        var enrolled = ctx.EnrolledServers.Select(i => IdentityBook.Key(i.ServerUrl)).ToHashSet();
        var seen = new HashSet<string>();
        var list = new List<ServerChoice>();

        void Add(string label, Uri? url)
        {
            if (url is null || !seen.Add(IdentityBook.Key(url)))
            {
                return;
            }

            var key = IdentityBook.Key(url);
            list.Add(new ServerChoice(label, url, key == current, enrolled.Contains(key)));
        }

        Add("Online server", ctx.Options.OnlineUrl);
        Add("Property server", ctx.Options.LocalUrl);
        foreach (var identity in ctx.EnrolledServers)
        {
            Add(identity.FacilityName ?? identity.Name, identity.ServerUrl);
        }

        Add("This server", ctx.Endpoint.Current);
        return list;
    }

    private void Unlock()
    {
        if (EnteredPin == _pin)
        {
            Error = null;
            _unlocked = true;
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(IsUnlocked));
        }
        else
        {
            Error = "Wrong PIN.";
        }
    }

    private void UseNew()
    {
        if (Uri.TryCreate(NewUrl.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            Choose(url);
        }
        else
        {
            Error = "Enter a full server address, for example http://192.168.1.75 or https://api.example.com.";
        }
    }

    private void Choose(Uri url)
    {
        Chosen = url;
        Close(true);
    }
}
