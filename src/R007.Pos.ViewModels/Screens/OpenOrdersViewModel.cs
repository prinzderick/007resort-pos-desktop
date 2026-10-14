using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>One row in the facility-wide open-orders picker (hamburger menu): every draft/sent/in-prep/ready/served
/// order at this facility, not just the ones on the current table/tab/counter.</summary>
public sealed class AnyOpenOrderRow
{
    public AnyOpenOrderRow(OrderSummary order, OpenOrdersViewModel owner)
    {
        Order = order;
        PickCommand = new RelayCommand(() => owner.Pick(Order));
    }

    public OrderSummary Order { get; }

    public string TargetText => Order.TabId is not null
        ? "Tab" + (Order.TableLabel is null ? string.Empty : " / " + Order.TableLabel)
        : Order.TableId is not null
            ? "Table " + (Order.TableLabel ?? Order.TableId!.Value.ToString("N")[..6])
            : "Counter sale";

    public string Title => $"{Order.Number}  {MoneyFormat.Display(Order.Total)}";

    // Billing is a separate state from the order workflow status: a DRAFT/SENT order can already have its bill
    // printed (PAY_AFTER_SERVICE/PAY_BEFORE_LEAVING facilities bill before the order is SERVED), so showing the
    // raw status alone reads as "nobody's touched this yet" when a cashier is actually waiting on payment for it.
    public string StatusText => Order.IsBilled ? "Bill printed" : Order.Status.Replace('_', ' ');

    public RelayCommand PickCommand { get; }
}

/// <summary>
/// "Open orders" (hamburger, top bar): every draft/unsettled order at this facility, so a cashier can jump straight
/// to one from anywhere instead of hunting for the right table/tab first. Picking one closes the dialog and the shell
/// loads it into Sell.
/// </summary>
public sealed class OpenOrdersViewModel : ModalViewModel
{
    private readonly PosContext _ctx;

    public OpenOrdersViewModel(PosContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync), null, SetError);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public override string Title => "Open orders";

    public ObservableCollection<AnyOpenOrderRow> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public OrderSummary? Picked { get; private set; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CancelCommand { get; }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    internal void Pick(OrderSummary order)
    {
        Picked = order;
        Close(true);
    }

    private static readonly string[] OpenStatuses =
    [
        OrderStatuses.Draft, OrderStatuses.Sent, OrderStatuses.InPreparation, OrderStatuses.Ready, OrderStatuses.Served,
    ];

    private async Task LoadAsync()
    {
        // One request per status, not a comma-joined filter[status]: the contract only documents a single-value
        // filter (cash sessions' filter[status]=OPEN) and the combined list silently matched nothing on the real
        // node, which is why this always showed empty.
        var orders = await _ctx.Orders.ListOpenOrdersAsync(_ctx.FacilityId, OpenStatuses, limit: 200).ConfigureAwait(true);
        Items.Clear();
        foreach (var o in orders.OrderByDescending(o => o.CreatedAt))
        {
            Items.Add(new AnyOpenOrderRow(o, this));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
