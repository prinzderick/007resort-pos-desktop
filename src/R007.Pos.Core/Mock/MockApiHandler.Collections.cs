using System.Globalization;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

/// <summary>
/// The mock's version of the waiter-collection flow (bill, collect, confirm, reject, expire, cash handover). It follows
/// <c>docs/WAITER_COLLECTION.md</c> closely enough for the whole cashier desk to run in demo mode and in tests; the real rules live in the API.
/// </summary>
public sealed partial class MockApiHandler
{
    private readonly Dictionary<Guid, MHandover> _handovers = [];
    private readonly Dictionary<Guid, decimal> _cashInHand = [];

    /// <summary>Facility rule <c>waiter_cash_holding</c> (default off, like the node).</summary>
    public bool WaiterCashHolding { get; set; }

    /// <summary>Rule <c>cash_handover_max_variance</c>; above it a handover waits for a supervisor sign-off.</summary>
    public decimal HandoverMaxVariance { get; set; } = 500m;

    /// <summary>Rule <c>pending_collection_expiry_minutes</c>.</summary>
    public int CollectionExpiryMinutes { get; set; } = 30;

    /// <summary>Test helper: the server's view of a payment.</summary>
    public Payment? PeekPayment(Guid id)
    {
        lock (_gate)
        {
            return _payments.TryGetValue(id, out var p) ? p.ToDto() : null;
        }
    }

    private decimal PendingOf(MOrder o) =>
        _payments.Values.Where(p => p.Status is PaymentStatuses.PendingConfirmation or PaymentStatuses.Authorizing)
            .SelectMany(p => p.Allocations).Where(x => x.OrderId == o.Id).Sum(x => x.Amount);

    private static void RequireNotBilled(MOrder o)
    {
        if (o.BillState == BillStates.BillPrinted)
        {
            throw new MockProblem(409, "order_billed", "The bill is printed", "Cancel the bill to change the order.");
        }
    }

    private void ExpireDueCollections()
    {
        foreach (var p in _payments.Values.Where(p => p.Status == PaymentStatuses.PendingConfirmation && p.Collection is { } c && c.ExpiresAt <= Now))
        {
            p.Status = PaymentStatuses.Expired;
            p.Collection!.Decision = CollectionDecisions.Expired;
            p.Collection.DecidedAt = Now;
        }
    }

    private static string LedgerTender(string collected) => collected switch
    {
        CollectionTenders.CardTerminal => TenderTypes.PosTerminal,
        CollectionTenders.PayLink => TenderTypes.Card,
        _ => collected,
    };

