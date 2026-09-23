namespace R007.Pos.Core.Api;

public sealed record BookableResource(Guid Id, Guid FacilityId, string Name, string Mode, int? Capacity, int SlotMinutes, Guid? ProductId, decimal Price, bool? Active);

public sealed record Slot(DateTimeOffset Start, DateTimeOffset End, bool Available, int? RemainingCapacity, decimal? Price);

public sealed record Availability(Guid ResourceId, DateTimeOffset? From, DateTimeOffset? To, IReadOnlyList<Slot> Slots);

public sealed record CustomerInput(string Name, string? Phone = null, string? Email = null, Guid? MembershipId = null);

public sealed record HoldRequest(Guid ResourceId, DateTimeOffset Start, DateTimeOffset End, int? Quantity = null, CustomerInput? Customer = null);

public static class BookingStatuses
{
    public const string Held = "HELD";
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Confirmed = "CONFIRMED";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";
}

public sealed record Booking(
    Guid Id,
    string Number,
    Guid ResourceId,
    string? ResourceName,
    Guid? FacilityId,
    DateTimeOffset Start,
    DateTimeOffset End,
    int? Quantity,
    string Status,
    DateTimeOffset? HoldExpiresAt,
    decimal Total,
    decimal AmountPaid,
    Guid? OrderId,
    Guid? EntitlementId,
    int RowVersion);

public sealed record ConfirmBookingRequest(IReadOnlyList<TenderInput>? Tenders = null, Guid? CashSessionId = null, string? PaystackReference = null);

public sealed record CancelBookingRequest(string Reason);

public sealed record EntitlementItem(
    Guid Id,
    string Kind,
    string Name,
    Guid FacilityId,
    int Quantity,
    int QuantityRedeemed,
    string ValidationMode,
    string? RentalStatus);

public sealed record Entitlement(
    Guid Id,
    string QrToken,
    string Status,
    Guid? OrderId,
    Guid? BookingId,
    string? HolderName,
    IReadOnlyList<EntitlementItem> Items,
    DateTimeOffset IssuedAt,
    IReadOnlyList<Guid>? GroupEntitlementIds = null);

public sealed record IssueEntitlementRequest(Guid? OrderId = null, Guid? BookingId = null);

/// <summary><c>POST /bookings/{id}/order</c>: attach the order (slot fee + rentals + goods) that will pay for the hold.</summary>
public sealed record AttachOrderRequest(Guid OrderId);
