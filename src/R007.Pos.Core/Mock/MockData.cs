using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

/// <summary>Seed data for the in-memory mock server (demo + tests). Deterministic ids so tests can refer to them.</summary>
public static class MockData
{
    public const string SupervisorPin = "9999";
    public const string CashierPin = "1234";
    public const string WaiterPin = "1111";
    public const string SupervisorNfc = "04FFEE0011";
    public const string CashierNfc = "04A1B2C3D4";

    public static readonly Guid RestaurantFacilityId = Guid.Parse("00000000-0000-7000-8000-000000000101");
    public static readonly Guid ClubFacilityId = Guid.Parse("00000000-0000-7000-8000-000000000102");
    public static readonly Guid ReceptionFacilityId = Guid.Parse("00000000-0000-7000-8000-000000000103");

    public static readonly Guid Beer = Guid.Parse("00000000-0000-7000-8000-000000001001");
    public static readonly Guid Soda = Guid.Parse("00000000-0000-7000-8000-000000001002");
    public static readonly Guid Water = Guid.Parse("00000000-0000-7000-8000-000000001003");
    public static readonly Guid Jollof = Guid.Parse("00000000-0000-7000-8000-000000001004");
    public static readonly Guid Suya = Guid.Parse("00000000-0000-7000-8000-000000001005");
    public static readonly Guid Fries = Guid.Parse("00000000-0000-7000-8000-000000001006");
    public static readonly Guid Biscuits = Guid.Parse("00000000-0000-7000-8000-000000001007");
    public static readonly Guid Racket = Guid.Parse("00000000-0000-7000-8000-000000001101");
    public static readonly Guid Towel = Guid.Parse("00000000-0000-7000-8000-000000001102");
    public static readonly Guid PoolTicket = Guid.Parse("00000000-0000-7000-8000-000000001103");
    public static readonly Guid TennisFee = Guid.Parse("00000000-0000-7000-8000-000000001104");

    public static readonly Guid CatDrinks = Guid.Parse("00000000-0000-7000-8000-000000002001");
    public static readonly Guid CatFood = Guid.Parse("00000000-0000-7000-8000-000000002002");
    public static readonly Guid CatRetail = Guid.Parse("00000000-0000-7000-8000-000000002003");
    public static readonly Guid CatRentals = Guid.Parse("00000000-0000-7000-8000-000000002004");

    public static readonly Guid TennisCourt = Guid.Parse("00000000-0000-7000-8000-000000004001");
    public static readonly Guid SwimmingPool = Guid.Parse("00000000-0000-7000-8000-000000004002");

    public static IReadOnlyList<Category> Categories { get; } =
    [
        new(CatDrinks, null, "Drinks", 1),
        new(CatFood, null, "Food", 2),
        new(CatRetail, null, "Retail", 3),
        new(CatRentals, null, "Tickets & Rentals", 4),
    ];

    private static Product P(Guid id, string sku, string name, Guid cat, string kind, decimal price, string route = "NONE") =>
        new(id, sku, name, cat, kind, price, "NGN", false, null, null, new PrepRoute(null, null, route), true, true);

    public static IReadOnlyList<Product> Products { get; } =
    [
        P(Beer, "DRK-BEER", "Star Lager 60cl", CatDrinks, ProductKinds.Drink, 1500m, "BAR"),
        P(Soda, "DRK-SODA", "Coca-Cola 50cl", CatDrinks, ProductKinds.Drink, 500m, "BAR"),
        P(Water, "DRK-WATR", "Bottled Water", CatDrinks, ProductKinds.Drink, 300m, "BAR"),
        P(Jollof, "FD-JOLLOF", "Jollof Rice", CatFood, ProductKinds.Food, 3500m, "KITCHEN"),
        P(Suya, "FD-SUYA", "Beef Suya", CatFood, ProductKinds.Food, 2500m, "KITCHEN"),
        P(Fries, "FD-FRIES", "Fries", CatFood, ProductKinds.Food, 1800m, "KITCHEN"),
        P(Biscuits, "RTL-BISC", "Biscuits Pack", CatRetail, ProductKinds.Retail, 700m),
        P(Racket, "RNT-RACKET", "Racket Rental", CatRentals, ProductKinds.Rental, 1000m),
        P(Towel, "RNT-TOWEL", "Towel Rental", CatRentals, ProductKinds.Rental, 500m),
        P(PoolTicket, "TKT-POOL", "Pool Day Ticket", CatRentals, ProductKinds.Ticket, 3000m),
        P(TennisFee, "FEE-TENNIS", "Tennis court (per hour)", CatRentals, ProductKinds.Service, 5000m),
    ];