    private HttpResponseMessage? RouteCollections(HttpRequestMessage request, string method, string[] seg, Dictionary<string, string> query, byte[] body, Caller caller)
    {
        string[] a;

        if (method == "POST" && Match(seg, "orders/{}/bill", out a))
        {
            Need(caller, Permissions.BillPrint);
            return PrintBill(request, GetOrder(a[0]), caller);
        }

        if (method == "POST" && Match(seg, "orders/{}/bill/cancel", out a))
        {
            Need(caller, Permissions.BillCancelExecute);
            return CancelBill(request, GetOrder(a[0]), Read(body, Ctx.CancelBillRequest), caller);
        }

        if (method == "POST" && Match(seg, "orders/{}/collections", out a))
        {
            Need(caller, Permissions.PaymentCollect);
            return Collect(GetOrder(a[0]), Read(body, Ctx.CollectionRequest), caller);
        }

        if (method == "POST" && Match(seg, "payments/{}/confirm", out a))
        {
            Need(caller, Permissions.PaymentConfirm);
            return Confirm(GetPayment(a[0]), Read(body, Ctx.ConfirmCollectionRequest), caller);
        }

        if (method == "POST" && Match(seg, "payments/{}/reject", out a))
        {
            Need(caller, Permissions.PaymentConfirm);
            return RejectCollection(GetPayment(a[0]), Read(body, Ctx.RejectCollectionRequest), caller);
        }

        if (method == "POST" && Match(seg, "cash-handovers", out _))
        {
            Need(caller, Permissions.CashHandoverCreate);
            return DeclareHandover(Read(body, Ctx.DeclareHandoverRequest), caller);
        }

        if (method == "GET" && Match(seg, "cash-handovers", out _))
        {
            Need(caller, Permissions.CashHandoverView);
            var facility = query.TryGetValue("facilityId", out var f) ? G(f) : (Guid?)null;
            var status = query.GetValueOrDefault("status");
            var items = _handovers.Values.Where(h => (facility is null || h.FacilityId == facility) && (status is null || h.Status == status))
                .OrderBy(h => h.CreatedAt).Select(h => h.ToDto()).ToList();
            return Json(200, new Page<CashHandover>(items, null), Ctx.PageCashHandover);
        }

        if (method == "POST" && Match(seg, "cash-handovers/{}/receive", out a))
        {
            Need(caller, Permissions.CashHandoverReceive);
            return ReceiveHandover(Handover(a[0]), Read(body, Ctx.ReceiveHandoverRequest), caller);
        }

        if (method == "POST" && Match(seg, "cash-handovers/{}/signoff", out a))
        {
            Need(caller, Permissions.CashHandoverSignoff);
            var h = Handover(a[0]);
            if (h.Status != HandoverStatuses.PendingSignoff)
            {
                throw new MockProblem(409, "handover_state_invalid", "This handover does not need a sign-off");
            }

            if (h.ReceivedBy == caller.Staff.Id)
            {
                throw new MockProblem(403, "self_signoff_forbidden", "The person who received the cash cannot sign it off");
            }

            h.Status = HandoverStatuses.SignedOff;
            h.SignedOffBy = caller.Staff.Id;
            h.SignedOffAt = Now;
            return Json(200, h.ToDto(), Ctx.CashHandover);
        }

        if (method == "GET" && Match(seg, "staff/{}/cash-in-hand", out a))
        {
            var id = G(a[0]);
            if (id != caller.Staff.Id)
            {
                Need(caller, Permissions.CashHandoverView);
            }

            return Json(200, Position(id), Ctx.CashInHand);
        }

        if (method == "GET" && Match(seg, "cash-in-hand", out _))
        {
            Need(caller, Permissions.CashHandoverView);
            var items = _cashInHand.Where(x => x.Value > 0m).Select(x => Position(x.Key)).ToList();
            return Json(200, new Page<CashInHand>(items, null), Ctx.PageCashInHand);
        }

        return null;
    }

    private MHandover Handover(string id) =>
        _handovers.TryGetValue(G(id), out var h) ? h : throw new MockProblem(404, "not_found", "Handover not found");

    private CashInHand Position(Guid staffId)
    {
        var name = _staff.FirstOrDefault(s => s.Id == staffId)?.Display;
        var pending = _payments.Values.Where(p => p.Status == PaymentStatuses.PendingConfirmation && p.TakenBy == staffId).ToList();
        return new CashInHand(
            staffId, "NGN", _cashInHand.GetValueOrDefault(staffId), null, false, WaiterCashHolding, null, pending.Count, pending.Sum(p => p.Amount),
            _handovers.Values.Count(h => h.Waiter == staffId && h.Status == HandoverStatuses.PendingReceipt), 0m, name);
    }

    private HttpResponseMessage PrintBill(HttpRequestMessage request, MOrder order, Caller caller)
    {
        var dto = ToDto(order);
        if (order.Status is OrderStatuses.Voided or OrderStatuses.Settled or OrderStatuses.PendingApproval || dto.Total <= 0m
            || (order.Status == OrderStatuses.Draft && !(_paymentTiming.TryGetValue(order.FacilityId, out var timing) && timing == PaymentTimings.PayFirst)))
        {
            throw new MockProblem(409, "order_state_invalid", $"A bill cannot be printed for an order that is {order.Status}.");
        }

        if (order.BillReopenCount > 0 && order.BillState == BillStates.Open && !IsPreApproved(request, caller, Permissions.BillCancelApprove))
        {
            throw new MockProblem(403, "supervisor_required", "This bill was cancelled before: a supervisor must authorise printing it again.");
        }

        var reprint = order.BillState == BillStates.BillPrinted;
        order.BillState = BillStates.BillPrinted;
        order.BillPrintedAt ??= Now;
        order.BillPrintCount++;
        order.RowVersion++;
        var view = ToDto(order);
        var table = order.TableId is { } t && _tables.TryGetValue(t, out var tb) ? tb.Label : null;
        var creator = _staff.FirstOrDefault(s => s.Id == order.CreatedBy);
        var bill = new PreBill(
            "PRE_BILL", "BILL - NOT A RECEIPT", order.Id, order.Number, new PreBillFacility(order.FacilityId, MockData.FacilityName(order.FacilityId)), table,
            new PreBillWaiter(creator?.Id, creator?.Display), Now, order.BillPrintCount, reprint,
            [.. view.Lines.Where(l => l.Status != "VOIDED").Select(l => new PreBillLine(l.Name, l.Quantity, l.UnitPrice, l.LineTotal))],
            view.Subtotal, view.DiscountTotal, view.TaxTotal > 0m ? [new PreBillTaxLine("VAT", view.TaxTotal)] : [], view.TaxTotal, view.Total, view.AmountPaid, view.BalanceDue, "NGN",
            new PayLinkInfo(false, null, null, null), "This is not a receipt. Pay only through the waiter's card machine, the transfer link or the cashier.", null);
        return Json(200, new BillResult(view, bill, reprint), Ctx.BillResult);
    }

