# API contract notes (POS)

Source of truth: the **built node** (`007resort-api`, branch `integration/mvp`, `docs/openapi/v1.yaml` there) as verified by driving the
POS against a real running node (see [REAL_API_TEST_REPORT.md](REAL_API_TEST_REPORT.md)); the original docs-repo contract
(`api/openapi/v1.yaml`, commit `e205d58`) is what the copied `ContractFixtures/` come from. The POS types live in
`src/R007.Pos.Core/Api/Contract.*.cs`; `ContractConformanceTests` guards drift (properties the node sends that the OpenAPI does not list yet
are pinned in that test's `AdditiveNodeFields`). This page records how the POS uses the contract, where the real node differs from
what the POS first assumed (all fixed), and what it still needs from the contract/backend owners.

## How each flow maps to the contract

| POS action | Endpoints |
|---|---|
| Start-up | `GET /system/info` (`minClientVersion["pos"]` -> "update required" banner), `GET /health/live` (probe; **server root, not under `/api/v1`**) |
| Enrol | `POST /devices/register {name, kind:POS_TERMINAL, mode:POS, hardwareId, registrationCode, platform, appVersion}` -> `deviceToken` (DPAPI); the code comes from `POST /devices/registration-codes` (IT admin, `device.register`) |
| Facility | device `facilityId` (else `homeFacilityId`); `GET /facilities/{id}/capabilities` (incl. `operatingRules.paymentTiming`); name from `GET /organization/facilities/{id}` (optional) |
| Login | `POST /auth/staff/login` `PIN` (`identifier` = staff number, `secret` = PIN) / `NFC_CARD` (**`identifier` = card UID, `secret` = the staff PIN, one call**) / `PASSWORD` (`identifier` = username); `POST /auth/staff/refresh` (single flight, rotating); `POST /auth/staff/logout` |
| Catalog | `GET /catalog/categories`, `GET /catalog/products?facilityId=` (all pages); barcode = `q=` search |
| Tables / tabs | `GET /tables`, `POST /tables/{id}/open`, `POST /tabs`, `GET /tabs`, `POST /tabs/{id}/settle` |
| Orders | `POST /orders` (client id, first line), `POST /orders/{id}/lines`, `DELETE .../lines/{lineId}`, `POST .../send`, `POST .../serve` (pay-after facilities); `If-Match: "v{rowVersion}"` (the node also accepts the bare `"{rowVersion}"` it emits in `ETag`) |
| Approvals | `POST .../void` and `.../lines/{id}/adjustments` -> `200` or `202 ApprovalOutcome`; `GET /approvals/{id}` (poll), `GET /approvals?scope=approvable`, `POST /approvals/{id}/decision`, `POST /approvals/{id}/cancel`; inline: `POST /auth/staff/step-up` then `X-Step-Up-Token` |
| Payments | `POST /payments {facilityId, cashSessionId, allocations, tenders[{tenderType, amount, reference, tendered, id}]}` -> `PaymentResult{payments, receiptId, changeDue}`; `POST /payments/{id}/refund` / `/reversal`; Paystack `POST /payments/paystack/initialize` + `GET .../verify/{reference}` |
| Receipts | `GET /receipts/{id}` (`?reprint=true` for reprints), `GET /orders/{id}/receipt` |
| Cash | `POST /cash-sessions`, `GET /cash-sessions?filter[status]=OPEN&filter[staffId]=`, `POST .../close {countedCash}`, `GET /reports/cashier-shift/{id}` |
| Reception | `GET /bookings/resources`, `.../availability`, `POST /bookings/hold` (no order yet), `POST /orders` (slot-fee product + rentals + goods), `POST /bookings/{id}/order {orderId}` (`If-Match`), pay the order with `POST /payments` (the node confirms the booking and issues the QR inside the payment), `GET /bookings/{id}`, `GET /entitlements?filter[orderId]|[bookingId]`, `GET /entitlements/{id}`, `POST /entitlements {orderId}` (idempotent fallback) |
| Customers | `GET /memberships?q=` (no separate customers resource in the contract) |

## Decisions the POS made

1. **Statuses/kinds are strings**, not enums, so an unknown value from a newer API does not break parsing (contract README).
2. **Order line notes are the only "modifier"**: the contract has no modifier model, so quick-note chips become `notes`.
3. **Quantity change = remove + re-add** (draft orders only): there is no PATCH for a line quantity.
4. **NFC + PIN** is ONE call on the real node: `NFC_CARD` with `identifier` = card UID and `secret` = the staff PIN. A card alone never signs in
   (on any station), so a card tap always moves the login screen to the PIN step. (The first version sent the UID as `secret` and then a
   second `PIN` login; the node answered 422 - the OpenAPI text was wrong, see PR #14 on 007resort-api.) Cards are registered by IT with
   `PUT /staff/{id}/credentials/nfc-card {cardUid}` (`staff.manage`).
5. **Booking + rentals (Reception, PAY_FIRST)**: `POST /bookings/hold` returns a HELD booking **without an order**. The POS then creates a counter
   order whose first line is the resource's slot-fee product (`BookableResource.productId`; quantity = party size only for `INDIVIDUAL_CAPACITY`),
   attaches it with `POST /bookings/{id}/order` (booking -> `PENDING_PAYMENT`), adds rentals as further lines, and pays the ORDER with
   `POST /payments` (which returns the receipt id and the change; `/bookings/{id}/confirm` returns neither). The node confirms the booking and
   issues the QR entitlement inside the payment transaction. Ticket-only sales at the till are the same minus the booking; one QR per individual
   ticket (`GET /entitlements?filter[orderId]`), printed as one slip each.
5b. **Payment timing** (`operatingRules.paymentTiming`): `PAY_FIRST` (Reception) pays a DRAFT order and the order **stays DRAFT/SENT** afterwards (it is not
   SETTLED), so the cart clears on `balanceDue == 0`. `PAY_AFTER_SERVICE` / `PAY_BEFORE_LEAVING` (Restaurant, bars) only take payment for a SERVED order
   (`409 order_state_invalid`, also for tab settle): the POS shows "Mark served" (`order.serve`, needs the kitchen/bar tickets READY) and offers Pay afterwards.
6. **Refund/reversal** return `Refund`/`PaymentReversal` with `status: PENDING_APPROVAL` + `approvalId`; the POS waits on that approval like any other.
7. **Emergency queue** allow-list is stricter than the contract: only CASH tenders, never card/transfer/Paystack, even when `allowOfflinePayments=ALL`.
8. **Approval push** is a hint only: `ApprovalCoordinator.NotifyDecided(approvalId)` re-reads `GET /approvals/{id}`. See "Realtime" below.

## Requests to the contract / backend owners

1. **Station authentication policy.** `NFC_CARD` always needs the PIN on the node (a card alone is refused), which covers the "card + PIN"
   station. What is still missing is an operating rule (e.g. `requireNfcAndPin`) readable **before login** (for example on `GET /devices/{id}`
   or `/system/info`) and enforced server-side so that a plain `PIN` login is refused at such stations. Today that is POS configuration
   (`Pos:RequireNfcAndPin`) only.
2. **Line quantity edit** (`PATCH /orders/{id}/lines/{lineId} {quantity}`) to avoid remove + re-add.
3. **Modifiers** (or a documented `notes` convention) if kitchens need structured options.
4. **Cash drawer authorisation.** The client design brief expects a manual "open drawer" to be authorised and audited
   by the API; there is no endpoint. The POS therefore kicks the drawer only for cash tenders (via the receipt) and
   offers no manual open. Add e.g. `POST /cash-drawer/open {reason}` (permission + audit) if manual opening is required.
5. **Offline policy visibility.** `allowOfflineOrders` / `allowOfflinePayments` are read from `GET /facilities/{id}/capabilities`,
   which needs a session; a terminal that loses the network before its first login of the day has no policy. Consider
   caching guidance or including the rules on the device resource.
6. **Line-level void** for sent lines (only order-level void exists; draft lines are simply removed).
7. ~~Booking + rental in one transaction~~ Answered by the real node: lines can be added to the attached order while the booking is
   `PENDING_PAYMENT`; `Booking.total` stays the **slot fee only** (rentals are on the order), so the amount due is the order's `balanceDue`.
8. ~~Receipt QR~~ Answered: `Receipt.qrPayload` is `null` for booking/ticket receipts (a documented gap); the POS prints the entitlement `qrToken`.
9. ~~Idempotent tender ids~~ Verified: same tender id + same body is a replay of the original payment; same id + different body is `409 concurrency_conflict`.
10. **Shift report needs `report.view`**, which the CASHIER role does not hold (`cash_session.view` only). The POS shows the session summary from
    `GET /cash-sessions/{id}` (`totals`) instead. If cashiers should print the full report, grant `report.view` or allow the owner of the session.
11. **Rate limits**: device registration 10/min/IP, login 10/min per identifier (+60/min/IP), refresh 30/min. Fine for one till; a provisioning
    script or a test run that enrols many terminals must honour `429`/`Retry-After` (the harness does).
12. **Percentage discount** values must match `^\d{1,3}(\.\d{1,2})?$` (2 decimals) whereas money is 4 decimals; consider accepting 4 for consistency.
13. **Undocumented additive fields the POS relies on**: `Receipt.amountPaid/balanceDue/duplicate/businessName/terminal/printLines`, `Receipt.tenders[].tendered`,
    `OperatingRules.paymentTiming`, `Entitlement.groupEntitlementIds`, `CashSession.totals` (documented by PR #14).

## Deviations found against the real node (all fixed on the POS side)

| # | Real node | POS assumed | Fix |
|---|---|---|---|
| 1 | `NFC_CARD`: `identifier`=uid, `secret`=PIN, one call | uid in `secret`, then a second PIN login | `LoginViewModel`, mock, fixtures |
| 2 | `GET /health/live` at the server root | `/api/v1/health/live` (404 -> the probe always said "down") | `R007ApiClient.PingAsync` |
| 3 | `mode` on register; `minClientVersion` keys are `pos`/`mobile`/`kds`/`admin` | no `mode`; `POS_TERMINAL` key | `mode:"POS"`, `pos` key first |
| 4 | `order_state_invalid` problem had `"status":"DRAFT"` | `status` is an int | tolerant `LenientInt32Converter` (+ API fix in PR #14) |
| 5 | Restaurant pays only SERVED orders; Reception pays DRAFT and the order stays DRAFT | pay any time; paid = SETTLED | `paymentTiming`, `ServeCommand`, cart clears on balance 0 |
| 6 | percent discount max 2 decimals | `10.0000` | `10`, `12.5` |
| 7 | hold has no order; attach via `POST /bookings/{id}/order`; pay via `/payments` | hold returned `orderId`; `/confirm` | `ReceptionViewModel`, `PaymentViewModel`, mock |
| 8 | several individual tickets = several entitlements (`groupEntitlementIds`) | one QR | slips per QR; receipt printed once (was twice) |
| 9 | cashier gets 403 on `/reports/cashier-shift` | shown after close | session summary fallback (`CanViewShiftReport = report.view`) |
| 10 | `totals.nonCash` is `[]` when empty, `{}` when filled | list | tolerant `DecimalMapJsonConverter` (+ API fix in PR #14) |
| 11 | receipt has `balanceDue`, `tendered`, `duplicate`, `businessName`, `terminal` | ignored | printed (BALANCE DUE, Tendered, DUPLICATE) |
| 12 | `system/info` reports `realtime.scheme` `http` | used it as the WebSocket scheme (throws) | mapped to `ws`/`wss` in `RealtimeClient.BuildUri` |

## Realtime (push)

`docs` repo `api/realtime.md` defines Reverb (Pusher protocol 7) channels. The POS uses **REST polling** for correctness
(approval decisions every 2 s; supervisor badge every tick). `RealtimeClient` (Core) subscribes to
`private-device.{deviceId}` via `POST /broadcasting/auth` and turns `approval.decided` / `approval.requested` into
hints; if the socket is down the POS silently keeps polling. **Verified against the live node's Reverb** (`realtime-approval` scenario):
the POS subscribes with a POS device token + staff bearer, and the supervisor's decision arrives as `approval.decided` on the
requesting device's channel. Only the Windows-side lifecycle (sleep/wake, network change) remains for
[windows-hardware-verification.md](windows-hardware-verification.md).
