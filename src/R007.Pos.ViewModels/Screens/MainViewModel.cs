using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

public sealed class NavItem(string title, ScreenViewModel screen) : ObservableObject
{
    private string? _badge;

    public string Title { get; } = title;

    public ScreenViewModel Screen { get; } = screen;

    public string? Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value);
    }
}

/// <summary>
/// The signed-in workspace. Which tabs exist is decided purely by the facility's capabilities and the staff member's
/// permissions (<see cref="PosContext.Features"/>): one POS application for every station, no per-facility builds.
/// </summary>
public sealed class MainViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private NavItem? _selected;

    public MainViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        Sell = new SellViewModel(ctx, nav);
        Tables = new TablesViewModel(ctx, nav);
        Reception = new ReceptionViewModel(ctx, nav);
        Approvals = new ApprovalsInboxViewModel(ctx);
        Collections = new CollectionsInboxViewModel(ctx, nav);
        Handovers = new CashHandoverDeskViewModel(ctx, nav);
        Collections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CollectionsInboxViewModel.PendingCount))
            {
                var item = Items.FirstOrDefault(i => ReferenceEquals(i.Screen, Collections));
                if (item is not null)
                {
                    item.Badge = CollectionsBadgeText;
                }

                OnPropertyChanged(nameof(CollectionsBadgeText));
            }
        };
        Cash = new CashSessionViewModel(ctx);
        History = new HistoryViewModel(ctx, nav);
        Queue = new QueueViewModel(ctx);
        SelectCommand = new AsyncRelayCommand(p => p is NavItem item ? SelectAsync(item) : Task.CompletedTask, null, SetError);
        Tables.TargetChosen += async (_, target) =>
        {
            try
            {
                await Sell.SetTargetAsync(target).ConfigureAwait(true);
                var sell = Items.FirstOrDefault(i => ReferenceEquals(i.Screen, Sell));
                if (sell is not null)
                {
                    await SelectAsync(sell).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
            {
                SetError(ex);
            }
        };
    }

    public SellViewModel Sell { get; }

    public TablesViewModel Tables { get; }

    public ReceptionViewModel Reception { get; }

    public ApprovalsInboxViewModel Approvals { get; }

    /// <summary>'Collected by waiters': payments waiters took at tables, waiting for this cashier to confirm.</summary>
    public CollectionsInboxViewModel Collections { get; }

    public CashHandoverDeskViewModel Handovers { get; }

    /// <summary>Count for the shell's status bar badge (null when nothing waits).</summary>
    public string? CollectionsBadgeText => Collections.PendingCount > 0 ? Collections.PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    public CashSessionViewModel Cash { get; }

    public HistoryViewModel History { get; }

    public QueueViewModel Queue { get; }

    public ObservableCollection<NavItem> Items { get; } = [];

    public AsyncRelayCommand SelectCommand { get; }

    public NavItem? Selected
    {
        get => _selected;
        private set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(Current));
            }
        }
    }

    public ScreenViewModel? Current => _selected?.Screen;

    public string StaffName => _ctx.Staff?.DisplayName ?? string.Empty;

    public string FacilityName => _ctx.FacilityName;

    /// <summary>Loads facility rules, catalog and cash session, then builds the tabs this person may use.</summary>
    public async Task InitializeAsync()
    {
        var ok = await RunAsync(async () =>
        {
            await _ctx.LoadFacilityAsync().ConfigureAwait(true);
            await _ctx.LoadCatalogAsync().ConfigureAwait(true);
            await _ctx.RefreshCashSessionAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (!ok && _ctx.Capabilities is null)
        {
            // Without capabilities we cannot know what this station may do: show only the queue/status until it loads.
            Info = "Could not load this station's configuration. Retry when the server is reachable.";
        }

        BuildItems();
        if (Items.Count > 0)
        {
            await SelectAsync(Items[0]).ConfigureAwait(true);
        }
    }

    public void BuildItems()
    {
        var f = _ctx.Features;
        Items.Clear();
        if (f.CanSell)
        {
            Items.Add(new NavItem("Sell", Sell));
        }

        if (f.ShowTables)
        {
            Items.Add(new NavItem("Tables & tabs", Tables));
        }

        if (f.CanBook)
        {
            Items.Add(new NavItem("Reception", Reception));
        }

        if (f.ShowCollections)
        {
            Items.Add(new NavItem("Collected by waiters", Collections));
        }

        if (f.ShowHandoverDesk)
        {
            Items.Add(new NavItem("Cash handover", Handovers));
        }

        if (f.CanDecideApprovals)
        {
            Items.Add(new NavItem("Approvals", Approvals));
        }

        if (f.CanManageCashSession)
        {
            Items.Add(new NavItem("Cash session", Cash));
        }

        if (f.CanViewHistory)
        {
            Items.Add(new NavItem("History", History));
        }

        Items.Add(new NavItem("Queue", Queue));
        OnPropertyChanged(nameof(StaffName));
        OnPropertyChanged(nameof(FacilityName));
    }

    public async Task SelectAsync(NavItem item)
    {
        Selected = item;
        try
        {
            await item.Screen.ActivateAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            SetError(ex);
        }
    }

    /// <summary>A push hint about collections/bills/handovers: reload what is on screen over REST (never trusted as data).</summary>
    public async Task OnCollectionEventAsync(string name, string? message)
    {
        if (Items.Any(i => ReferenceEquals(i.Screen, Collections)))
        {
            await Collections.OnRealtimeAsync(name, message).ConfigureAwait(true);
            var item = Items.First(i => ReferenceEquals(i.Screen, Collections));
            item.Badge = CollectionsBadgeText;
            OnPropertyChanged(nameof(CollectionsBadgeText));
        }

        if (name == "cash-handover.received" && Items.Any(i => ReferenceEquals(i.Screen, Handovers)))
        {
            await Handovers.RefreshQuietlyAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Refresh the supervisor badge (called by the shell's background loop while signed in).</summary>
    public async Task RefreshBadgesAsync()
    {
        var approvals = Items.FirstOrDefault(i => ReferenceEquals(i.Screen, Approvals));
        if (approvals is not null)
        {
            await Approvals.RefreshQuietlyAsync().ConfigureAwait(true);
            approvals.Badge = Approvals.PendingCount > 0 ? Approvals.PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        var collections = Items.FirstOrDefault(i => ReferenceEquals(i.Screen, Collections));
        if (collections is not null)
        {
            await Collections.RefreshQuietlyAsync().ConfigureAwait(true);
            Collections.TickAges();
            collections.Badge = CollectionsBadgeText;
            OnPropertyChanged(nameof(CollectionsBadgeText));
        }

        var handovers = Items.FirstOrDefault(i => ReferenceEquals(i.Screen, Handovers));
        if (handovers is not null)
        {
            await Handovers.RefreshQuietlyAsync().ConfigureAwait(true);
            handovers.Badge = Handovers.OpenCount > 0 ? Handovers.OpenCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }
    }
}
