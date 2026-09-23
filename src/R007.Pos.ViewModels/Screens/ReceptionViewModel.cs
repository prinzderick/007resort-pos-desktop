using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.ViewModels.Screens;

public sealed class SlotRow(Slot slot)
{
    public Slot Slot { get; } = slot;

    public string TimeText => $"{Slot.Start.ToOffset(TimeSpan.FromHours(1)):HH:mm}-{Slot.End.ToOffset(TimeSpan.FromHours(1)):HH:mm}";

    public string PriceText => Slot.Price is { } p ? MoneyFormat.Display(p) : string.Empty;

    public string Availability => !Slot.Available ? "Full" : Slot.RemainingCapacity is { } r ? $"{r} left" : "Open";

    public bool CanBook => Slot.Available;
}

public sealed class RentalRow(Product product)
{
    public Product Product { get; } = product;

    public string Name => Product.Name;

    public string PriceText => MoneyFormat.Display(Product.Price, Product.Currency);
}

/// <summary>
/// Main Reception: sports/pool booking, ticket and equipment-rental sales. Pick a resource and slot, hold it (409 if
/// someone else just took it), optionally add rental items, then take payment: paying confirms the booking and prints a
/// receipt with the QR entitlement that the Sports Entrance and Store scan. Bookings need a live connection.
/// </summary>
public sealed class ReceptionViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private readonly INavigator _nav;
    private BookableResource? _resource;
    private SlotRow? _slot;
    private DateTimeOffset _day;
    private int _partySize = 1;
    private Membership? _member;
    private string _customerName = string.Empty;
    private Booking? _booking;
    private decimal _balanceDue;

    public ReceptionViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        _nav = nav;
        _day = ctx.Time.GetUtcNow().Date is var d ? new DateTimeOffset(d, TimeSpan.Zero) : default;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadResourcesAsync), null, SetError);
        LoadSlotsCommand = new AsyncRelayCommand(() => RunAsync(LoadSlotsAsync), () => Resource is not null, SetError);
        HoldCommand = new AsyncRelayCommand(HoldAsync, () => CanHold, SetError);
        AddRentalCommand = new AsyncRelayCommand(p => AddRentalAsync(p as RentalRow), _ => Booking is not null, SetError);
        PayCommand = new AsyncRelayCommand(PayAsync, () => CanPay, SetError);
        CancelHoldCommand = new AsyncRelayCommand(CancelHoldAsync, () => Booking is not null, SetError);
        LookupMemberCommand = new AsyncRelayCommand(LookupMemberAsync, () => _ctx.Features.CanLookupCustomers, SetError);
        NextDayCommand = new AsyncRelayCommand(() => ShiftDayAsync(1), null, SetError);
        PreviousDayCommand = new AsyncRelayCommand(() => ShiftDayAsync(-1), null, SetError);
        IncreasePartyCommand = new RelayCommand(() => PartySize = Math.Min(50, PartySize + 1));
        DecreasePartyCommand = new RelayCommand(() => PartySize = Math.Max(1, PartySize - 1));
    }

    public ObservableCollection<BookableResource> Resources { get; } = [];

    public ObservableCollection<SlotRow> Slots { get; } = [];

    public IReadOnlyList<RentalRow> Rentals => [.. _ctx.Products.Where(p => p.Active && p.Kind == ProductKinds.Rental).Select(p => new RentalRow(p))];

    public BookableResource? Resource
    {
        get => _resource;
        set
        {
            if (SetProperty(ref _resource, value))
            {
                Slot = null;
                Slots.Clear();
                LoadSlotsCommand.RaiseCanExecuteChanged();
                if (value is not null)
                {
                    _ = RunAsync(LoadSlotsAsync);
                }
            }
        }
    }

    public SlotRow? Slot
    {
        get => _slot;
        set
        {
            if (SetProperty(ref _slot, value))
            {
                HoldCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanHold));
            }
        }
    }

    public string DayText => _day.ToString("ddd dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public int PartySize
    {
        get => _partySize;
        set => SetProperty(ref _partySize, value);
    }

    public Membership? Member
    {
        get => _member;
        private set
        {
            if (SetProperty(ref _member, value))
            {
                OnPropertyChanged(nameof(MemberText));
            }
        }
    }

    public string MemberText => _member is null ? "No member linked" : $"{_member.HolderName} ({_member.Number}, {_member.Status})";

    public string CustomerName
    {
        get => _customerName;
        set => SetProperty(ref _customerName, value);
    }

    public Booking? Booking
    {
        get => _booking;
        private set
        {
            if (SetProperty(ref _booking, value))
            {
                OnPropertyChanged(nameof(HasBooking));
                OnPropertyChanged(nameof(BookingText));
                OnPropertyChanged(nameof(CanPay));
                OnPropertyChanged(nameof(CanHold));
                HoldCommand.RaiseCanExecuteChanged();
                PayCommand.RaiseCanExecuteChanged();
                CancelHoldCommand.RaiseCanExecuteChanged();
                AddRentalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasBooking => _booking is not null;

    /// <summary>Server totals: booking price plus any rentals, exactly as the API reports the order balance.</summary>
    public string BookingText => _booking is null
        ? string.Empty
        : $"{_booking.Number}: {_booking.ResourceName} {_booking.Start.ToOffset(TimeSpan.FromHours(1)):HH:mm}-{_booking.End.ToOffset(TimeSpan.FromHours(1)):HH:mm}, {MoneyFormat.Display(_balanceDue)} to pay";

    public bool CanHold => Booking is null && Resource is not null && Slot is { CanBook: true } && _ctx.Features.CanBook;

    public bool CanPay => Booking is not null && _ctx.Features.CanPay;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand LoadSlotsCommand { get; }

    public AsyncRelayCommand HoldCommand { get; }

    public AsyncRelayCommand AddRentalCommand { get; }

    public AsyncRelayCommand PayCommand { get; }

    public AsyncRelayCommand CancelHoldCommand { get; }

    public AsyncRelayCommand LookupMemberCommand { get; }

    public AsyncRelayCommand NextDayCommand { get; }

    public AsyncRelayCommand PreviousDayCommand { get; }

    public RelayCommand IncreasePartyCommand { get; }

    public RelayCommand DecreasePartyCommand { get; }

    public override Task ActivateAsync() => RunAsync(LoadResourcesAsync);

    private async Task LoadResourcesAsync()
    {
        var found = await _ctx.Api.GetBookableResourcesAsync().ConfigureAwait(true);
        Resources.Clear();
        foreach (var r in found.Where(r => r.Active != false))
        {
            Resources.Add(r);
        }

        OnPropertyChanged(nameof(Rentals));
    }

    private async Task LoadSlotsAsync()
    {
        if (Resource is null)
        {
            return;
        }

        var availability = await _ctx.Api.GetAvailabilityAsync(Resource.Id, _day, _day.AddDays(1)).ConfigureAwait(true);
        Slots.Clear();
        foreach (var s in availability.Slots)
        {
            Slots.Add(new SlotRow(s));
        }
    }

    private async Task ShiftDayAsync(int days)
    {
        _day = _day.AddDays(days);
        OnPropertyChanged(nameof(DayText));
        await RunAsync(LoadSlotsAsync).ConfigureAwait(true);
    }

    private async Task LookupMemberAsync()
    {
        var modal = new CustomerLookupViewModel(_ctx);
        if (await _nav.ShowModalAsync(modal).ConfigureAwait(true) && modal.Selected is { } m)
        {
            Member = m;
            CustomerName = m.HolderName;
        }
    }

    /// <summary>
    /// Hold the slot, then build the paying order the way the node expects: the resource's slot-fee product (per slot, or per seat for
    /// capacity resources) goes on a counter order which is attached to the booking (<c>POST /bookings/{id}/order</c>). Rentals and goods
    /// are further lines on that same order; paying it confirms the booking.
    /// </summary>
    private async Task HoldAsync()
    {
        Error = null;
        var resource = Resource!;
        if (resource.ProductId is not { } feeProduct)
        {
            Error = $"{resource.Name} has no price configured (no slot-fee product). Ask IT to set it up.";
            return;
        }

        var perSeat = resource.Mode == "INDIVIDUAL_CAPACITY";
        var name = string.IsNullOrWhiteSpace(CustomerName) ? "Walk-in" : CustomerName.Trim();
        var request = new HoldRequest(resource.Id, Slot!.Slot.Start, Slot.Slot.End, perSeat ? PartySize : null, new CustomerInput(name, null, null, Member?.Id));
        Booking held;
        try
        {
            held = await _ctx.Api.HoldBookingAsync(request, IdempotencyKeys.New()).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ex.Code == "slot_unavailable")
        {
            Error = Describe(ex);
            await LoadSlotsAsync().ConfigureAwait(true);
            return;
        }
        catch (ApiUnavailableException)
        {
            Error = "Bookings need a live connection to the server (a slot is scarce, so it cannot be queued). Try again when it returns.";
            return;
        }

        try
        {
            var line = new OrderLineInput(feeProduct, perSeat ? PartySize : 1, null, ClientIds.New(), _ctx.Time.GetUtcNow());
            var order = await _ctx.Api.CreateOrderAsync(new CreateOrderRequest(_ctx.FacilityId, null, null, OrderChannels.Counter, name, [line], ClientIds.New(), _ctx.Time.GetUtcNow()), IdempotencyKeys.New()).ConfigureAwait(true);
            Booking = await _ctx.Api.AttachBookingOrderAsync(held.Id, held.RowVersion, order.Id, IdempotencyKeys.New()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // Never leave a slot held for nothing.
            await TryReleaseAsync(held).ConfigureAwait(true);
            Error = Describe(ex);
            return;
        }

        await RefreshBalanceAsync().ConfigureAwait(true);
        Info = "Slot held. Add rentals if needed, then take payment before the hold expires.";
    }

    private async Task TryReleaseAsync(Booking held)
    {
        try
        {
            var fresh = await _ctx.Api.GetBookingAsync(held.Id).ConfigureAwait(true);
            await _ctx.Api.CancelBookingAsync(held.Id, fresh.RowVersion, new CancelBookingRequest("Order could not be created"), IdempotencyKeys.New()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // The hold simply expires on its own.
        }
    }

    private async Task RefreshBalanceAsync()
    {
        if (Booking?.OrderId is { } orderId)
        {
            _balanceDue = (await _ctx.Api.GetOrderAsync(orderId).ConfigureAwait(true)).BalanceDue;
        }
        else if (Booking is not null)
        {
            _balanceDue = Booking.Total - Booking.AmountPaid; // only until the order is attached
        }

        OnPropertyChanged(nameof(BookingText));
    }

    private async Task AddRentalAsync(RentalRow? rental)
    {
        if (rental is null || Booking?.OrderId is not { } orderId)
        {
            Error = "This booking has no order to add rentals to.";
            return;
        }

        Error = null;
        var order = await _ctx.Api.GetOrderAsync(orderId).ConfigureAwait(true);
        await _ctx.Api.AddLineAsync(orderId, order.RowVersion, new OrderLineInput(rental.Product.Id, 1, null, ClientIds.New(), _ctx.Time.GetUtcNow()), IdempotencyKeys.New()).ConfigureAwait(true);
        await RefreshBalanceAsync().ConfigureAwait(true);
        Info = $"Added {rental.Name}.";
    }

    private async Task PayAsync()
    {
        Error = null;
        await RefreshBalanceAsync().ConfigureAwait(true);
        var facility = _ctx.FacilityId;
        var modal = new PaymentViewModel(_ctx, PaymentTarget.ForBooking(Booking!, facility, _balanceDue));
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        if (modal.Paid)
        {
            Info = $"Booking confirmed. {modal.PrintMessage}";
            Booking = null;
            Slot = null;
            CustomerName = string.Empty;
            Member = null;
            PartySize = 1;
            await RunAsync(LoadSlotsAsync).ConfigureAwait(true);
        }
    }

    private async Task CancelHoldAsync()
    {
        if (Booking is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var fresh = await _ctx.Api.GetBookingAsync(Booking.Id).ConfigureAwait(true);
            await _ctx.Api.CancelBookingAsync(Booking.Id, fresh.RowVersion, new CancelBookingRequest("Cancelled at reception"), IdempotencyKeys.New()).ConfigureAwait(true);
            Booking = null;
            Info = "Hold released.";
            await LoadSlotsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }
}
