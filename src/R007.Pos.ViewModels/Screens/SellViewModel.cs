using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.ViewModels.Screens;

public sealed record CategoryChip(Guid? Id, string Name);

/// <summary>A tile in the touch product grid. <c>PriceText</c> is the API's price for this facility (display only).</summary>
public sealed class ProductTile(Product product)
{
    public Product Product { get; } = product;

    public string Name => Product.Name;

    public string PriceText => MoneyFormat.Display(Product.Price, Product.Currency);

    public bool IsAvailable => Product.Active;
}

/// <summary>An unpaid order on the current table/tab, with its bill-state chips; picking it loads it into the cart so it can be billed, served or paid.</summary>
public sealed class OpenOrderRow(OrderSummary order)
{
    public OrderSummary Order { get; } = order;

    public string Title => $"{Order.Number}  {MoneyFormat.Display(Order.Total)}";

    public string StatusText => Order.Status.Replace('_', ' ');

    public IReadOnlyList<OrderChip> Chips { get; } = OrderStateChips.For(order);

    public string ChipsText => OrderStateChips.Joined(Chips);

    public bool HasChips => Chips.Count > 0;
}

public sealed class CartLineViewModel(WorkingLine line)
{
    public WorkingLine Line { get; } = line;

    public string Name => Line.Name;

    public int Quantity => Line.Quantity;

    public string UnitPriceText => MoneyFormat.Display(Line.UnitPrice);

    /// <summary>Server line total, or "pending" while the line is only queued (no local price maths).</summary>
    public string LineTotalText => Line.LineTotal is { } total ? MoneyFormat.Display(total) : "pending";

    public string? Notes => Line.Notes;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Line.Notes);

    public bool IsPending => Line.IsPending;
}

/// <summary>Where new orders go: nothing (counter sale), a table, or an open tab.</summary>
public sealed record SellTarget(Guid? TableId, string? TableLabel, Tab? Tab)
{
    public static SellTarget Counter { get; } = new(null, null, null);

    public string Label => Tab is { } t
        ? $"Tab {(string.IsNullOrWhiteSpace(t.CustomerName) ? t.Id.ToString("N")[..6] : t.CustomerName)}{(TableLabel is null ? string.Empty : " / " + TableLabel)}"
        : TableLabel is not null ? $"Table {TableLabel}" : "Counter sale";
}

/// <summary>
/// The main sell screen: category tabs, touch product grid, search, barcode scan, and the cart. Every amount in the cart
/// is the API's (added/removed lines return the re-priced order). Sensitive lines/order actions go through the
/// supervisor approval flow. Works on a table/tab for Club/Restaurant-style facilities.
/// </summary>
public sealed class SellViewModel : ScreenViewModel, IScanTarget
{
    private readonly PosContext _ctx;
    private readonly INavigator _nav;
    private CategoryChip _selectedCategory;
    private string _searchText = string.Empty;
    private string _scanText = string.Empty;
    private string? _pendingNote;
    private WorkingOrder? _order;
    private CartLineViewModel? _selectedLine;
    private SellTarget _target = SellTarget.Counter;
    private Tab? _tab;

