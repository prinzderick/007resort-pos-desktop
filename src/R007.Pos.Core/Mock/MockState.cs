using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

internal sealed class MStaff
{
    public required Guid Id { get; init; }

    public required string Display { get; init; }

    public required string Number { get; init; }

    public required string Username { get; init; }

    public required string Pin { get; init; }

    public required string Nfc { get; init; }

    public required IReadOnlyList<string> Permissions { get; init; }

    public Staff ToDto() => new(Id, Display, Number, ["MOCK_ROLE"], Permissions, null);
}

internal sealed class MAdjustment
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Kind { get; init; }

    public required string Value { get; init; }

    public required string Reason { get; init; }

    public decimal Amount { get; set; }

    public string Status { get; set; } = "APPLIED";

    public Guid? ApprovalId { get; set; }
}

internal sealed class MLine
{
    public required Guid Id { get; init; }

    public required Guid ProductId { get; init; }

    public required string Name { get; init; }

    public required int Quantity { get; init; }

    public required decimal BaseUnitPrice { get; init; }

    public decimal? OverridePrice { get; set; }

    public string? Notes { get; init; }

    public string Status { get; set; } = "PENDING";

    public string Route { get; init; } = "NONE";

    public string Kind { get; init; } = ProductKinds.Retail;

    public List<MAdjustment> Adjustments { get; } = [];

    public decimal UnitPrice => OverridePrice ?? BaseUnitPrice;

    public decimal Gross => UnitPrice * Quantity;

    public decimal Discount => Math.Min(Gross, Adjustments.Where(a => a.Status == "APPLIED").Sum(a => a.Amount));

    public decimal Net => Gross - Discount;
}

internal sealed class MOrder
{
    public required Guid Id { get; init; }

    public required string Number { get; init; }

    public required Guid FacilityId { get; init; }

    public Guid? TableId { get; set; }

    public Guid? TabId { get; set; }

    public string? CustomerName { get; init; }

    public string? Channel { get; init; }

    public string Status { get; set; } = OrderStatuses.Draft;

    public string? StatusBeforeApproval { get; set; }

    public List<MLine> Lines { get; } = [];

    public decimal AmountPaid { get; set; }

    public int RowVersion { get; set; } = 1;

    public Guid? PendingApprovalId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? BookingId { get; set; }

    public Guid CreatedBy { get; init; }
}

internal sealed class MTab
{
    public required Guid Id { get; init; }

    public required Guid FacilityId { get; init; }

    public Guid? TableId { get; init; }

    public string? CustomerName { get; init; }

    public string Status { get; set; } = "OPEN";

    public List<Guid> OrderIds { get; } = [];

    public DateTimeOffset OpenedAt { get; init; }

    public int RowVersion { get; set; } = 1;
}

internal sealed class MApproval
{
    public required Guid Id { get; init; }

    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    public required Guid FacilityId { get; init; }

    public string Status { get; set; } = ApprovalStatuses.Pending;

    public required Guid RequestedBy { get; init; }

    public required string RequestedByName { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required string Reason { get; init; }

    public decimal? Amount { get; init; }

    public required string Summary { get; init; }

    public required string RequiredPermission { get; init; }

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? Note { get; set; }

    /// <summary>Applies the held action when approved.</summary>
    public Action? OnApprove { get; init; }

    /// <summary>Reverts the held state when rejected/cancelled.</summary>
    public Action? OnReject { get; init; }

    public Approval ToDto() => new(Id, Action, EntityType, EntityId, FacilityId, Status, RequestedBy, RequestedByName, RequestedAt, Reason, Amount, Summary, DecidedBy, DecidedAt, Note, RequiredPermission);
}

internal sealed class MPayment
{
    public required Guid Id { get; init; }

    public required Guid GroupId { get; init; }

    public required Guid FacilityId { get; init; }

    public required string Tender { get; init; }

    public string? Reference { get; init; }

    public string? Provider { get; init; }

    public string? ProviderReference { get; init; }

    public string Status { get; set; } = PaymentStatuses.Captured;

    public required decimal Amount { get; init; }

    public decimal? Tendered { get; init; }

    public decimal? Change { get; init; }

    public decimal Refunded { get; set; }

    public List<PaymentAllocation> Allocations { get; } = [];

    public Guid? CashSessionId { get; init; }

    public Guid ReceiptId { get; set; }

    public required Guid TakenBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Payment ToDto() => new(Id, GroupId, FacilityId, Tender, Provider, ProviderReference, Reference, Status, Amount, Tendered, Change, Refunded, "NGN", Allocations, CashSessionId, ReceiptId, TakenBy, CreatedAt, Status == PaymentStatuses.Captured ? CreatedAt : null);
}

internal sealed class MCashSession
{
    public required Guid Id { get; init; }

    public required Guid FacilityId { get; init; }

    public required Guid StaffId { get; init; }

    public string Status { get; set; } = "OPEN";

    public required decimal OpeningFloat { get; init; }

    public decimal? Counted { get; set; }

    public required DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; set; }
}

internal sealed class MBooking
{
    public required Guid Id { get; init; }

    public required string Number { get; init; }

    public required BookableResource Resource { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset End { get; init; }

    public int Quantity { get; init; } = 1;

    public string Status { get; set; } = BookingStatuses.Held;

    public required DateTimeOffset HoldExpiresAt { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? EntitlementId { get; set; }

    public int RowVersion { get; set; } = 1;
}

internal sealed class MIdempotent
{
    public required string Fingerprint { get; init; }

    public required int Status { get; init; }

    public required byte[] Body { get; init; }
}