    /// <summary>Barcodes the mock server resolves through the product search (<c>q=</c>).</summary>
    public static IReadOnlyDictionary<string, Guid> Barcodes { get; } = new Dictionary<string, Guid>
    {
        ["6001234500011"] = Beer,
        ["6001234500028"] = Soda,
        ["6001234500035"] = Water,
        ["6001234500042"] = Jollof,
        ["6001234500059"] = Suya,
        ["6001234500066"] = Fries,
        ["6001234500073"] = Biscuits,
        ["6001234500110"] = Racket,
        ["6001234500127"] = Towel,
        ["6001234500134"] = PoolTicket,
    };

    private static OperatingRules Rules(bool tabs, bool cashSession, string offlinePayments) =>
        new(null, null, tabs, cashSession, true, offlinePayments, 300, false, "7.5");

    public static FacilityCapabilities Restaurant { get; } = new(
        RestaurantFacilityId,
        [Capabilities.Pos, Capabilities.TableService, Capabilities.OpenTab, Capabilities.KitchenRouting, Capabilities.ReceiptPrinting, Capabilities.PaymentAcceptance, Capabilities.BarcodeSales],
        Rules(true, false, OfflinePaymentPolicy.CashOnly));

    public static FacilityCapabilities IndoorClub { get; } = new(
        ClubFacilityId,
        [Capabilities.Pos, Capabilities.TableService, Capabilities.OpenTab, Capabilities.BarRouting, Capabilities.ReceiptPrinting, Capabilities.PaymentAcceptance, Capabilities.Membership],
        Rules(true, true, OfflinePaymentPolicy.CashOnly));

    public static FacilityCapabilities Reception { get; } = new(
        ReceptionFacilityId,
        [Capabilities.Pos, Capabilities.Ticketing, Capabilities.Booking, Capabilities.EquipmentRental, Capabilities.Membership, Capabilities.ReceiptPrinting, Capabilities.PaymentAcceptance, Capabilities.BarcodeSales],
        Rules(false, true, OfflinePaymentPolicy.CashOnly));

    public static string FacilityName(Guid id) => id == ClubFacilityId ? "Indoor Club" : id == ReceptionFacilityId ? "Main Reception" : "Restaurant";

    /// <summary>Registration code -> facility. Any other code enrols as the Restaurant; <c>BAD</c> is rejected.</summary>
    public static FacilityCapabilities FacilityForEnrollmentCode(string code) =>
        code.Trim().ToUpperInvariant() switch
        {
            "RECEPTION" => Reception,
            "CLUB" => IndoorClub,
            _ => Restaurant,
        };

    public static IReadOnlyList<string> CashierPermissions { get; } =
    [
        Permissions.OrderCreate, Permissions.OrderSend, Permissions.OrderSettle, Permissions.PaymentTake, Permissions.PaymentSplit,
        Permissions.CashSessionOpen, Permissions.CashSessionClose, Permissions.ReceiptReprint,
        Permissions.OrderVoidExecute, Permissions.OrderDiscountExecute, Permissions.OrderCompExecute, Permissions.OrderPriceOverrideExecute,
        Permissions.RefundExecute, Permissions.PaymentReversalExecute, Permissions.TabView, Permissions.TabOpen, Permissions.MembershipView,
        Permissions.BookingCreate, Permissions.TicketIssue, Permissions.OrderView, Permissions.PaymentView, Permissions.ReceiptView, Permissions.CashSessionView,
        Permissions.OrderLineAdd, Permissions.OrderLineRemoveUnsent, Permissions.ReportView,
    ];

    public static IReadOnlyList<string> WaiterPermissions { get; } =
        [Permissions.OrderCreate, Permissions.OrderLineAdd, Permissions.OrderLineRemoveUnsent, Permissions.OrderSend, Permissions.TabView, Permissions.TabOpen, Permissions.MembershipView, Permissions.OrderView];

    public static IReadOnlyList<string> SupervisorPermissions { get; } =
    [
        .. CashierPermissions,
        Permissions.OrderVoidApprove, Permissions.OrderDiscountApprove, Permissions.OrderCompApprove,
        Permissions.OrderPriceOverrideApprove, Permissions.RefundApprove, Permissions.PaymentReversalApprove,
    ];
}