    private HttpResponseMessage CancelBill(HttpRequestMessage request, MOrder order, CancelBillRequest req, Caller caller)
    {
        if (order.BillState != BillStates.BillPrinted)
        {
            throw new MockProblem(409, "bill_not_printed", "There is no printed bill to cancel");
        }

        if (_payments.Values.Any(p => p.Status is PaymentStatuses.PendingConfirmation or PaymentStatuses.Authorizing or PaymentStatuses.Captured && p.Allocations.Any(x => x.OrderId == order.Id)))
        {
            throw new MockProblem(409, "collections_pending", "Money was collected on this bill; confirm or reject it first.");
        }

        void Apply()
        {
            order.BillState = BillStates.Open;
            order.BillReopenCount++;
            order.RowVersion++;
        }

        if (IsPreApproved(request, caller, Permissions.BillCancelApprove))
        {
            Apply();
            return Json(200, ToDto(order), Ctx.Order);
        }

        var approval = RequestApproval(caller, "bill.cancel", "order", order.Id, order.FacilityId, req.Reason, ToDto(order).Total, $"Reopen bill for {order.Number}", Permissions.BillCancelApprove, Apply, () => { }, null);
        return Json(202, new BillCancelPending(ToDto(order), approval.ToDto()), Ctx.BillCancelPending);
    }

