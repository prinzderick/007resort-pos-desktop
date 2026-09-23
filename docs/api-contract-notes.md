# API contract notes (POS)

Source of truth: `api/openapi/v1.yaml` in the docs repo (checked at commit `e205d58`). The POS types live in
`src/R007.Pos.Core/Api/Contract.*.cs`; `ContractConformanceTests` guards drift. This page records how the POS uses
the contract, where it had to make a call, and what it needs from the contract/backend owners.

## How each flow maps to the contract

| POS action | Endpoints |
|---|---|
| Start-up | `GET /system/info` (`minClientVersion["POS_TERMINAL"]` -> "update required" banner), `GET /health/live` (probe) |
| Enrol | `POST /devices/register {name, kind:POS_TERMINAL, hardwareId, registrationCode, platform, appVersion}` -> `deviceToken` (DPAPI) |
| Facility | device `facilityId` (else `homeFacilityId`); `GET /facilities/{id}/capabilities`; name from `GET /organization/facilities/{id}` (optional) |
| Login | `POST /auth/staff/login` `PIN` (`identifier` = staff number) / `NFC_CARD` (`secret` = card UID) / `PASSWORD`; `POST /auth/staff/refresh` (single flight, rotating); `POST /auth/staff/logout` |
| Catalog | `GET /catalog/categories`, `GET /catalog/products?facilityId=` (all pages); barcode = `q=` search |
| Tables / tabs | `GET /tables`, `POST /tables/{id}/open`, `POST /tabs`, `GET /tabs`, `POST /tabs/{id}/settle` |
| Orders | `POST /orders` (client id, first line), `POST /orders/{id}/lines`, `DELETE .../lines/{lineId}`, `POST .../send`; `If-Match: "v{rowVersion}"` |
| Approvals | `POST .../void` and `.../lines/{id}/adjustments` -> `200` or `202 ApprovalOutcome`; `GET /approvals/{id}` (poll), `GET /approvals?scope=approvable`, `POST /approvals/{id}/decision`, `POST /approvals/{id}/cancel`; inline: `POST /auth/staff/step-up` then `X-Step-Up-Token` |
| Payments | `POST /payments {facilityId, cashSessionId, allocations, tenders[{tenderType, amount, reference, tendered, id}]}` -> `PaymentResult{payments, receiptId, changeDue}`; `POST /payments/{id}/refund` / `/reversal`; Paystack `POST /payments/paystack/initialize` + `GET .../verify/{reference}` |
| Receipts | `GET /receipts/{id}` (`?reprint=true` for reprints), `GET /orders/{id}/receipt` |
| Cash | `POST /cash-sessions`, `GET /cash-sessions?filter[status]=OPEN&filter[staffId]=`, `POST .../close {countedCash}`, `GET /reports/cashier-shift/{id}` |
| Reception | `GET /bookings/resources`, `.../availability`, `POST /bookings/hold`, `POST /bookings/{id}/confirm {tenders, cashSessionId}`, `GET /entitlements/{id}`, `POST /entitlements {orderId}` |
| Customers | `GET /memberships?q=` (no separate customers resource in the contract) |

## Decisions the POS made

1. **Statuses/kinds are strings**, not enums, so an unknown value from a newer API does not break parsing (contract README).
2. **Order line notes are the only "modifier"**: the contract has no modifier model, so quick-note chips become `notes`.
3. **Quantity change = remove + re-add** (draft orders only): there is no PATCH for a line quantity.
4. **NFC + PIN** is done client-side in two contract calls: `NFC_CARD` login (provisional session) then `PIN` login for the
   same staff number; only the PIN session is kept and the card-only session is revoked. The server does not enforce
   "both factors" (see requests).
5. **Booking + rentals**: rentals are added as lines on the booking's `orderId`; the booking is paid and confirmed through
   `POST /bookings/{id}/confirm`. Ticket/rental products sold at the till are paid with `POST /payments`, then
   `POST /entitlements {orderId}` issues the QR.
6. **Refund/reversal** return `Refund`/`PaymentReversal` with `status: PENDING_APPROVAL` + `approvalId`; the POS waits on that approval like any other.
7. **Emergency queue** allow-list is stricter than the contract: only CASH tenders, never card/transfer/Paystack, even when `allowOfflinePayments=ALL`.
8. **Approval push** is a hint only: `ApprovalCoordinator.NotifyDecided(approvalId)` re-reads `GET /approvals/{id}`. See "Realtime" below.

## Requests to the contract / backend owners

1. **Station authentication policy.** Add an operating rule (e.g. `requireNfcAndPin`) readable **before login** (for
   example on `GET /devices/{id}` or `/system/info`), and enforce it server-side: `PIN`/`NFC_CARD` alone must be refused
   at such stations. Today it is POS configuration (`Pos:RequireNfcAndPin`) and the server cannot enforce it.
2. **Line quantity edit** (`PATCH /orders/{id}/lines/{lineId} {quantity}`) to avoid remove + re-add.
3. **Modifiers** (or a documented `notes` convention) if kitchens need structured options.
4. **Cash drawer authorisation.** The client design brief expects a manual "open drawer" to be authorised and audited
   by the API; there is no endpoint. The POS therefore kicks the drawer only for cash tenders (via the receipt) and
   offers no manual open. Add e.g. `POST /cash-drawer/open {reason}` (permission + audit) if manual opening is required.
5. **Offline policy visibility.** `allowOfflineOrders` / `allowOfflinePayments` are read from `GET /facilities/{id}/capabilities`,
   which needs a session; a terminal that loses the network before its first login of the day has no policy. Consider
   caching guidance or including the rules on the device resource.
6. **Line-level void** for sent lines (only order-level void exists; draft lines are simply removed).
7. **Booking + rental in one transaction**: confirm that adding lines to `booking.orderId` while `HELD` is supported and
   that `Booking.total` reflects them; the POS assumes so and reads the order balance for the amount due.
8. **Receipt QR**: the POS uses `Receipt.qrPayload` when present and otherwise the entitlement `qrToken` from
   `GET /entitlements/{id}`; state which is canonical for the printed QR.
9. **Idempotent tender ids**: confirm a replayed tender id whose payment belongs to a *different* group returns
   `409 concurrency_conflict` (the POS never reuses a tender id across attempts).

## Realtime (push)

`docs` repo `api/realtime.md` defines Reverb (Pusher protocol 7) channels. The POS uses **REST polling** for correctness
(approval decisions every 2 s; supervisor badge every tick). `RealtimeClient` (Core) subscribes to
`private-device.{deviceId}` via `POST /broadcasting/auth` and turns `approval.decided` / `approval.requested` into
hints; if the socket is down the POS silently keeps polling. Verify against a live Reverb node
([windows-hardware-verification.md](windows-hardware-verification.md)).
