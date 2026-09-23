using System.Globalization;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

public sealed partial class MockApiHandler
{
    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private Order ToDto(MOrder o)
    {
        var subtotal = o.Lines.Where(l => l.Status != "REMOVED" && l.Status != "VOIDED").Sum(l => l.Gross);
        var discount = o.Lines.Where(l => l.Status != "REMOVED" && l.Status != "VOIDED").Sum(l => l.Discount);
        var tax = _options.VatRatePercent > 0 ? Round2((subtotal - discount) * _options.VatRatePercent / 100m) : 0m;
        var total = subtotal - discount + tax;
        var lines = o.Lines.Where(l => l.Status != "REMOVED").Select(l => new OrderLine(
            l.Id,
            l.ProductId,
            l.Name,
            l.Quantity,
            l.UnitPrice,
            null,
            l.Net,
            l.Notes,
            l.Status,
            new PrepRoute(null, null, l.Route),
            [.. l.Adjustments.Select(a => new LineAdjustment(a.Id, a.Kind, a.Value, a.Reason, a.Amount, a.Status, a.ApprovalId))])).ToList();

        return new Order(
            o.Id, o.Number, o.FacilityId, o.TableId, o.TabId, o.CustomerName, o.Channel, o.Status, lines,
            subtotal, discount, tax, total, o.AmountPaid, o.Status == OrderStatuses.Voided ? 0m : total - o.AmountPaid,
            "NGN", o.PendingApprovalId, o.RowVersion, o.CreatedAt);
    }

    private OrderSummary Summary(MOrder o)
    {
        var dto = ToDto(o);
        var label = o.TableId is { } t && _tables.TryGetValue(t, out var table) ? table.Label : null;
        return new OrderSummary(o.Id, o.Number, o.FacilityId, o.TableId, label, o.TabId, o.Status, dto.Total, dto.BalanceDue, dto.Lines.Count, o.CreatedAt);
    }

    private Tab ToDto(MTab t)
    {
        var orders = t.OrderIds.Select(id => _orders[id]).Where(o => o.Status != OrderStatuses.Voided).Select(ToDto).ToList();
        return new Tab(t.Id, t.FacilityId, t.TableId, t.CustomerName, t.Status, t.OrderIds, orders.Sum(o => o.Total), orders.Sum(o => o.AmountPaid), orders.Sum(o => o.BalanceDue), "NGN", t.OpenedAt, t.RowVersion);
    }

    private static void CheckIfMatch(HttpRequestMessage request, int rowVersion)
    {
        var header = request.Headers.TryGetValues("If-Match", out var values) ? values.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(header))
        {
            throw new MockProblem(428, "concurrency_conflict", "If-Match header is required");
        }