    private HttpResponseMessage Collect(MOrder order, CollectionRequest req, Caller caller)
    {
        RequireV7(req.Id);
        if (req.Id is { } known && _payments.TryGetValue(known, out var prior))
        {
            var replay = Json(200, new CollectionResult(prior.ToDto(), Summary(order)), Ctx.CollectionResult);
            replay.Headers.TryAddWithoutValidation("Idempotent-Replayed", "true");
            return replay;
        }

        if (order.BillState != BillStates.BillPrinted)
        {
            throw new MockProblem(409, "order_not_billed", "Print the bill before collecting.");
        }

        if (order.Status is OrderStatuses.Voided or OrderStatuses.Settled or OrderStatuses.PendingApproval)
        {
            throw new MockProblem(409, "order_state_invalid", $"The order is {order.Status}.");
        }

        if (req.TenderType == CollectionTenders.PayLink)
        {
            throw new MockProblem(501, "terminal_provider_unavailable", "Pay links are not simulated by the mock.");
        }

        if (req.TenderType == CollectionTenders.Cash && !WaiterCashHolding)
        {
            throw new MockProblem(403, "cash_holding_not_allowed", "Cash holding is not enabled for you; send the customer to the cashier.");
        }

        var view = ToDto(order);
        if (req.Amount <= 0m || req.Amount > (view.Collectable ?? 0m))
        {
            throw new MockProblem(409, "over_collection", "That is more than is left to collect on this bill.");
        }

        foreach (var reference in new[] { req.ApprovalCode, req.SlipReference, req.BankReference }.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (_payments.Values.Any(p => p.Status is PaymentStatuses.PendingConfirmation or PaymentStatuses.Captured && p.Collection is { } c
                && (c.ApprovalCode == reference || c.SlipReference == reference || c.BankReference == reference)))
            {
                throw new MockProblem(409, "duplicate_reference", "That reference was already used on another collection.");
            }
        }

        var payment = new MPayment
        {
            Id = req.Id ?? Guid.CreateVersion7(),
            GroupId = Guid.CreateVersion7(),
            FacilityId = order.FacilityId,
            Tender = LedgerTender(req.TenderType),
            Provider = "MANUAL",
            Reference = req.ApprovalCode ?? req.BankReference ?? req.SlipReference,
            Status = PaymentStatuses.PendingConfirmation,
            Amount = req.Amount,
            Tendered = req.TenderType == CollectionTenders.Cash ? req.Tendered ?? req.Amount : null,
            Change = req.TenderType == CollectionTenders.Cash ? (req.Tendered ?? req.Amount) - req.Amount : null,
            TakenBy = caller.Staff.Id,
            CreatedAt = Now,
            Collection = new MCollection
            {
                Tender = req.TenderType,
                CollectedBy = caller.Staff.Id,
                CollectedByName = caller.Staff.Display,
                ApprovalCode = req.ApprovalCode,
                SlipReference = req.SlipReference,
                Last4 = req.Last4,
                BankReference = req.BankReference,
                ExpiresAt = Now.AddMinutes(CollectionExpiryMinutes),
                OrderNumber = order.Number,
                TableLabel = order.TableId is { } t && _tables.TryGetValue(t, out var tb) ? tb.Label : null,
            },
        };
        payment.Allocations.Add(new PaymentAllocation(order.Id, req.Amount));
        _payments[payment.Id] = payment;
        if (req.TenderType == CollectionTenders.Cash)
        {
            _cashInHand[caller.Staff.Id] = _cashInHand.GetValueOrDefault(caller.Staff.Id) + req.Amount;
        }

        order.RowVersion++;
        return Json(201, new CollectionResult(payment.ToDto(), Summary(order)), Ctx.CollectionResult);
    }

    private HttpResponseMessage Confirm(MPayment payment, ConfirmCollectionRequest req, Caller caller)
    {
        ExpireDueCollections();
        if (payment.Collection is null)
        {
            throw new MockProblem(409, "payment_state_invalid", "This payment was not collected by a waiter.");
        }

        if (payment.TakenBy == caller.Staff.Id)
        {
            throw new MockProblem(403, "self_confirmation_forbidden", "You cannot confirm money you collected yourself.");
        }

        if (payment.Status == PaymentStatuses.Captured && payment.Collection.Decision == CollectionDecisions.Confirmed)
        {
            var again = Json(200, ConfirmResult(payment), Ctx.ConfirmCollectionResult);
            again.Headers.TryAddWithoutValidation("Idempotent-Replayed", "true");
            return again;
        }

        if (payment.Status == PaymentStatuses.Authorizing)
        {
            throw new MockProblem(409, "auto_confirm_only", "Only the payment provider can confirm this payment.");
        }

        if (payment.Status != PaymentStatuses.PendingConfirmation)
        {
            throw new MockProblem(409, "payment_state_invalid", $"This collection is {payment.Status.ToLowerInvariant()} and can no longer be confirmed.");
        }

        var order = _orders[payment.Allocations[0].OrderId];
        if (payment.Amount > ToDto(order).Total - order.AmountPaid)
        {
            throw new MockProblem(409, "balance_changed", "The bill was paid another way meanwhile. Reject this collection.");
        }

        MCashSession? session = null;
        if (payment.Collection.Tender == CollectionTenders.Cash)
        {
            session = _cashSessions.Values.FirstOrDefault(c => c.Status == "OPEN" && c.StaffId == caller.Staff.Id);
            if (session is null && _facilityCaps[payment.FacilityId].OperatingRules?.RequireCashSession == true)
            {
                throw new MockProblem(409, "cash_session_required", "Open a cash session before confirming cash.");
            }
        }

        payment.Status = PaymentStatuses.Captured;
        payment.CapturedAt = Now;
        payment.Collection.Decision = CollectionDecisions.Confirmed;
        payment.Collection.DecidedBy = caller.Staff.Id;
        payment.Collection.DecidedAt = Now;
        payment.Collection.MatchedReference = req.MatchedReference;
        order.AmountPaid += payment.Amount;
        SettleIfPaid(order);
        var receipt = BuildReceipt(caller, payment.FacilityId, [order], [payment]);
        payment.ReceiptId = receipt.Id;
        _ = session;
        return Json(200, ConfirmResult(payment), Ctx.ConfirmCollectionResult);
    }

