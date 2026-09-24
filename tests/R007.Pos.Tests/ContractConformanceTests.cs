using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using R007.Pos.Core.Api;

namespace R007.Pos.Tests;

/// <summary>
/// Guards the client against drifting from the API contract (007resort-docs <c>api/openapi/v1.yaml</c>, copied fixtures in
/// <c>ContractFixtures/</c>): real example payloads from the spec must parse into our types, every property we send must
/// exist in the spec's request schema (and every required one must be sent), and our response types must model every
/// property the spec marks required.
/// </summary>
public sealed class ContractConformanceTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ContractFixtures", name + ".json"));

    private static JsonNode Schemas() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ContractFixtures", "schemas.json")))!;

    private static PosJsonContext Ctx => PosJsonContext.Default;

    // Responses ---------------------------------------------------------------------------------------------------
    [Fact]
    public void AuthResult_Example_Parses()
    {
        var auth = JsonSerializer.Deserialize(Fixture("AuthResult__ok"), Ctx.AuthResult)!;

        Assert.False(string.IsNullOrEmpty(auth.AccessToken));
        Assert.False(string.IsNullOrEmpty(auth.RefreshToken));
        Assert.True(auth.ExpiresInSeconds > 0);
        Assert.Equal("Amaka Okafor", auth.Staff.DisplayName);
        Assert.Contains("order.send", auth.Staff.Permissions);
        Assert.NotNull(auth.Session);
    }

    [Theory]
    [InlineData("Order__created")]
    [InlineData("Order__sent")]
    public void Order_Examples_ParseWithDecimalMoney(string fixture)
    {
        var order = JsonSerializer.Deserialize(Fixture(fixture), Ctx.Order)!;

        Assert.NotEmpty(order.Lines);
        Assert.True(order.Total > 0m);
        Assert.Equal("NGN", order.Currency);
        Assert.True(order.RowVersion >= 1);
        Assert.Equal(order.Total - order.AmountPaid, order.BalanceDue);
        Assert.All(order.Lines, l => Assert.True(l.Quantity >= 1));
    }

    [Fact]
    public void PaymentResult_Example_Parses()
    {
        var result = JsonSerializer.Deserialize(Fixture("PaymentResult__ok"), Ctx.PaymentResult)!;

        var payment = Assert.Single(result.Payments);
        Assert.Equal(TenderTypes.Cash, payment.TenderType);
        Assert.Equal(9000m, payment.Amount);
        Assert.Equal(10000m, payment.Tendered);
        Assert.Equal(1000m, payment.ChangeGiven);
        Assert.Equal(1000m, result.ChangeDue);
        Assert.NotEqual(Guid.Empty, result.ReceiptId);
        Assert.Equal(PaymentStatuses.Captured, payment.Status);
    }

    [Fact]
    public void ApprovalOutcome_202Example_Parses()
    {
        var outcome = JsonSerializer.Deserialize(Fixture("ApprovalOutcome__pending"), Ctx.ApprovalOutcome)!;

        Assert.Equal("PENDING_APPROVAL", outcome.Status);
        Assert.True(outcome.Approval.IsPending);
        Assert.False(string.IsNullOrEmpty(outcome.Approval.Action));
    }

    [Fact]
    public void FacilityCapabilities_Example_Parses()
    {
        var caps = JsonSerializer.Deserialize(Fixture("FacilityCapabilities__c"), Ctx.FacilityCapabilities)!;

        Assert.NotEmpty(caps.Capabilities);
        Assert.NotNull(caps.OperatingRules);
    }

    [Fact]
    public void ProductPage_Availability_Tables_Approvals_Entitlement_Examples_Parse()
    {
        var products = JsonSerializer.Deserialize(Fixture("ProductPage__p"), Ctx.PageProduct)!;
        Assert.NotEmpty(products.Items);
        Assert.All(products.Items, p => Assert.True(p.Price > 0m));

        var availability = JsonSerializer.Deserialize(Fixture("Availability__a"), Ctx.Availability)!;
        Assert.NotEmpty(availability.Slots);

        var tables = JsonSerializer.Deserialize(Fixture("DiningTablePage__t"), Ctx.PageDiningTable)!;
        Assert.NotEmpty(tables.Items);

        var approvals = JsonSerializer.Deserialize(Fixture("ApprovalPage__a"), Ctx.PageApproval)!;
        Assert.NotEmpty(approvals.Items);

        var entitlement = JsonSerializer.Deserialize(Fixture("Entitlement__e"), Ctx.Entitlement)!;
        Assert.False(string.IsNullOrEmpty(entitlement.QrToken));
        Assert.NotEmpty(entitlement.Items);
    }

    // Requests ----------------------------------------------------------------------------------------------------
    [Fact]
    public void RequestExamples_DeserialiseIntoOurRequestTypes()
    {
        var cash = JsonSerializer.Deserialize(Fixture("CreatePaymentRequest__cash"), Ctx.CreatePaymentRequest)!;
        Assert.Single(cash.Tenders);
        Assert.Equal(TenderTypes.Cash, cash.Tenders[0].TenderType);
        Assert.NotEmpty(cash.Allocations);

        var split = JsonSerializer.Deserialize(Fixture("CreatePaymentRequest__split"), Ctx.CreatePaymentRequest)!;
        Assert.True(split.Tenders.Count > 1);
        Assert.Equal(split.Allocations.Sum(a => a.Amount), split.Tenders.Sum(t => t.Amount)); // contract: tenders add up to allocations

        var pin = JsonSerializer.Deserialize(Fixture("StaffLoginRequest__pin"), Ctx.StaffLoginRequest)!;
        Assert.Equal(CredentialTypes.Pin, pin.CredentialType);
        var nfc = JsonSerializer.Deserialize(Fixture("StaffLoginRequest__nfc"), Ctx.StaffLoginRequest)!;
        Assert.Equal(CredentialTypes.NfcCard, nfc.CredentialType);
        Assert.Equal("04A2246B7C5E80", nfc.Identifier); // card uid = identifier, PIN = secret (verified against the real node)
        Assert.Equal("1234", nfc.Secret);

        var create = JsonSerializer.Deserialize(Fixture("CreateOrderRequest__dinein"), Ctx.CreateOrderRequest)!;
        Assert.Equal("DINE_IN", create.Channel);
        Assert.NotNull(JsonSerializer.Deserialize(Fixture("OrderLineInput__l"), Ctx.OrderLineInput));
        Assert.NotNull(JsonSerializer.Deserialize(Fixture("SettleTabRequest__s"), Ctx.SettleTabRequest));
        Assert.NotNull(JsonSerializer.Deserialize(Fixture("HoldRequest__h"), Ctx.HoldRequest));
        Assert.NotNull(JsonSerializer.Deserialize(Fixture("ConfirmBookingRequest__cash"), Ctx.ConfirmBookingRequest));
        Assert.NotNull(JsonSerializer.Deserialize(Fixture("AdjustmentRequest__d"), Ctx.AdjustmentRequest));
        var stepUp = JsonSerializer.Deserialize(Fixture("StepUpRequest__sup"), Ctx.StepUpRequest)!;
        Assert.False(string.IsNullOrEmpty(stepUp.Permission));
    }

    private static readonly Guid G = Guid.CreateVersion7();
    private static readonly DateTimeOffset T = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static IEnumerable<(string Schema, string Json)> PopulatedRequests()
    {
        var tender = new TenderInput(TenderTypes.Cash, 1m, "ref", 2m, G, T);
        var customer = new CustomerInput("N", "p", "e@x.com", G);
        yield return ("StaffLoginRequest", JsonSerializer.Serialize(new StaffLoginRequest(CredentialTypes.Pin, "S-1", "1234"), Ctx.StaffLoginRequest));
        yield return ("StepUpRequest", JsonSerializer.Serialize(new StepUpRequest(CredentialTypes.Pin, "S-1", "1", "order.void.approve", "order", G), Ctx.StepUpRequest));
        yield return ("DeviceRegisterRequest", JsonSerializer.Serialize(new DeviceRegisterRequest("n", DeviceKinds.PosTerminal, "hw", "code", "windows", "1.0.0"), Ctx.DeviceRegisterRequest));
        yield return ("CreateOrderRequest", JsonSerializer.Serialize(new CreateOrderRequest(G, G, G, "DINE_IN", "c", [new OrderLineInput(G, 1, "n", G, T)], G, T), Ctx.CreateOrderRequest));
        yield return ("OrderLineInput", JsonSerializer.Serialize(new OrderLineInput(G, 1, "n", G, T), Ctx.OrderLineInput));
        yield return ("SendOrderRequest", JsonSerializer.Serialize(new SendOrderRequest([G]), Ctx.SendOrderRequest));
        yield return ("VoidRequest", JsonSerializer.Serialize(new VoidRequest("r"), Ctx.VoidRequest));
        yield return ("AdjustmentRequest", JsonSerializer.Serialize(new AdjustmentRequest(AdjustmentKinds.DiscountPercent, "10.0000", "r"), Ctx.AdjustmentRequest));
        yield return ("ApprovalDecisionRequest", JsonSerializer.Serialize(new ApprovalDecisionRequest(ApprovalDecisions.Approve, "n", "t"), Ctx.ApprovalDecisionRequest));
        yield return ("OpenTabRequest", JsonSerializer.Serialize(new OpenTabRequest(G, G, "c", [G], G, T), Ctx.OpenTabRequest));
        yield return ("AddTabOrdersRequest", JsonSerializer.Serialize(new AddTabOrdersRequest([G]), Ctx.AddTabOrdersRequest));
        yield return ("SettleTabRequest", JsonSerializer.Serialize(new SettleTabRequest([tender], G), Ctx.SettleTabRequest));
        yield return ("CreatePaymentRequest", JsonSerializer.Serialize(new CreatePaymentRequest(G, [new AllocationInput(G, 1m)], [tender], G, G, "c", T), Ctx.CreatePaymentRequest));
        yield return ("RefundRequest", JsonSerializer.Serialize(new RefundRequest(1m, "r", TenderTypes.Cash), Ctx.RefundRequest));
        yield return ("ReversalRequest", JsonSerializer.Serialize(new ReversalRequest("r"), Ctx.ReversalRequest));
        yield return ("PaystackInitRequest", JsonSerializer.Serialize(new PaystackInitRequest(1m, "e@x.com", [G], G, G, "https://x.test"), Ctx.PaystackInitRequest));
        yield return ("OpenCashSessionRequest", JsonSerializer.Serialize(new OpenCashSessionRequest(G, 0m), Ctx.OpenCashSessionRequest));
        yield return ("CloseCashSessionRequest", JsonSerializer.Serialize(new CloseCashSessionRequest(1m, "n"), Ctx.CloseCashSessionRequest));
        yield return ("HoldRequest", JsonSerializer.Serialize(new HoldRequest(G, T, T.AddHours(1), 2, customer), Ctx.HoldRequest));
        yield return ("ConfirmBookingRequest", JsonSerializer.Serialize(new ConfirmBookingRequest([tender], G, "ps"), Ctx.ConfirmBookingRequest));
        yield return ("CancelBookingRequest", JsonSerializer.Serialize(new CancelBookingRequest("r"), Ctx.CancelBookingRequest));
        yield return ("IssueEntitlementRequest", JsonSerializer.Serialize(new IssueEntitlementRequest(G, G), Ctx.IssueEntitlementRequest));
        yield return ("RefreshRequest", JsonSerializer.Serialize(new RefreshRequest("t"), Ctx.RefreshRequest));
        yield return ("LogoutRequest", JsonSerializer.Serialize(new LogoutRequest(true), Ctx.LogoutRequest));
    }

    [Fact]
    public void EveryRequestWeSend_UsesOnlySpecProperties_AndIncludesAllRequiredOnes()
    {
        var schemas = Schemas()["requests"]!;
        var problems = new List<string>();

        foreach (var (schema, json) in PopulatedRequests())
        {
            var spec = schemas[schema] ?? throw new InvalidOperationException($"Schema {schema} missing from fixtures");
            var known = spec["properties"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
            var required = spec["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var sent = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToHashSet();

            problems.AddRange(sent.Except(known).Select(p => $"{schema}: we send '{p}' which is not in the spec"));
            problems.AddRange(required.Except(sent).Select(p => $"{schema}: required '{p}' is never sent"));
        }

        // Nested objects the spec defines separately.
        var payment = JsonNode.Parse(PopulatedRequests().First(r => r.Schema == "CreatePaymentRequest").Json)!;
        CheckNested(schemas, "TenderInput", payment["tenders"]![0]!.AsObject(), problems);
        var hold = JsonNode.Parse(PopulatedRequests().First(r => r.Schema == "HoldRequest").Json)!;
        CheckNested(schemas, "CustomerInput", hold["customer"]!.AsObject(), problems);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void CheckNested(JsonNode schemas, string schema, JsonObject sentObject, List<string> problems)
    {
        var spec = schemas[schema]!;
        var known = spec["properties"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var required = spec["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var sent = sentObject.Select(p => p.Key).ToHashSet();
        problems.AddRange(sent.Except(known).Select(p => $"{schema}: we send '{p}' which is not in the spec"));
        problems.AddRange(required.Except(sent).Select(p => $"{schema}: required '{p}' is never sent"));
    }

    private static readonly Dictionary<string, Type> ResponseTypes = new()
    {
        ["SystemInfo"] = typeof(SystemInfo),
        ["Staff"] = typeof(Staff),
        ["AuthResult"] = typeof(AuthResult),
        ["StepUpResult"] = typeof(StepUpResult),
        ["Device"] = typeof(Device),
        ["DeviceRegisterResult"] = typeof(DeviceRegisterResult),
        ["Facility"] = typeof(Facility),
        ["FacilityCapabilities"] = typeof(FacilityCapabilities),
        ["Category"] = typeof(Category),
        ["Product"] = typeof(Product),
        ["DiningTable"] = typeof(DiningTable),
        ["Order"] = typeof(Order),
        ["OrderLine"] = typeof(OrderLine),
        ["OrderSummary"] = typeof(OrderSummary),
        ["LineAdjustment"] = typeof(LineAdjustment),
        ["Approval"] = typeof(Approval),
        ["ApprovalOutcome"] = typeof(ApprovalOutcome),
        ["Tab"] = typeof(Tab),
        ["Payment"] = typeof(Payment),
        ["PaymentResult"] = typeof(PaymentResult),
        ["Refund"] = typeof(Refund),
        ["PaymentReversal"] = typeof(PaymentReversal),
        ["PaystackInitResult"] = typeof(PaystackInitResult),
        ["Receipt"] = typeof(Receipt),
        ["CashSession"] = typeof(CashSession),
        ["CashierShiftReport"] = typeof(CashierShiftReport),
        ["BookableResource"] = typeof(BookableResource),
        ["Availability"] = typeof(Availability),
        ["Booking"] = typeof(Booking),
        ["Entitlement"] = typeof(Entitlement),
        ["EntitlementItem"] = typeof(EntitlementItem),
        ["Membership"] = typeof(Membership),
        ["PrepRoute"] = typeof(PrepRoute),
    };

    /// <summary>Spec-required fields we deliberately do not model (documented reason). Everything else must be present.</summary>
    private static readonly HashSet<string> KnownOmissions = [];

    [Fact]
    public void ResponseTypes_ModelEverySpecRequiredProperty()
    {
        var responses = Schemas()["responses"]!.AsObject();
        var problems = new List<string>();

        foreach (var (schema, type) in ResponseTypes)
        {
            var required = responses[schema]!["required"]!.AsArray().Select(n => n!.GetValue<string>());
            var have = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant()).ToHashSet();
            foreach (var name in required)
            {
                if (!have.Contains(name.ToLowerInvariant()) && !KnownOmissions.Contains($"{schema}.{name}"))
                {
                    problems.Add($"{schema}: spec-required '{name}' is not modelled on {type.Name}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Response properties the real node sends (verified live, see docs/REAL_API_TEST_REPORT.md) that the OpenAPI document does not list yet.
    /// They are additive: the POS models them because it needs them, and they must stay listed here so the deviation stays visible.
    /// </summary>
    private static readonly Dictionary<string, string[]> AdditiveNodeFields = new(StringComparer.Ordinal)
    {
        ["Receipt"] = ["AmountPaid", "BalanceDue", "Duplicate", "BusinessName", "PrintLines", "Terminal"],
        ["OperatingRules"] = ["PaymentTiming"],
        ["Entitlement"] = ["GroupEntitlementIds"],
        ["CashSession"] = ["Totals"],
        // Waiter collection (api docs/WAITER_COLLECTION.md; every path is x-additive in the node's openapi, the docs contract copy predates it).
        ["Order"] = ["BillState", "BillPrintedAt", "BillPrintCount", "BillReopenCount", "AwaitingPayment", "PendingCollected", "Collectable"],
        ["OrderSummary"] = ["BillState", "BillPrintedAt", "BillPrintCount", "BillReopenCount", "AwaitingPayment", "PendingCollected", "Collectable"],
        ["Payment"] = ["Collection"],
    };

    [Fact]
    public void EveryPropertyWeModelOnAResponse_ExistsInTheSpec_SoNothingIsMisspelt()
    {
        var responses = Schemas()["responses"]!.AsObject();
        var problems = new List<string>();

        foreach (var (schema, type) in ResponseTypes)
        {
            var spec = responses[schema]!["properties"]!.AsArray().Select(n => n!.GetValue<string>().ToLowerInvariant()).ToHashSet();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var lower = property.Name.ToLowerInvariant();
                if (property.DeclaringType != type || property.GetMethod?.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null && property.Name is "EqualityContract")
                {
                    continue;
                }

                // Helper members (IsPending, Has...) are computed, not wire properties.
                if (property.SetMethod is null && property.GetMethod?.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
                {
                    continue;
                }

                if (!spec.Contains(lower) && !(AdditiveNodeFields.TryGetValue(schema, out var extra) && extra.Contains(property.Name)))
                {
                    problems.Add($"{schema}: '{property.Name}' is not a property in the spec");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
