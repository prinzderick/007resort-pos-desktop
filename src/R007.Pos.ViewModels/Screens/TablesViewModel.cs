using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.ViewModels.Screens;

public sealed class TableTile(DiningTable table, Tab? tab, IReadOnlyList<OrderChip>? chips = null)
{
    /// <summary>Bill printed / Awaiting payment / Collected - awaiting confirmation (from the API's bill fields).</summary>
    public IReadOnlyList<OrderChip> Chips { get; } = chips ?? [];

    public string ChipsText => OrderStateChips.Joined(Chips);

    public bool HasChips => Chips.Count > 0;

    public DiningTable Table { get; } = table;

    public Tab? Tab { get; } = tab;

    public string Label => Table.Label;

    public string Status => Table.Status;

    public string Detail => Tab is { } t
        ? $"{(string.IsNullOrWhiteSpace(t.CustomerName) ? "Tab" : t.CustomerName)}  {MoneyFormat.Display(t.BalanceDue, t.Currency ?? "NGN")}"
        : Table.Status == "FREE" ? "Free" : Table.Status;
}

public sealed class TabRow(Tab tab, string? tableLabel, IReadOnlyList<OrderChip>? chips = null)
{
    public IReadOnlyList<OrderChip> Chips { get; } = chips ?? [];

    public string ChipsText => OrderStateChips.Joined(Chips);

    public bool HasChips => Chips.Count > 0;

    public Tab Tab { get; } = tab;

    public string Title => (string.IsNullOrWhiteSpace(Tab.CustomerName) ? "Tab " + Tab.Id.ToString("N")[..6] : Tab.CustomerName) + (tableLabel is null ? string.Empty : $"  ({tableLabel})");

    public string BalanceText => "Owes " + MoneyFormat.Display(Tab.BalanceDue, Tab.Currency ?? "NGN");
}

/// <summary>Tables and open tabs (Restaurant / Indoor Club style): open a tab, add rounds of orders, settle on exit.</summary>
public sealed class TablesViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private readonly INavigator _nav;

    public TablesViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        _nav = nav;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync), null, SetError);
        OpenTabCommand = new AsyncRelayCommand(OpenTabAsync, () => _ctx.Features.CanOpenTabs, SetError);
        SelectTableCommand = new AsyncRelayCommand(p => SelectTableAsync(p as TableTile), null, SetError);
        SelectTabCommand = new RelayCommand(p =>
        {
            if (p is TabRow row)
            {
                TargetChosen?.Invoke(this, new SellTarget(row.Tab.TableId, TableLabelFor(row.Tab.TableId), row.Tab));
            }
        });
    }

    /// <summary>Raised when the operator picks a table/tab to work on (the shell switches to the Sell screen).</summary>
    public event EventHandler<SellTarget>? TargetChosen;

    public ObservableCollection<TableTile> Tables { get; } = [];

    public ObservableCollection<TabRow> Tabs { get; } = [];

    public bool CanOpenTabs => _ctx.Features.CanOpenTabs;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand OpenTabCommand { get; }

    public AsyncRelayCommand SelectTableCommand { get; }

    public RelayCommand SelectTabCommand { get; }

    private List<DiningTable> _tableCache = [];

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    private string? TableLabelFor(Guid? id) => id is null ? null : _tableCache.FirstOrDefault(t => t.Id == id)?.Label;

    private async Task LoadAsync()
    {
        var tables = await _ctx.Api.GetTablesAsync(_ctx.FacilityId).ConfigureAwait(true);
        var tabs = _ctx.Features.CanOpenTabs || _ctx.Capabilities?.Has(Capabilities.OpenTab) == true
            ? await _ctx.Api.ListOpenTabsAsync(_ctx.FacilityId).ConfigureAwait(true)
            : [];

        IReadOnlyList<OrderSummary> open = [];
        try
        {
            open = (await _ctx.Api.ListOrdersAsync(_ctx.FacilityId, "DRAFT,SENT,IN_PREPARATION,READY,SERVED", null, null, 100).ConfigureAwait(true)).Items;
        }
        catch (ApiException)
        {
            // No order.view here: tables still work, just without the bill-state chips.
        }

        _tableCache = [.. tables];
        Tables.Clear();
        foreach (var t in tables)
        {
            var tab = tabs.FirstOrDefault(x => x.Id == t.OpenTabId);
            Tables.Add(new TableTile(t, tab, OrderStateChips.For(open.Where(o => o.TableId == t.Id || (tab is not null && o.TabId == tab.Id)))));
        }

        Tabs.Clear();
        foreach (var tab in tabs)
        {
            Tabs.Add(new TabRow(tab, TableLabelFor(tab.TableId), OrderStateChips.For(open.Where(o => o.TabId == tab.Id))));
        }
    }

    private async Task SelectTableAsync(TableTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (tile.Tab is { } tab)
        {
            TargetChosen?.Invoke(this, new SellTarget(tile.Table.Id, tile.Label, tab));
            return;
        }

        if (tile.Table.Status == "FREE")
        {
            await _ctx.Api.OpenTableAsync(tile.Table.Id, IdempotencyKeys.New()).ConfigureAwait(true);
        }

        TargetChosen?.Invoke(this, new SellTarget(tile.Table.Id, tile.Label, null));
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task OpenTabAsync()
    {
        var modal = new OpenTabViewModel(_ctx, [.. _tableCache.Where(t => t.Status == "FREE")]);
        if (await _nav.ShowModalAsync(modal).ConfigureAwait(true) && modal.Created is { } tab)
        {
            await LoadAsync().ConfigureAwait(true);
            TargetChosen?.Invoke(this, new SellTarget(tab.TableId, TableLabelFor(tab.TableId), tab));
        }
    }
}

/// <summary>Open a tab: a customer name and (optionally) a table. The client supplies a UUIDv7 id so it is idempotent.</summary>
public sealed class OpenTabViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private string _customerName = string.Empty;
    private DiningTable? _table;

    public OpenTabViewModel(PosContext ctx, IReadOnlyList<DiningTable> freeTables)
    {
        _ctx = ctx;
        FreeTables = freeTables;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy, SetError);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public override string Title => "Open a tab";

    public IReadOnlyList<DiningTable> FreeTables { get; }

    public string CustomerName
    {
        get => _customerName;
        set => SetProperty(ref _customerName, value);
    }

    public DiningTable? Table
    {
        get => _table;
        set => SetProperty(ref _table, value);
    }

    public Tab? Created { get; private set; }

    public AsyncRelayCommand OpenCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task OpenAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomerName) && Table is null)
        {
            Error = "Enter a name or pick a table.";
            return;
        }

        var ok = await RunAsync(async () =>
        {
            var request = new OpenTabRequest(_ctx.FacilityId, Table?.Id, string.IsNullOrWhiteSpace(CustomerName) ? null : CustomerName.Trim(), null, ClientIds.New(), _ctx.Time.GetUtcNow());
            Created = await _ctx.Api.OpenTabAsync(request, IdempotencyKeys.New()).ConfigureAwait(true);
        }).ConfigureAwait(true);
        if (ok)
        {
            Close(true);
        }
    }
}