    public SellViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        _nav = nav;
        _selectedCategory = new CategoryChip(null, "All");
        AddProductCommand = new AsyncRelayCommand(p => AddAsync(p as ProductTile), _ => _ctx.Features.CanSell, SetError);
        IncrementCommand = new AsyncRelayCommand(p => ChangeQuantityAsync(p as CartLineViewModel, +1), null, SetError);
        DecrementCommand = new AsyncRelayCommand(p => ChangeQuantityAsync(p as CartLineViewModel, -1), null, SetError);
        RemoveLineCommand = new AsyncRelayCommand(p => ChangeQuantityAsync(p as CartLineViewModel, int.MinValue), null, SetError);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend, SetError);
        ServeCommand = new AsyncRelayCommand(ServeAsync, () => CanServe, SetError);
        PayCommand = new AsyncRelayCommand(PayOrderAsync, () => CanPayOrder, SetError);
        SettleTabCommand = new AsyncRelayCommand(SettleTabAsync, () => CanSettleTab, SetError);
        VoidCommand = new AsyncRelayCommand(VoidAsync, () => CanVoid, SetError);
        PrintBillCommand = new AsyncRelayCommand(PrintBillAsync, () => CanPrintBill, SetError);
        ReopenBillCommand = new AsyncRelayCommand(ReopenBillAsync, () => CanReopenBill, SetError);
        SelectOpenOrderCommand = new AsyncRelayCommand(p => SelectOpenOrderAsync(p as OpenOrderRow), null, SetError);
        DiscountCommand = new AsyncRelayCommand(p => AdjustAsync(p as CartLineViewModel, AdjustmentKinds.DiscountPercent), null, SetError);
        CompCommand = new AsyncRelayCommand(p => AdjustAsync(p as CartLineViewModel, AdjustmentKinds.Comp), null, SetError);
        PriceOverrideCommand = new AsyncRelayCommand(p => AdjustAsync(p as CartLineViewModel, AdjustmentKinds.PriceOverride), null, SetError);
        ScanCommand = new AsyncRelayCommand(() => HandleScanAsync(ScanText), null, SetError);
        NewOrderCommand = new RelayCommand(NewOrder);
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(RefreshOrderAsync), null, SetError);
        ToggleNoteCommand = new RelayCommand(p => PendingNote = string.Equals(PendingNote, p as string, StringComparison.Ordinal) ? null : p as string);
        SelectCategoryCommand = new RelayCommand(p => SelectedCategory = p as CategoryChip ?? SelectedCategory);
        ctx.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PosContext.Products) or nameof(PosContext.Categories))
            {
                RebuildCatalog();
            }
            else if (e.PropertyName == nameof(PosContext.Features))
            {
                RaiseActions();
            }
        };
        RebuildCatalog();
    }

    public ObservableCollection<CategoryChip> Categories { get; } = [];

    public ObservableCollection<ProductTile> VisibleProducts { get; } = [];

    public ObservableCollection<CartLineViewModel> Lines { get; } = [];

    /// <summary>Unpaid orders already on this table/tab (the cashier picks one to print its bill, serve or take payment).</summary>
    public ObservableCollection<OpenOrderRow> OpenOrders { get; } = [];

    public bool HasOpenOrders => OpenOrders.Count > 0;

    public IReadOnlyList<string> QuickNotes => _ctx.Options.QuickNotes.Count > 0 ? _ctx.Options.QuickNotes : R007.Pos.Core.Configuration.PosOptions.DefaultQuickNotes;

    public CategoryChip SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                RebuildProducts();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RebuildProducts();
            }
        }
    }

    /// <summary>The barcode/scan box: keyboard-wedge scanners type here and press Enter.</summary>
    public string ScanText
    {
        get => _scanText;
        set => SetProperty(ref _scanText, value);
    }

    public string? PendingNote
    {
        get => _pendingNote;
        set => SetProperty(ref _pendingNote, value);
    }

    public CartLineViewModel? SelectedLine
    {
        get => _selectedLine;
        set => SetProperty(ref _selectedLine, value);
    }

    public bool ShowBarcodeBox => _ctx.Features.CanScanBarcodes;

    public WorkingOrder? Order => _order;

    public SellTarget Target
    {
        get => _target;
        private set
        {
            if (SetProperty(ref _target, value))
            {
                OnPropertyChanged(nameof(TargetLabel));
                OnPropertyChanged(nameof(IsTabTarget));
            }
        }
    }

    public string TargetLabel => _target.Label;

    public bool IsTabTarget => _target.Tab is not null;

    public Tab? ActiveTab
    {
        get => _tab;
        private set
        {
            if (SetProperty(ref _tab, value))
            {
                OnPropertyChanged(nameof(TabBalanceText));
                OnPropertyChanged(nameof(CanSettleTab));
            }
        }
    }

    public string TabBalanceText => _tab is { } t ? $"Tab balance {MoneyFormat.Display(t.BalanceDue, t.Currency ?? "NGN")}" : string.Empty;

    // Server-priced totals (or, in an emergency, a clearly labelled estimate) -------------------------------------
    public bool HasOrder => _order is not null;

    public bool IsPendingConfirmation => _order?.IsPendingConfirmation == true;

    public string OrderNumberText => _order is null ? "No order yet" : $"Order {_order.Number}  [{_order.Status}]";

    public string SubtotalText => _order?.Subtotal is { } v ? MoneyFormat.Display(v, _order.Currency) : string.Empty;

    public string DiscountText => _order?.DiscountTotal is { } v and > 0m ? "-" + MoneyFormat.Display(v, _order.Currency) : string.Empty;

    public string TaxText => _order?.TaxTotal is { } v and > 0m ? MoneyFormat.Display(v, _order.Currency) : string.Empty;

    public string TotalText => _order is null
        ? MoneyFormat.Display(0m)
        : _order.Total is { } total
            ? MoneyFormat.Display(total, _order.Currency)
            : "ESTIMATE " + MoneyFormat.Display(_order.EstimatedTotal, _order.Currency) + " (server will price)";

    public string BalanceText => _order?.BalanceDue is { } v && _order.AmountPaid is > 0m ? "Balance " + MoneyFormat.Display(v, _order.Currency) : string.Empty;

    public string StatusBanner => _order switch
    {
        null => string.Empty,
        { IsPendingConfirmation: true } => "PENDING CONFIRMATION: saved on this terminal, not yet on the server.",
        { PendingApprovalId: not null } => "Waiting for supervisor approval.",
        { FullyCollectedByWaiter: true } => "COLLECTED BY WAITER - awaiting confirmation. Confirm or reject it in 'Collected by waiters'; do not take payment again.",
        { HasPendingCollection: true } => "Part of this bill is already collected by a waiter and awaits confirmation.",
        { IsBilled: true } => $"BILL PRINTED{BillCountSuffix} - the order is frozen. Awaiting payment.",
        _ when AwaitingService && CanServe => "Payment is taken after service: mark the order served once the items are ready.",
        _ => string.Empty,
    };

    public bool IsBilled => _order?.IsBilled == true;

    private string BillCountSuffix => _order?.BillPrintCount is > 1 ? $" (printed {_order.BillPrintCount} times)" : string.Empty;

    /// <summary>Reprint counter for the cashier: "Bill printed x2".</summary>
    public string BillText => _order is { IsBilled: true } o ? $"Bill printed x{o.BillPrintCount ?? 1}" : string.Empty;

    public IReadOnlyList<OrderChip> StateChips => _order is null ? [] : OrderStateChips.For(_order);

    public string StateChipsText => OrderStateChips.Joined(StateChips);

    public bool HasStateChips => StateChips.Count > 0;

    /// <summary>Print (or reprint) the pre-bill for this order. A draft is only billable at a pay-first facility.</summary>
    public bool CanPrintBill => _order is { IsPendingConfirmation: false, IsClosed: false, HasLines: true, PendingApprovalId: null } o
        && _ctx.Features.CanPrintBill && (!o.IsDraft || _ctx.Features.PayFirst) && !FullyPaid && !o.FullyCollectedByWaiter && o.Total is > 0m;

    public bool CanReopenBill => _order is { IsBilled: true, IsPendingConfirmation: false, HasPendingCollection: false } && _ctx.Features.CanReopenBill;

    // Actions gating (UI only; the API enforces) -----------------------------------------------------------------
    public bool CanSend => _order is { IsDraft: true, HasLines: true, IsBilled: false } && _ctx.Features.CanSend && _order.PendingApprovalId is null;

    public bool CanPayOrder => _order is { HasLines: true, IsClosed: false, FullyCollectedByWaiter: false } && _ctx.Features.CanPay && _order.PendingApprovalId is null && !AwaitingService && !FullyPaid;

    /// <summary>Pay-after-service facility (Restaurant): the node only takes payment for a SERVED order, so paying is offered once it is served.</summary>
    public bool AwaitingService => _ctx.Features.PayAfterService && _order is { IsPendingConfirmation: false } o && o.Status != OrderStatuses.Served;

    /// <summary>Server-reported: nothing left to pay (a pay-first order stays DRAFT/SENT after it is paid, it is not SETTLED).</summary>
    public bool FullyPaid => _order is { BalanceDue: <= 0m, AmountPaid: > 0m };

    /// <summary>Mark an order served (order.serve) so a pay-after-service facility can take payment. The kitchen/bar must have the items ready.</summary>
    public bool CanServe => _order is { IsPendingConfirmation: false, Status: OrderStatuses.Sent or OrderStatuses.InPreparation or OrderStatuses.Ready } && _ctx.Features.CanServe && _order.PendingApprovalId is null;

    public bool CanSettleTab => _tab is { IsOpen: true } && _ctx.Features.CanPay;

    public bool CanVoid => _order is { IsClosed: false, IsBilled: false, PendingApprovalId: null, IsPendingConfirmation: false } && _ctx.Features.CanVoid;

    public bool CanDiscount => _ctx.Features.CanDiscount && !IsPendingConfirmation && !IsBilled;

    public bool CanComp => _ctx.Features.CanComp && !IsPendingConfirmation && !IsBilled;

    public bool CanPriceOverride => _ctx.Features.CanPriceOverride && !IsPendingConfirmation && !IsBilled;

    public bool CanEditLines => _order is { IsDraft: true, IsPendingConfirmation: false, IsBilled: false } && _ctx.Features.CanRemoveLines;

    public AsyncRelayCommand AddProductCommand { get; }

    public AsyncRelayCommand IncrementCommand { get; }

    public AsyncRelayCommand DecrementCommand { get; }

    public AsyncRelayCommand RemoveLineCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand PayCommand { get; }

    public AsyncRelayCommand ServeCommand { get; }

    public AsyncRelayCommand SettleTabCommand { get; }

    public AsyncRelayCommand VoidCommand { get; }

    public AsyncRelayCommand PrintBillCommand { get; }

    public AsyncRelayCommand ReopenBillCommand { get; }

    public AsyncRelayCommand SelectOpenOrderCommand { get; }

    public AsyncRelayCommand DiscountCommand { get; }

    public AsyncRelayCommand CompCommand { get; }

    public AsyncRelayCommand PriceOverrideCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand NewOrderCommand { get; }

    public RelayCommand ToggleNoteCommand { get; }

    public RelayCommand SelectCategoryCommand { get; }

    /// <summary>Points the screen at a table / tab (from the Tables screen) and starts with an empty cart.</summary>
    public async Task SetTargetAsync(SellTarget target)
    {
        Target = target;
        ActiveTab = target.Tab;
        NewOrder();
        if (target.Tab is not null)
        {
            await RunAsync(RefreshTabAsync).ConfigureAwait(true);
        }

        await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
    }

    public override async Task ActivateAsync()
    {
        RebuildCatalog();
        if (_tab is not null)
        {
            await RunAsync(RefreshTabAsync).ConfigureAwait(true);
        }

        await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
    }

    private async Task RefreshOpenOrdersQuietlyAsync()
    {
        OpenOrders.Clear();
        if (_target.TableId is null && _target.Tab is null)
        {
            OnPropertyChanged(nameof(HasOpenOrders));
            return;
        }

        try
        {
            var page = await _ctx.Api.ListOrdersAsync(_ctx.FacilityId, "DRAFT,SENT,IN_PREPARATION,READY,SERVED", _target.Tab?.Id, null, 100).ConfigureAwait(true);
            foreach (var o in page.Items.Where(o => _target.Tab is not null || o.TableId == _target.TableId))
            {
                OpenOrders.Add(new OpenOrderRow(o));
            }
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // The list is a convenience: the cart still works without it.
        }

        OnPropertyChanged(nameof(HasOpenOrders));
    }

    private async Task SelectOpenOrderAsync(OpenOrderRow? row)
    {
        if (row is null)
        {
            return;
        }

        Error = null;
        _order = WorkingOrder.FromServer(await _ctx.Api.GetOrderAsync(row.Order.Id).ConfigureAwait(true));
        RebuildCart();
    }

    private async Task PrintBillAsync()
    {
        Error = null;
        await RefreshOrderAsync().ConfigureAwait(true);
        if (_order is not { } order || !CanPrintBill)
        {
            return;
        }

        BillResult? result = null;
        try
        {
            result = await _ctx.Api.PrintBillAsync(order.Id, new BillRequest(), IdempotencyKeys.New()).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ex.Code == "supervisor_required")
        {
            var modal = new SensitiveActionViewModel(
                _ctx,
                "Print the bill again",
                $"The bill for {order.Number} was cancelled before, so a supervisor must authorise printing it again.",
                Permissions.BillCancelApprove,
                async input =>
                {
                    result = await _ctx.Api.PrintBillAsync(order.Id, new BillRequest(), IdempotencyKeys.New(), input.StepUpToken).ConfigureAwait(true);
                    return new SensitiveResult(true, null);
                },
                false,
                "order",
                order.Id);
            await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        }

        if (result is null)
        {
            return;
        }

        _order = WorkingOrder.FromServer(result.Order);
        RebuildCart();
        var printed = await _ctx.Printing.PrintBillAsync(result.Bill).ConfigureAwait(true);
        Info = (result.Reprint ? $"Bill reprinted (print #{result.Bill.PrintCount}). " : "Bill printed. ") + printed.Message;
        await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
        if (_tab is not null)
        {
            await RefreshTabAsync().ConfigureAwait(true);
        }
    }

    private async Task ReopenBillAsync()
    {
        Error = null;
        var order = _order!;
        var modal = new SensitiveActionViewModel(
            _ctx,
            "Reopen the bill",
            $"Reopen {order.Number} so it can be changed. A supervisor must approve; a new bill must be printed afterwards.",
            Permissions.BillCancelApprove,
            async input =>
            {
                var r = await _ctx.Api.CancelBillAsync(order.Id, new CancelBillRequest(input.Reason), IdempotencyKeys.New(), input.StepUpToken).ConfigureAwait(true);
                return new SensitiveResult(r.Order is not null, r.Pending?.Approval.Id);
            },
            true,
            "order",
            order.Id);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await RunAsync(RefreshOrderAsync).ConfigureAwait(true);
        if (modal.Outcome == SensitiveOutcome.Applied)
        {
            Info = "Bill reopened. Print a new bill when the order is ready.";
        }

        await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
    }

    private void NewOrder()
    {
        _order = null;
        Info = null;
        RebuildCart();
    }

    private OrderTarget OrderTarget() => new(
        _ctx.FacilityId,
        _target.TableId,
        _target.Tab?.Id,
        _target.Tab is not null || _target.TableId is not null ? OrderChannels.DineIn : OrderChannels.Counter);

    private async Task AddAsync(ProductTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        await AddProductAsync(tile.Product).ConfigureAwait(true);
    }

    private async Task AddProductAsync(Product product)
    {
        Error = null;
        if (_order is { IsDraft: false } or { IsClosed: true } or { IsBilled: true })
        {
            _order = null; // a sent/closed order is final: the next item starts a new one on the same table/tab
        }

        var updated = await _ctx.Orders.AddProductAsync(_order, product, 1, PendingNote, OrderTarget()).ConfigureAwait(true);
        PendingNote = null;
        _order = updated;
        RebuildCart();
        if (updated.IsPendingConfirmation)
        {
            Info = "Server unreachable: item saved on this terminal (pending confirmation).";
        }
    }

    private async Task ChangeQuantityAsync(CartLineViewModel? line, int delta)
    {
        if (line is null || _order is null)
        {
            return;
        }

        Error = null;
        if (delta == int.MinValue)
        {
            _order = await _ctx.Orders.RemoveLineAsync(_order, line.Line.Id).ConfigureAwait(true);
        }
        else
        {
            _order = await _ctx.Orders.SetQuantityAsync(_order, line.Line, line.Line.Quantity + delta).ConfigureAwait(true);
        }

        RebuildCart();
    }

    private async Task SendAsync()
    {
        Error = null;
        _order = await _ctx.Orders.SendAsync(_order!).ConfigureAwait(true);
        RebuildCart();
        Info = _order.IsPendingConfirmation ? "Send saved on this terminal: NOT confirmed until the server is back." : "Sent.";
        await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
        if (_tab is not null)
        {
            await RefreshTabAsync().ConfigureAwait(true);
        }
    }

    private async Task ServeAsync()
    {
        Error = null;
        await RefreshOrderAsync().ConfigureAwait(true); // the kitchen moves the order's version: always serve against the latest
        if (_order is not { } order || !CanServe)
        {
            return;
        }

        try
        {
            _order = WorkingOrder.FromServer(await _ctx.Api.ServeOrderAsync(order.Id, order.RowVersion, IdempotencyKeys.New()).ConfigureAwait(true));
            Info = "Served.";
            await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
        }
        catch (ApiException ex) when (ex.Code == "order_state_invalid")
        {
            Error = ex.UserMessage; // e.g. "Some items are still being prepared."
        }

        RebuildCart();
        if (_tab is not null)
        {
            await RefreshTabAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Barcode / SKU / search text. A unique hit is added straight to the cart.</summary>
    public async Task HandleScanAsync(string data)
    {
        var code = data?.Trim();
        ScanText = string.Empty;
        if (string.IsNullOrEmpty(code) || !_ctx.Features.CanSell)
        {
            return;
        }

        Error = null;
        var local = _ctx.Products.FirstOrDefault(p => string.Equals(p.Sku, code, StringComparison.OrdinalIgnoreCase));
        if (local is not null)
        {
            await AddProductAsync(local).ConfigureAwait(true);
            return;
        }

        IReadOnlyList<Product> found;
        try
        {
            found = await _ctx.Api.GetProductsAsync(_ctx.FacilityId, code).ConfigureAwait(true);
        }
        catch (ApiUnavailableException)
        {
            Error = "Cannot look up that code while the server is unreachable. Pick the item from the grid.";
            return;
        }

        var active = found.Where(p => p.Active).ToList();
        switch (active.Count)
        {
            case 1:
                await AddProductAsync(active[0]).ConfigureAwait(true);
                break;
            case 0:
                Error = $"No product matches '{code}'.";
                break;
            default:
                SearchText = code;
                Info = $"{active.Count} products match '{code}'. Pick one.";
                break;
        }
    }

    private async Task PayOrderAsync()
    {
        var order = _order!;
        await PayAsync(PaymentTarget.ForOrder(order)).ConfigureAwait(true);
    }

    private async Task SettleTabAsync()
    {
        await RefreshTabAsync().ConfigureAwait(true);
        if (_tab is null)
        {
            return;
        }

        if (_order is { IsDraft: true, HasLines: true, IsPendingConfirmation: false } && _ctx.Features.CanSend)
        {
            Error = "Send the current order before settling the tab.";
            return;
        }

        await PayAsync(PaymentTarget.ForTab(_tab, Target.Label)).ConfigureAwait(true);
    }

    private async Task PayAsync(PaymentTarget target)
    {
        Error = null;
        var modal = new PaymentViewModel(_ctx, target);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        if (modal.QueuedPending)
        {
            NewOrder();
            Info = modal.Info;
            return;
        }

        if (modal.Paid)
        {
            await RunAsync(async () =>
            {
                await RefreshOrderAsync().ConfigureAwait(true);
                if (_tab is not null)
                {
                    await RefreshTabAsync().ConfigureAwait(true);
                }

                if (_order is { IsClosed: true } or { BalanceDue: <= 0m, AmountPaid: > 0m })
                {
                    NewOrder(); // paid in full (a pay-first order stays DRAFT/SENT on the node, so the balance decides)
                }
            }).ConfigureAwait(true);
            Info = modal.ChangeText ?? "Paid.";
            await RefreshOpenOrdersQuietlyAsync().ConfigureAwait(true);
        }
    }

    private async Task VoidAsync()
    {
        await RunSensitiveAsync(
            "Void order",
            $"Void {_order!.Number}. Sent items are reversed by the server.",
            Permissions.OrderVoidApprove,
            async input => SensitiveResult.From(await _ctx.Api.VoidOrderAsync(_order!.Id, _order.RowVersion, new VoidRequest(input.Reason), IdempotencyKeys.New(), input.StepUpToken).ConfigureAwait(true)),
            SensitiveValueKind.None).ConfigureAwait(true);
    }

    private async Task AdjustAsync(CartLineViewModel? line, string kind)
    {
        if (line is null || _order is null)
        {
            return;
        }

        var (title, permission, valueKind) = kind switch
        {
            AdjustmentKinds.DiscountPercent => ("Discount", Permissions.OrderDiscountApprove, SensitiveValueKind.Percent),
            AdjustmentKinds.Comp => ("Complimentary item", Permissions.OrderCompApprove, SensitiveValueKind.None),
            _ => ("Price override", Permissions.OrderPriceOverrideApprove, SensitiveValueKind.Price),
        };

        await RunSensitiveAsync(
            title,
            $"{title} on {line.Name} x{line.Quantity}.",
            permission,
            async input => SensitiveResult.From(await _ctx.Api.AdjustLineAsync(
                _order!.Id,
                line.Line.Id,
                _order.RowVersion,
                new AdjustmentRequest(kind, kind == AdjustmentKinds.Comp ? "0.0000" : input.Value, input.Reason),
                IdempotencyKeys.New(),
                input.StepUpToken).ConfigureAwait(true)),
            valueKind).ConfigureAwait(true);
    }

    private async Task RunSensitiveAsync(string title, string summary, string approvePermission, Func<SensitiveInput, Task<SensitiveResult>> execute, SensitiveValueKind valueKind)
    {
        Error = null;
        var modal = new SensitiveActionViewModel(_ctx, title, summary, approvePermission, execute, true, "order", _order?.Id, valueKind);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await RunAsync(async () =>
        {
            await RefreshOrderAsync().ConfigureAwait(true);
            if (_order is { Status: OrderStatuses.Voided })
            {
                NewOrder();
                Info = "Order voided.";
            }
        }).ConfigureAwait(true);
        if (modal.Outcome == SensitiveOutcome.Applied && _order is not null)
        {
            Info = $"{title}: approved and applied.";
        }
    }

    private async Task RefreshOrderAsync()
    {
        if (_order is { IsPendingConfirmation: false } order)
        {
            _order = await _ctx.Orders.RefreshAsync(order).ConfigureAwait(true);
            RebuildCart();
        }
    }

    private async Task RefreshTabAsync()
    {
        if (_target.Tab is { } tab)
        {
            ActiveTab = await _ctx.Api.GetTabAsync(tab.Id).ConfigureAwait(true);
        }
    }

    // View state -------------------------------------------------------------------------------------------------
    private void RebuildCatalog()
    {
        Categories.Clear();
        Categories.Add(new CategoryChip(null, "All"));
        foreach (var c in _ctx.Categories)
        {
            Categories.Add(new CategoryChip(c.Id, c.Name));
        }

        _selectedCategory = Categories[0];
        OnPropertyChanged(nameof(SelectedCategory));
        RebuildProducts();
        OnPropertyChanged(nameof(ShowBarcodeBox));
    }

    private void RebuildProducts()
    {
        VisibleProducts.Clear();
        var search = SearchText.Trim();
        foreach (var p in _ctx.Products)
        {
            if (!p.Active)
            {
                continue;
            }

            var matches = search.Length > 0
                ? p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (p.Sku?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                : _selectedCategory.Id is null || p.CategoryId == _selectedCategory.Id;
            if (matches)
            {
                VisibleProducts.Add(new ProductTile(p));
            }
        }
    }

    private void RebuildCart()
    {
        Lines.Clear();
        foreach (var l in _order?.Lines ?? [])
        {
            Lines.Add(new CartLineViewModel(l));
        }

        foreach (var name in new[]
        {
            nameof(Order), nameof(HasOrder), nameof(IsPendingConfirmation), nameof(OrderNumberText), nameof(SubtotalText), nameof(DiscountText),
            nameof(TaxText), nameof(TotalText), nameof(BalanceText), nameof(StatusBanner), nameof(IsBilled), nameof(BillText), nameof(StateChips), nameof(StateChipsText), nameof(HasStateChips), nameof(CanEditLines), nameof(CanDiscount), nameof(CanComp), nameof(CanPriceOverride),
        })
        {
            OnPropertyChanged(name);
        }

        RaiseActions();
    }

    private void RaiseActions()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanServe));
        OnPropertyChanged(nameof(AwaitingService));
        OnPropertyChanged(nameof(CanPayOrder));
        OnPropertyChanged(nameof(CanSettleTab));
        OnPropertyChanged(nameof(CanVoid));
        OnPropertyChanged(nameof(CanEditLines));
        OnPropertyChanged(nameof(CanPrintBill));
        OnPropertyChanged(nameof(CanReopenBill));
        PrintBillCommand.RaiseCanExecuteChanged();
        ReopenBillCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        ServeCommand.RaiseCanExecuteChanged();
        PayCommand.RaiseCanExecuteChanged();
        SettleTabCommand.RaiseCanExecuteChanged();
        VoidCommand.RaiseCanExecuteChanged();
        AddProductCommand.RaiseCanExecuteChanged();
    }
}
