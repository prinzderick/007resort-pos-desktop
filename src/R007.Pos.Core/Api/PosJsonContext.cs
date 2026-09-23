using System.Text.Json;
using System.Text.Json.Serialization;
using R007.Pos.Core.Money;

namespace R007.Pos.Core.Api;

/// <summary>
/// Source-generated (trim/AOT friendly, reflection-free) serializer metadata for every wire type.
/// camelCase, nulls omitted, money as decimal strings via <see cref="DecimalStringJsonConverter"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(DecimalStringJsonConverter), typeof(NullableDecimalStringJsonConverter)])]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(StaffLoginRequest))]
[JsonSerializable(typeof(AuthResult))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(StepUpRequest))]
[JsonSerializable(typeof(StepUpResult))]
[JsonSerializable(typeof(DeviceRegisterRequest))]
[JsonSerializable(typeof(DeviceRegisterResult))]
[JsonSerializable(typeof(Facility))]
[JsonSerializable(typeof(FacilityCapabilities))]
[JsonSerializable(typeof(Page<Category>))]
[JsonSerializable(typeof(Page<Product>))]
[JsonSerializable(typeof(Page<DiningTable>))]
[JsonSerializable(typeof(DiningTable))]
[JsonSerializable(typeof(Tab))]
[JsonSerializable(typeof(Page<Tab>))]
[JsonSerializable(typeof(OpenTabRequest))]
[JsonSerializable(typeof(AddTabOrdersRequest))]
[JsonSerializable(typeof(SettleTabRequest))]
[JsonSerializable(typeof(CreateOrderRequest))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Page<OrderSummary>))]
[JsonSerializable(typeof(OrderLineInput))]
[JsonSerializable(typeof(SendOrderRequest))]
[JsonSerializable(typeof(VoidRequest))]
[JsonSerializable(typeof(AdjustmentRequest))]
[JsonSerializable(typeof(ApprovalOutcome))]
[JsonSerializable(typeof(Approval))]
[JsonSerializable(typeof(Page<Approval>))]
[JsonSerializable(typeof(ApprovalDecisionRequest))]
[JsonSerializable(typeof(CreatePaymentRequest))]
[JsonSerializable(typeof(PaymentResult))]
[JsonSerializable(typeof(Payment))]
[JsonSerializable(typeof(Page<Payment>))]
[JsonSerializable(typeof(RefundRequest))]
[JsonSerializable(typeof(Refund))]
[JsonSerializable(typeof(ReversalRequest))]
[JsonSerializable(typeof(PaymentReversal))]
[JsonSerializable(typeof(PaystackInitRequest))]
[JsonSerializable(typeof(PaystackInitResult))]
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(Page<Membership>))]
[JsonSerializable(typeof(CashSession))]
[JsonSerializable(typeof(Page<CashSession>))]
[JsonSerializable(typeof(OpenCashSessionRequest))]
[JsonSerializable(typeof(CloseCashSessionRequest))]
[JsonSerializable(typeof(CashierShiftReport))]
[JsonSerializable(typeof(Page<BookableResource>))]
[JsonSerializable(typeof(Availability))]
[JsonSerializable(typeof(HoldRequest))]
[JsonSerializable(typeof(Booking))]
[JsonSerializable(typeof(ConfirmBookingRequest))]
[JsonSerializable(typeof(CancelBookingRequest))]
[JsonSerializable(typeof(Entitlement))]
[JsonSerializable(typeof(IssueEntitlementRequest))]
[JsonSerializable(typeof(BroadcastAuthRequest))]
[JsonSerializable(typeof(BroadcastAuthResponse))]
[JsonSerializable(typeof(ProblemDetailsDto))]
public sealed partial class PosJsonContext : JsonSerializerContext
{
}

/// <summary>RFC 7807 problem body with the API's stable <c>code</c> extension.</summary>
public sealed record ProblemDetailsDto(
    string? Type,
    string? Title,
    [property: JsonConverter(typeof(LenientInt32Converter))] int? Status,
    string? Detail,
    string? Code,
    IDictionary<string, string[]>? Errors,
    Guid? ApprovalId = null);

public sealed record Health(string Status);

/// <summary>
/// Reads an integer that some server builds emit as a string (or a non-numeric domain value). RFC 7807 says <c>status</c>
/// is the HTTP status integer, but the node once shipped <c>"status":"DRAFT"</c> on <c>order_state_invalid</c>; an
/// unreadable value must never turn a business error into a parse failure.
/// </summary>
public sealed class LenientInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetInt32(out var n):
                return n;
            case JsonTokenType.String when int.TryParse(reader.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
