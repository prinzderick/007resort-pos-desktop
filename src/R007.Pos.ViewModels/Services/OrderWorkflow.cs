using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Offline;

namespace R007.Pos.ViewModels.Services;

/// <summary>
/// Cart operations against the API, with the emergency-queue fallback. Online, every call returns the server's
/// priced order. If the API is unreachable and the facility allows offline orders, the same request (same client
/// UUIDv7 ids, same <c>Idempotency-Key</c>) is written to the encrypted queue for ordered replay and the cart shows
/// it as <see cref="WorkingOrder.IsPendingConfirmation"/> with a labelled estimate. Once an order is pending, further
/// operations on it are queued too (never sent out of order).
/// </summary>
public sealed class OrderWorkflow(IR007ApiClient api, EmergencyQueue emergency, TimeProvider time)
{
    private static PosJsonContext Ctx => PosJsonContext.Default;

    public async Task<WorkingOrder> AddProductAsync(WorkingOrder? order, Product product, int quantity, string? notes, OrderTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var line = new OrderLineInput(product.Id, quantity, string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), ClientIds.New(), time.GetUtcNow());
        var key = IdempotencyKeys.New();

        if (order is null)
        {
            var create = new CreateOrderRequest(target.FacilityId, target.TableId, target.TabId, target.Channel, target.CustomerName, [line], ClientIds.New(), time.GetUtcNow());
            try
            {
                return WorkingOrder.FromServer(await api.CreateOrderAsync(create, key, ct).ConfigureAwait(false));
            }
            catch (ApiUnavailableException) when (emergency.Policy.AllowOrders)
            {
                await QueueOrThrowAsync("api/v1/orders", JsonSerializer.Serialize(create, Ctx.CreateOrderRequest), key, "New order", $"api/v1/orders/{create.Id:D}", false, ct).ConfigureAwait(false);
                return Offline(create.Id!.Value, target, [ToWorking(line, product)]);
            }
        }

        if (order.IsPendingConfirmation)
        {
            await QueueLineAsync(order, line, key, product.Name, ct).ConfigureAwait(false);
            return WithLines(order, [.. order.Lines, ToWorking(line, product)]);
        }

        try
        {
            return WorkingOrder.FromServer(await api.AddLineAsync(order.Id, order.RowVersion, line, key, ct).ConfigureAwait(false));
        }
        catch (ApiUnavailableException) when (emergency.Policy.AllowOrders)
        {
            await QueueLineAsync(order, line, key, product.Name, ct).ConfigureAwait(false);
            return WithLines(order, [.. order.Lines, ToWorking(line, product)]) with { IsPendingConfirmation = true };
        }
    }

    public async Task<WorkingOrder> RemoveLineAsync(WorkingOrder order, Guid lineId, CancellationToken ct = default)
    {
        if (order.IsPendingConfirmation)
        {
            throw new InvalidOperationException("Lines cannot be removed while the order is pending confirmation. Reconnect first.");
        }

        return WorkingOrder.FromServer(await api.RemoveLineAsync(order.Id, lineId, order.RowVersion, IdempotencyKeys.New(), ct).ConfigureAwait(false));
    }

    /// <summary>The API has no quantity-edit call: this removes the line and re-adds it at the new quantity (draft orders only).</summary>
    public async Task<WorkingOrder> SetQuantityAsync(WorkingOrder order, WorkingLine line, int newQuantity, CancellationToken ct = default)
    {
        if (order.IsPendingConfirmation)
        {
            throw new InvalidOperationException("Quantities cannot be changed while the order is pending confirmation. Reconnect first.");
        }

        var afterRemove = await api.RemoveLineAsync(order.Id, line.Id, order.RowVersion, IdempotencyKeys.New(), ct).ConfigureAwait(false);
        if (newQuantity <= 0)
        {
            return WorkingOrder.FromServer(afterRemove);
        }

        var readd = new OrderLineInput(line.ProductId, newQuantity, line.Notes, ClientIds.New(), time.GetUtcNow());
        return WorkingOrder.FromServer(await api.AddLineAsync(order.Id, afterRemove.RowVersion, readd, IdempotencyKeys.New(), ct).ConfigureAwait(false));
    }

    public async Task<WorkingOrder> SendAsync(WorkingOrder order, CancellationToken ct = default)
    {
        var key = IdempotencyKeys.New();
        var path = $"api/v1/orders/{order.Id:D}/send";
        if (order.IsPendingConfirmation)
        {
            await QueueOrThrowAsync(path, JsonSerializer.Serialize(new SendOrderRequest(), Ctx.SendOrderRequest), key, $"Send order {order.Number}", $"api/v1/orders/{order.Id:D}", true, ct).ConfigureAwait(false);
            return order with { Status = "SENT (pending)" };
        }

        try
        {
            return WorkingOrder.FromServer(await api.SendOrderAsync(order.Id, order.RowVersion, key, ct).ConfigureAwait(false));
        }
        catch (ApiUnavailableException) when (emergency.Policy.AllowOrders)
        {
            await QueueOrThrowAsync(path, JsonSerializer.Serialize(new SendOrderRequest(), Ctx.SendOrderRequest), key, $"Send order {order.Number}", $"api/v1/orders/{order.Id:D}", true, ct).ConfigureAwait(false);
            return order with { Status = "SENT (pending)", IsPendingConfirmation = true };
        }
    }

    public async Task<WorkingOrder> RefreshAsync(WorkingOrder order, CancellationToken ct = default) =>
        WorkingOrder.FromServer(await api.GetOrderAsync(order.Id, ct).ConfigureAwait(false));

    private Task QueueLineAsync(WorkingOrder order, OrderLineInput line, string key, string productName, CancellationToken ct) =>
        QueueOrThrowAsync(
            $"api/v1/orders/{order.Id:D}/lines",
            JsonSerializer.Serialize(line, Ctx.OrderLineInput),
            key,
            $"Add {productName} to order {order.Number}",
            $"api/v1/orders/{order.Id:D}",
            true,
            ct);

    private async Task QueueOrThrowAsync(string path, string body, string key, string description, string aggregate, bool ifMatch, CancellationToken ct)
    {
        var result = await emergency.QueueAsync(path, body, key, description, aggregate, ifMatch, ct).ConfigureAwait(false);
        if (!result.IsQueued)
        {
            throw new ApiUnavailableException(result.Message);
        }
    }

    private static WorkingLine ToWorking(OrderLineInput input, Product product) =>
        new(input.Id!.Value, product.Id, product.Name, input.Quantity, product.Price, null, input.Notes, "PENDING", true);

    private static WorkingOrder Offline(Guid id, OrderTarget target, IReadOnlyList<WorkingLine> lines) =>
        new(id, "PENDING-" + id.ToString("N")[^6..].ToUpperInvariant(), target.FacilityId, target.TableId, target.TabId, OrderStatuses.Draft, 0, lines, null, null, null, null, null, null, Estimate(lines), "NGN", true, null);

    private static WorkingOrder WithLines(WorkingOrder order, IReadOnlyList<WorkingLine> lines) =>
        order with { Lines = lines, EstimatedTotal = Estimate(lines) };

    /// <summary>Emergency display estimate only (catalog price x quantity, no tax or discounts): labelled "estimate" in the UI; the API re-prices on replay.</summary>
    public static decimal Estimate(IEnumerable<WorkingLine> lines) => lines.Sum(l => l.UnitPrice * l.Quantity);
}