    private ConfirmCollectionResult ConfirmResult(MPayment payment) =>
        new(payment.ToDto(), Summary(_orders[payment.Allocations[0].OrderId]), payment.ReceiptId == Guid.Empty ? null : payment.ReceiptId);

    private HttpResponseMessage RejectCollection(MPayment payment, RejectCollectionRequest req, Caller caller)
    {
        ExpireDueCollections();
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 3)
        {
            throw new MockProblem(422, "validation_failed", "A reason of at least 3 characters is required.");
        }

        if (payment.Collection is null || payment.Status == PaymentStatuses.Captured)
        {
            throw new MockProblem(409, "payment_state_invalid", "A confirmed payment cannot be rejected; use a refund or reversal.");
        }

        if (payment.Status == PaymentStatuses.Rejected)
        {
            return Json(200, payment.ToDto(), Ctx.Payment);
        }

        if (payment.Status != PaymentStatuses.PendingConfirmation)
        {
            throw new MockProblem(409, "payment_state_invalid", $"This collection is {payment.Status.ToLowerInvariant()}.");
        }

        payment.Status = PaymentStatuses.Rejected;
        payment.Collection.Decision = CollectionDecisions.Rejected;
        payment.Collection.DecidedBy = caller.Staff.Id;
        payment.Collection.DecidedAt = Now;
        payment.Collection.DecisionReason = req.Reason.Trim();
        return Json(200, payment.ToDto(), Ctx.Payment);
    }

    private HttpResponseMessage DeclareHandover(DeclareHandoverRequest req, Caller caller)
    {
        RequireV7(req.Id);
        if (req.Id is { } known && _handovers.TryGetValue(known, out var prior))
        {
            return Json(201, prior.ToDto(), Ctx.CashHandover);
        }

        var inHand = _cashInHand.GetValueOrDefault(caller.Staff.Id);
        if (!WaiterCashHolding && inHand <= 0m)
        {
            throw new MockProblem(403, "cash_holding_not_allowed", "Cash holding is not enabled for you.");
        }

        if (req.DeclaredAmount <= 0m || req.DeclaredAmount > inHand)
        {
            throw new MockProblem(422, "amount_mismatch", "You cannot hand over more than the cash you hold.");
        }

        var h = new MHandover
        {
            Id = req.Id ?? Guid.CreateVersion7(),
            FacilityId = req.FacilityId ?? MockData.RestaurantFacilityId,
            Waiter = caller.Staff.Id,
            WaiterName = caller.Staff.Display,
            ExpectedInHand = inHand,
            Declared = req.DeclaredAmount,
            Note = req.Note,
            CreatedAt = Now,
        };
        _handovers[h.Id] = h;
        return Json(201, h.ToDto(), Ctx.CashHandover);
    }

    private HttpResponseMessage ReceiveHandover(MHandover h, ReceiveHandoverRequest req, Caller caller)
    {
        if (h.Status != HandoverStatuses.PendingReceipt)
        {
            throw new MockProblem(409, "already_received", "This handover was already received.");
        }

        if (h.Waiter == caller.Staff.Id)
        {
            throw new MockProblem(403, "self_receipt_forbidden", "You cannot receive your own handover.");
        }

        if (req.CountedAmount < 0m)
        {
            throw new MockProblem(422, "validation_failed", "The counted amount cannot be negative.");
        }

        h.Counted = req.CountedAmount;
        h.Variance = req.CountedAmount - h.Declared;
        h.ReceivedBy = caller.Staff.Id;
        h.ReceivedAt = Now;
        h.RequiresSignoff = Math.Abs(h.Variance.Value) > HandoverMaxVariance;
        h.Status = h.RequiresSignoff ? HandoverStatuses.PendingSignoff : HandoverStatuses.Received;
        _cashInHand[h.Waiter] = _cashInHand.GetValueOrDefault(h.Waiter) - h.Declared;
        return Json(200, h.ToDto(), Ctx.CashHandover);
    }
}
