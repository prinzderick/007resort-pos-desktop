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
        Cash = new CashSessionViewModel(ctx);
        History = new HistoryViewModel(ctx, nav);
        Queue = new QueueViewModel(ctx);
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

    public CashSessionViewModel Cash { get; }

    public HistoryViewModel History { get; }

    public QueueViewModel Queue { get; }

    public ObservableCollection<NavItem> Items { get; } = [];

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

    /// <summary>Refresh the supervisor badge (called by the shell's background loop while signed in).</summary>
    public async Task RefreshBadgesAsync()
    {
        var item = Items.FirstOrDefault(i => ReferenceEquals(i.Screen, Approvals));
        if (item is null)
        {
            return;
        }

        await Approvals.RefreshQuietlyAsync().ConfigureAwait(true);
        item.Badge = Approvals.PendingCount > 0 ? Approvals.PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