        if (header != $"\"v{rowVersion.ToString(CultureInfo.InvariantCulture)}\"")
        {
            throw new MockProblem(412, "concurrency_conflict", "The order changed since you loaded it", $"currentRowVersion={rowVersion}");
        }
    }

    private HttpResponseMessage OrderResponse(int status, MOrder o)
    {
        var response = Json(status, ToDto(o), Ctx.Order);
        response.Headers.TryAddWithoutValidation("ETag", $"\"v{o.RowVersion.ToString(CultureInfo.InvariantCulture)}\"");
        return response;
    }

    private MOrder GetOrder(string id) =>
        _orders.TryGetValue(G(id), out var order) ? order : throw new MockProblem(404, "not_found", "Order not found");

    private MLine BuildLine(OrderLineInput input)
    {
        RequireV7(input.Id);
        if (input.Quantity < 1)
        {
            throw new MockProblem(422, "validation_failed", "Quantity must be at least 1");
        }

        var product = MockData.Products.FirstOrDefault(p => p.Id == input.ProductId)
            ?? throw new MockProblem(404, "not_found", "Product not found");
        return new MLine
        {
            Id = input.Id ?? Guid.CreateVersion7(),
            ProductId = product.Id,
            Name = product.Name,
            Quantity = input.Quantity,
            BaseUnitPrice = product.Price,
            Notes = input.Notes,
            Route = product.PrepRoute?.Kind ?? "NONE",
            Kind = product.Kind,
        };
    }

    private HttpResponseMessage? RouteTablesOrdersTabs(HttpRequestMessage request, string method, string[] seg, Dictionary<string, string> query, byte[] body, Caller caller)
    {
        string[] a;

        if (method == "GET" && Match(seg, "tables", out _))
        {
            var facility = G(query.GetValueOrDefault("facilityId") ?? string.Empty);
            return Json(200, new Page<DiningTable>([.. _tables.Values.Where(t => t.FacilityId == facility).OrderBy(t => t.Label, StringComparer.Ordinal)], null), Ctx.PageDiningTable);
        }

        if (method == "POST" && Match(seg, "tables/{}/open", out a))
        {
            var id = G(a[0]);
            var table = _tables.GetValueOrDefault(id) ?? throw new MockProblem(404, "not_found", "Table not found");
            _tables[id] = table with { Status = "OCCUPIED", RowVersion = (table.RowVersion ?? 1) + 1 };
            return Json(200, _tables[id], Ctx.DiningTable);
        }

        // Orders
        if (method == "POST" && Match(seg, "orders", out _))
        {
            Need(caller, Permissions.OrderCreate);
            return CreateOrder(Read(body, Ctx.CreateOrderRequest), caller);
        }

        if (method == "GET" && Match(seg, "orders", out _))
        {
            Need(caller, Permissions.OrderView);
            var facility = query.TryGetValue("filter[facilityId]", out var f) ? G(f) : (Guid?)null;
            var statuses = query.TryGetValue("filter[status]", out var st) ? st.Split(',') : null;
            var tab = query.TryGetValue("filter[tabId]", out var tb) ? G(tb) : (Guid?)null;
            var items = _orders.Values
                .Where(o => (facility is null || o.FacilityId == facility) && (tab is null || o.TabId == tab) && (statuses is null || statuses.Contains(o.Status)))
                .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Number, StringComparer.Ordinal)
                .Select(Summary).ToList();
            return Json(200, new Page<OrderSummary>(items, null), Ctx.PageOrderSummary);
        }

        if (method == "GET" && Match(seg, "orders/{}", out a))
        {
            Need(caller, Permissions.OrderView);
            return OrderResponse(200, GetOrder(a[0]));
        }

        if (method == "POST" && Match(seg, "orders/{}/lines", out a))
        {
            Need(caller, Permissions.OrderLineAdd);
            var order = GetOrder(a[0]);
            var input = Read(body, Ctx.OrderLineInput);
            if (order.Lines.Any(l => l.Id == input.Id))
            {
                return OrderResponse(201, order); // idempotent by client id
            }

            CheckIfMatch(request, order.RowVersion);
            RequireDraft(order);
            order.Lines.Add(BuildLine(input));
            order.RowVersion++;
            return OrderResponse(201, order);
        }

        if (method == "DELETE" && Match(seg, "orders/{}/lines/{}", out a))
        {
            Need(caller, Permissions.OrderLineRemoveUnsent);
            var order = GetOrder(a[0]);
            CheckIfMatch(request, order.RowVersion);
            RequireDraft(order);
            var line = order.Lines.FirstOrDefault(l => l.Id == G(a[1])) ?? throw new MockProblem(404, "not_found", "Line not found");
            line.Status = "REMOVED";
            order.RowVersion++;
            return OrderResponse(200, order);
        }

        if (method == "POST" && Match(seg, "orders/{}/send", out a))
        {
            Need(caller, Permissions.OrderSend);
            var order = GetOrder(a[0]);
            if (order.Status == OrderStatuses.Sent)
            {
                return OrderResponse(200, order); // already sent: safe replay
            }

            CheckIfMatch(request, order.RowVersion);
            RequireDraft(order);
            if (order.Lines.All(l => l.Status == "REMOVED"))
            {
                throw new MockProblem(422, "validation_failed", "Order has no lines");
            }

            order.Status = OrderStatuses.Sent;
            foreach (var l in order.Lines.Where(l => l.Status == "PENDING"))
            {
                l.Status = l.Route == "NONE" ? "LOCKED" : "ROUTED";
            }

            order.RowVersion++;
            return OrderResponse(200, order);
        }

        if (method == "POST" && Match(seg, "orders/{}/serve", out a))
        {
            Need(caller, Permissions.OrderServe);
            var order = GetOrder(a[0]);
            CheckIfMatch(request, order.RowVersion);
            if (order.Status is not (OrderStatuses.Sent or OrderStatuses.Ready or OrderStatuses.InPreparation))
            {
                throw new MockProblem(409, "order_state_invalid", "Order cannot be served in its current state");
            }

            order.Status = OrderStatuses.Served;
            order.RowVersion++;
            return OrderResponse(200, order);
        }

        if (method == "POST" && Match(seg, "orders/{}/void", out a))
        {
            Need(caller, Permissions.OrderVoidExecute);
            return VoidOrder(request, GetOrder(a[0]), Read(body, Ctx.VoidRequest), caller);
        }

        if (method == "POST" && Match(seg, "orders/{}/lines/{}/adjustments", out a))
        {
            return Adjust(request, GetOrder(a[0]), G(a[1]), Read(body, Ctx.AdjustmentRequest), caller);
        }

        // Tabs
        if (method == "POST" && Match(seg, "tabs", out _))
        {
            Need(caller, Permissions.TabOpen);
            return OpenTab(Read(body, Ctx.OpenTabRequest));
        }

        if (method == "GET" && Match(seg, "tabs", out _))
        {
            Need(caller, Permissions.TabView);
            var facility = query.TryGetValue("filter[facilityId]", out var f) ? G(f) : (Guid?)null;
            var status = query.GetValueOrDefault("filter[status]");
            var items = _tabs.Values.Where(t => (facility is null || t.FacilityId == facility) && (status is null || t.Status == status)).OrderBy(t => t.OpenedAt).Select(ToDto).ToList();
            return Json(200, new Page<Tab>(items, null), Ctx.PageTab);
        }

        if (method == "GET" && Match(seg, "tabs/{}", out a))
        {
            Need(caller, Permissions.TabView);
            return Json(200, ToDto(GetTab(a[0])), Ctx.Tab);
        }

        if (method == "POST" && Match(seg, "tabs/{}/orders", out a))
        {
            Need(caller, Permissions.TabOpen);
            var tab = GetTab(a[0]);
            CheckIfMatch(request, tab.RowVersion);
            foreach (var id in Read(body, Ctx.AddTabOrdersRequest).OrderIds)
            {
                var order = _orders.GetValueOrDefault(id) ?? throw new MockProblem(404, "not_found", "Order not found");
                order.TabId = tab.Id;
                if (!tab.OrderIds.Contains(id))
                {
                    tab.OrderIds.Add(id);
                }
            }

            tab.RowVersion++;
            return Json(200, ToDto(tab), Ctx.Tab);
        }

        // Approvals
        if (method == "GET" && Match(seg, "approvals", out _))
        {
            var scope = query.GetValueOrDefault("scope") ?? "mine";
            var status = query.GetValueOrDefault("filter[status]");
            var items = _approvals.Values
                .Where(x => status is null || x.Status == status)
                .Where(x => scope == "approvable"
                    ? caller.Staff.Permissions.Contains(x.RequiredPermission) && x.RequestedBy != caller.Staff.Id
                    : x.RequestedBy == caller.Staff.Id)
                .OrderBy(x => x.RequestedAt).Select(x => x.ToDto()).ToList();
            return Json(200, new Page<Approval>(items, null), Ctx.PageApproval);
        }

        if (method == "GET" && Match(seg, "approvals/{}", out a))
        {
            return Json(200, GetApproval(a[0]).ToDto(), Ctx.Approval);
        }

        if (method == "POST" && Match(seg, "approvals/{}/decision", out a))
        {
            var approval = GetApproval(a[0]);
            var req = Read(body, Ctx.ApprovalDecisionRequest);
            var decider = caller.Staff;
            if (!string.IsNullOrEmpty(req.StepUpToken) && _stepUps.Remove(req.StepUpToken, out var su))
            {
                decider = su.Approver;
            }

            Decide(approval, req.Decision, decider, req.Note);
            return Json(200, approval.ToDto(), Ctx.Approval);
        }

        if (method == "POST" && Match(seg, "approvals/{}/cancel", out a))
        {
            var approval = GetApproval(a[0]);
            if (approval.RequestedBy != caller.Staff.Id)
            {
                throw new MockProblem(403, "permission_denied", "Only the requester can cancel");
            }

            if (approval.Status == ApprovalStatuses.Pending)
            {
                approval.Status = ApprovalStatuses.Cancelled;
                approval.OnReject?.Invoke();
            }

            return Json(200, approval.ToDto(), Ctx.Approval);
        }

        return null;
    }

    private static void RequireDraft(MOrder order)
    {
        if (order.Status != OrderStatuses.Draft)
        {
            throw new MockProblem(409, "order_state_invalid", "Only a draft order can be changed");
        }
    }

    private MTab GetTab(string id) =>
        _tabs.TryGetValue(G(id), out var tab) ? tab : throw new MockProblem(404, "not_found", "Tab not found");

    private MApproval GetApproval(string id) =>
        _approvals.TryGetValue(G(id), out var approval) ? approval : throw new MockProblem(404, "not_found", "Approval not found");

    private HttpResponseMessage CreateOrder(CreateOrderRequest req, Caller caller)
    {
        RequireV7(req.Id);
        if (req.Id is { } existing && _orders.TryGetValue(existing, out var prior))
        {
            if (prior.FacilityId != req.FacilityId)
            {
                throw new MockProblem(409, "concurrency_conflict", "That id already exists with a different body");
            }

            return OrderResponse(201, prior);
        }

        if (!_facilityCaps.ContainsKey(req.FacilityId))
        {
            throw new MockProblem(404, "not_found", "Facility not found");
        }

        var order = new MOrder
        {
            Id = req.Id ?? Guid.CreateVersion7(),
            Number = $"{MockData.FacilityName(req.FacilityId)[..3].ToUpperInvariant()}-{(++_orderSeq).ToString("000000", CultureInfo.InvariantCulture)}",
            FacilityId = req.FacilityId,
            TableId = req.TableId,
            TabId = req.TabId,
            CustomerName = req.CustomerName,
            Channel = req.Channel ?? OrderChannels.DineIn,
            CreatedAt = Now,
            CreatedBy = caller.Staff.Id,
        };

        foreach (var line in req.Lines ?? [])
        {
            order.Lines.Add(BuildLine(line));
        }

        _orders[order.Id] = order;
        if (order.TableId is { } tableId && _tables.TryGetValue(tableId, out var table))
        {
            _tables[tableId] = table with { Status = "OCCUPIED", OpenOrderIds = [.. table.OpenOrderIds ?? [], order.Id] };
        }

        if (order.TabId is { } tabId && _tabs.TryGetValue(tabId, out var tab))
        {
            tab.OrderIds.Add(order.Id);
            tab.RowVersion++;
        }

        return OrderResponse(201, order);
    }

    private HttpResponseMessage OpenTab(OpenTabRequest req)
    {
        RequireV7(req.Id);
        if (req.Id is { } existing && _tabs.TryGetValue(existing, out var prior))
        {
            return Json(201, ToDto(prior), Ctx.Tab);
        }

        var tab = new MTab { Id = req.Id ?? Guid.CreateVersion7(), FacilityId = req.FacilityId, TableId = req.TableId, CustomerName = req.CustomerName, OpenedAt = Now };
        foreach (var id in req.OrderIds ?? [])
        {
            var order = _orders.GetValueOrDefault(id) ?? throw new MockProblem(404, "not_found", "Order not found");
            order.TabId = tab.Id;
            tab.OrderIds.Add(id);
        }

        _tabs[tab.Id] = tab;
        if (req.TableId is { } tableId && _tables.TryGetValue(tableId, out var table))
        {
            _tables[tableId] = table with { Status = "OCCUPIED", OpenTabId = tab.Id };
        }

        return Json(201, ToDto(tab), Ctx.Tab);
    }

    private HttpResponseMessage VoidOrder(HttpRequestMessage request, MOrder order, VoidRequest req, Caller caller)
    {
        CheckIfMatch(request, order.RowVersion);
        if (order.Status is OrderStatuses.Voided or OrderStatuses.Settled or OrderStatuses.PendingApproval)
        {
            throw new MockProblem(409, "order_state_invalid", "Order cannot be voided in its current state");
        }

        void Apply()
        {
            order.Status = OrderStatuses.Voided;
            order.PendingApprovalId = null;
            order.RowVersion++;
            ReleaseTable(order);
        }

        if (IsPreApproved(request, caller, Permissions.OrderVoidApprove))
        {
            Apply();
            return OrderResponse(200, order);
        }

        var approval = RequestApproval(caller, "order.void", "order", order.Id, order.FacilityId, req.Reason, ToDto(order).Total, $"Void order {order.Number}", Permissions.OrderVoidApprove, Apply, () => Revert(order), order);
        return Json(202, new ApprovalOutcome(OrderStatuses.PendingApproval, approval.ToDto(), ToDto(order)), Ctx.ApprovalOutcome);
    }

    private void ReleaseTable(MOrder order)
    {
        if (order.TableId is { } tableId && _tables.TryGetValue(tableId, out var table)
            && !_orders.Values.Any(o => o.TableId == tableId && o.Id != order.Id && !o.IsClosedForTable()))
        {
            _tables[tableId] = table with { Status = "FREE", OpenTabId = null };
        }
    }

    private static void Revert(MOrder order)
    {
        order.Status = order.StatusBeforeApproval ?? OrderStatuses.Draft;
        order.PendingApprovalId = null;
        order.RowVersion++;
    }

    private MApproval RequestApproval(Caller caller, string action, string entityType, Guid entityId, Guid facilityId, string reason, decimal? amount, string summary, string permission, Action onApprove, Action onReject, MOrder? order)
    {
        var approval = new MApproval
        {
            Id = Guid.CreateVersion7(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            FacilityId = facilityId,
            RequestedBy = caller.Staff.Id,
            RequestedByName = caller.Staff.Display,
            RequestedAt = Now,
            Reason = reason,
            Amount = amount,
            Summary = summary,
            RequiredPermission = permission,
            OnApprove = onApprove,
            OnReject = onReject,
        };
        _approvals[approval.Id] = approval;
        if (order is not null)
        {
            order.StatusBeforeApproval = order.Status;
            order.Status = OrderStatuses.PendingApproval;
            order.PendingApprovalId = approval.Id;
            order.RowVersion++;
        }

        return approval;
    }

    private void Decide(MApproval approval, string decision, MStaff decider, string? note)
    {
        if (approval.Status != ApprovalStatuses.Pending)
        {
            throw new MockProblem(409, "order_state_invalid", "Approval already decided");
        }

        if (!decider.Permissions.Contains(approval.RequiredPermission))
        {
            throw new MockProblem(403, "permission_denied", "You cannot decide this approval", approval.RequiredPermission);
        }

        if (decider.Id == approval.RequestedBy)
        {
            throw new MockProblem(403, "permission_denied", "You cannot approve your own request");
        }

        approval.DecidedBy = decider.Id;
        approval.DecidedAt = Now;
        approval.Note = note;
        if (decision == ApprovalDecisions.Approve)
        {
            approval.Status = ApprovalStatuses.Approved;
            approval.OnApprove?.Invoke();
        }
        else
        {
            approval.Status = ApprovalStatuses.Rejected;
            approval.OnReject?.Invoke();
        }
    }

    private HttpResponseMessage Adjust(HttpRequestMessage request, MOrder order, Guid lineId, AdjustmentRequest req, Caller caller)
    {
        var (executePerm, approvePerm) = req.Kind switch
        {
            AdjustmentKinds.DiscountPercent or AdjustmentKinds.DiscountAmount => (Permissions.OrderDiscountExecute, Permissions.OrderDiscountApprove),
            AdjustmentKinds.Comp => (Permissions.OrderCompExecute, Permissions.OrderCompApprove),
            AdjustmentKinds.PriceOverride => (Permissions.OrderPriceOverrideExecute, Permissions.OrderPriceOverrideApprove),
            _ => throw new MockProblem(422, "validation_failed", "Unknown adjustment kind"),
        };

        Need(caller, executePerm);
        CheckIfMatch(request, order.RowVersion);
        if (order.IsClosedForTable() || order.Status == OrderStatuses.PendingApproval)
        {
            throw new MockProblem(409, "order_state_invalid", "Order cannot be adjusted in its current state");
        }

        var line = order.Lines.FirstOrDefault(l => l.Id == lineId && l.Status != "REMOVED") ?? throw new MockProblem(404, "not_found", "Line not found");
        if (!MoneyParse(req.Value, out var value) || value < 0)
        {
            throw new MockProblem(422, "validation_failed", "Adjustment value must be a decimal string");
        }

        var adjustment = new MAdjustment { Kind = req.Kind, Value = req.Value, Reason = req.Reason };

        void Apply()
        {
            adjustment.Amount = req.Kind switch
            {
                AdjustmentKinds.DiscountPercent => Round2(line.Gross * value / 100m),
                AdjustmentKinds.DiscountAmount => value,
                AdjustmentKinds.Comp => line.Gross,
                _ => 0m,
            };
            if (req.Kind == AdjustmentKinds.PriceOverride)
            {
                line.OverridePrice = value;
            }

            adjustment.Status = "APPLIED";
            order.PendingApprovalId = null;
            if (order.Status == OrderStatuses.PendingApproval)
            {
                order.Status = order.StatusBeforeApproval ?? OrderStatuses.Draft;
            }

            order.RowVersion++;
        }

        // Small percentage discounts are within the cashier's own authority (facility approval threshold); everything else needs approval.
        var withinThreshold = req.Kind == AdjustmentKinds.DiscountPercent && value <= 5m;
        if (withinThreshold || IsPreApproved(request, caller, approvePerm))
        {
            line.Adjustments.Add(adjustment);
            Apply();
            return OrderResponse(200, order);
        }

        adjustment.Status = "PENDING_APPROVAL";
        line.Adjustments.Add(adjustment);
        var approval = RequestApproval(caller, "order.adjustment", "order", order.Id, order.FacilityId, req.Reason, null, $"{req.Kind} {req.Value} on {line.Name}", approvePerm, Apply, () =>
        {
            adjustment.Status = "REJECTED";
            Revert(order);
        }, order);
        adjustment.ApprovalId = approval.Id;
        return Json(202, new ApprovalOutcome(OrderStatuses.PendingApproval, approval.ToDto(), ToDto(order)), Ctx.ApprovalOutcome);
    }

    private static bool MoneyParse(string value, out decimal amount) => R007.Pos.Core.Money.MoneyFormat.TryParseWire(value, out amount);
}

internal static class MockOrderExtensions
{
    public static bool IsClosedForTable(this MOrder o) => o.Status is OrderStatuses.Settled or OrderStatuses.Voided;
}
