using R007.Pos.Core.Api;

namespace R007.Pos.Core.Terminal;

/// <summary>
/// Which screens/actions this terminal offers right now: a pure function of the facility's capabilities/operating
/// rules (from the device's facility) and the signed-in staff member's permissions. No station names appear anywhere:
/// a new facility is just a different capability set. These flags only hide/disable UI; the API enforces every rule.
/// </summary>
public sealed record TerminalFeatures(
    bool CanSell,
    bool HasRouting,
    bool ShowTables,
    bool CanOpenTabs,
    bool CanSend,
    bool CanAddLines,
    bool CanRemoveLines,
    bool CanPay,
    bool CanSplit,
    bool CanVoid,
    bool CanDiscount,
    bool CanComp,
    bool CanPriceOverride,
    bool CanRefund,
    bool CanReprint,
    bool CanViewHistory,
    bool CanManageCashSession,
    bool RequireCashSession,
    bool CanBook,
    bool CanIssueTickets,
    bool CanLookupCustomers,
    bool CanDecideApprovals,
    bool CanViewShiftReport,
    bool CanScanBarcodes,
    bool AllowOfflineOrders,
    string OfflinePayments,
    bool RequiresNfcAndPin)
{
    public static TerminalFeatures None { get; } = From(null, null, false);

    public bool AnyPosScreen => CanSell || ShowTables || CanBook || CanManageCashSession || CanViewHistory || CanDecideApprovals;

    public static TerminalFeatures From(FacilityCapabilities? caps, Staff? staff, bool requireNfcAndPin)
    {
        bool Cap(string c) => caps?.Has(c) == true;
        bool Perm(string p) => staff?.Has(p) == true;

        var pos = Cap(Capabilities.Pos);
        var acceptsPayment = Cap(Capabilities.PaymentAcceptance) || pos;
        var rules = caps?.OperatingRules;

        return new TerminalFeatures(
            CanSell: pos && Perm(Permissions.OrderCreate),
            HasRouting: Cap(Capabilities.KitchenRouting) || Cap(Capabilities.BarRouting),
            ShowTables: pos && (Cap(Capabilities.TableService) || Cap(Capabilities.OpenTab)) && (Perm(Permissions.TabView) || Perm(Permissions.OrderCreate)),
            CanOpenTabs: pos && Cap(Capabilities.OpenTab) && rules?.AllowOpenTabs != false && (Perm(Permissions.TabOpen) || Perm(Permissions.OrderCreate)),
            CanSend: (Cap(Capabilities.KitchenRouting) || Cap(Capabilities.BarRouting)) && Perm(Permissions.OrderSend),
            CanAddLines: Perm(Permissions.OrderLineAdd) || Perm(Permissions.OrderCreate),
            CanRemoveLines: Perm(Permissions.OrderLineRemoveUnsent) || Perm(Permissions.OrderCreate),
            CanPay: acceptsPayment && Perm(Permissions.PaymentTake),
            CanSplit: acceptsPayment && Perm(Permissions.PaymentSplit),
            CanVoid: Perm(Permissions.OrderVoidExecute) || Perm(Permissions.OrderVoidApprove),
            CanDiscount: Perm(Permissions.OrderDiscountExecute) || Perm(Permissions.OrderDiscountApprove),
            CanComp: Perm(Permissions.OrderCompExecute) || Perm(Permissions.OrderCompApprove),
            CanPriceOverride: Perm(Permissions.OrderPriceOverrideExecute) || Perm(Permissions.OrderPriceOverrideApprove),
            CanRefund: Perm(Permissions.RefundExecute) || Perm(Permissions.PaymentReversalExecute),
            CanReprint: Cap(Capabilities.ReceiptPrinting) && Perm(Permissions.ReceiptReprint),
            CanViewHistory: Perm(Permissions.PaymentView),
            CanManageCashSession: acceptsPayment && (Perm(Permissions.CashSessionOpen) || Perm(Permissions.CashSessionClose)),
            RequireCashSession: rules?.RequireCashSession == true,
            CanBook: (Cap(Capabilities.Booking) || Cap(Capabilities.Ticketing)) && Perm(Permissions.BookingCreate),
            CanIssueTickets: Cap(Capabilities.Ticketing) && Perm(Permissions.TicketIssue),
            CanLookupCustomers: Perm(Permissions.MembershipView),
            CanDecideApprovals: Permissions.ApprovalDecisions.Any(Perm),
            CanViewShiftReport: Perm(Permissions.CashSessionView) || Perm(Permissions.ReportView) || Perm(Permissions.CashSessionClose),
            CanScanBarcodes: Cap(Capabilities.BarcodeSales),
            AllowOfflineOrders: rules?.AllowOfflineOrders == true,
            OfflinePayments: rules?.AllowOfflinePayments ?? OfflinePaymentPolicy.None,
            RequiresNfcAndPin: requireNfcAndPin);
    }
}
