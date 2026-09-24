# POS client layer vs the REAL node - test report

**What was tested.** The POS's real Core / API client / HTTP pipeline (retry, auth, single-flight refresh, connectivity) /
view-models / receipt + ESC/POS layer, built exactly as the app builds them, driven against the running Laravel local node
(`007resort-api`, `integration/mvp`, MySQL 8.4, Redis, Reverb) - **not** the in-memory mock. The WPF views are not involved (they cannot run on
macOS); everything under them is.

- Harness: `tools/R007.Pos.Harness` (console, runs on macOS/Linux/Windows). One xunit test per scenario in
  `tests/R007.Pos.IntegrationTests` (category `RealNode`, **skipped unless `R007_API_BASE_URL` is set**, so CI stays green without a node).
- Result of the final run against the node on 2026-09-24: **22 / 22 scenarios pass**, plus the 238 unit tests (mock) and the 23 xunit wrappers.

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
export R007_API_BASE_URL=http://127.0.0.1:8080/api/v1                 # or the /api/v1-less root
export R007_NODE_SCRIPT=<repos>/work/api-integration/scripts/local-node.sh   # only for offline-replay (stops/starts the node)
dotnet run --project tools/R007.Pos.Harness -- --list                  # scenarios
dotnet run --project tools/R007.Pos.Harness -- -v [--only a,b] [--skip-outage]
dotnet test tests/R007.Pos.IntegrationTests                            # same scenarios as xunit (category RealNode)
```

Every scenario enrols a **fresh terminal** (a new one-time registration code issued by the IT admin through the API, `mode: POS`,
`X-Device-Token`) and signs in through the real login view-model. The node rate-limits registration (10/min/IP) and login (10/min per
identifier), so a full run pauses ~1 minute a few times; the harness waits out `429` like a patient client. Demo data is a finite resource
(tables, stock): when no table is free the harness has supervisor1 void stray orders left by earlier runs.

## What works against the real node

| Scenario | Proves |
|---|---|
| `enrol` | fresh code -> `POST /devices/register` (kind POS_TERMINAL, mode POS) -> device token; code single use; device row shows kind + mode; facility, capabilities and `paymentTiming` (Reception `PAY_FIRST`); `/health/live` probe; `minClientVersion.pos` |
| `login` | PIN via the login view-model; wrong PIN -> 401 `invalid_credentials`; username + password; **token refresh single-flight** (6 parallel requests, one rotation); refresh rotation refuses reuse; sign-out; bad bearer -> 401; `If-Match` accepted as `"v3"` and `"3"` |
| `login-nfc-pin` | card registered by IT; tap -> PIN step; card alone never signs in; wrong PIN refused; card + PIN -> one `NFC_CARD` call, card owner signed in; NFC without PIN refused |
| `catalog` | facility products, categories, prices as decimals, `q=` sku search, server-priced order (client sends no price) |
| `restaurant-order` | table map -> open table -> order on table (client UUIDv7 ids) -> send (lines ROUTED) -> pay refused before service (409 `order_state_invalid`; POS disables Pay) -> serve refused while tickets are being prepared -> kitchen plays the KDS (NEW..DISPENSED) -> **Mark served** -> pay cash with server-computed change -> receipt + drawer kick |
| `restaurant-tab` | open-tab dialog, two rounds on one tab, tab balance is the server's, settle refused before service, kitchen + serve, **split cash + transfer settle**, both orders SETTLED; duplicate transfer reference -> 409 `duplicate_reference` |
| `idempotency` | same `Idempotency-Key` replays; same client id + same body replays; same id + different body -> 409 `concurrency_conflict`; key reuse with another body refused; non-UUIDv7 id -> 422; ETag is the quoted `rowVersion`; missing `If-Match` -> 428; stale -> 412; line-add replay applies once |
| `approval-void-approved` | cashier void of a SENT order -> 202 -> waits (poll) -> supervisor's inbox approves on another terminal -> VOIDED |
| `approval-void-rejected` | supervisor rejects with a note -> the note is shown to the cashier, order unchanged |
| `approval-stepup` | wrong supervisor PIN yields no token; a waiter cannot step up for `order.void.approve`; supervisor at the till -> `X-Step-Up-Token` -> applied at once |
| `approval-discount` | discount and comp are held (`require_approval_for`); the discounted total is the **server's** (10% off), comp prices the line at 0; a 3-decimal percentage is refused by the dialog |
| `approval-cancel` | cashier cancels a waiting request; approval CANCELLED; a late decision -> `approval_already_decided` |
| `realtime-approval` | POS `RealtimeClient` connects to Reverb (`ws://host:8081`), authorises `private-device.{id}`, and receives `approval.decided` |
| `pay-first-cash` | Reception (PAY_FIRST): a DRAFT order is paid in cash, change computed by the node, cart clears, drawer kick |
| `pay-split-partial` | partial settle (1000 of 3500), over-tender refused by the dialog, split cash + transfer for the rest (one atomic request, one group), change on the cash tender; `duplicate_reference`; `amount_mismatch` (422); same tender id + same body = replay (charged once); same id + different body = 409 |
| `pay-refund-reversal` | history row -> refund needs approval (202 -> supervisor -> `PARTIALLY_REFUNDED`, refunded amount), over-refund refused; reversal with step-up -> `REVERSED`, order balance re-opens; receipt snapshot unchanged; reprint counted |
| `cash-session` | cash without a session -> 409 `cash_session_required`; open with float; second open -> 409; sale; **blind-count close** (7000 vs expected 7100) -> variance -100 shown only after closing; cashier has no `report.view` -> session summary printed; manager's shift report matches |
| `receipt-render` | the API receipt payload -> 80 mm layout (48 columns) -> ESC/POS bytes (init, cut, drawer kick, 7-bit ASCII); every amount on the node's own `printLines` is on the POS layout; BALANCE DUE + Tendered + Change; snapshot immutable after the order is paid off; reprint marked DUPLICATE, counted, no drawer kick |
| `reception-booking` | tennis court hold -> slot-fee order + 2 rackets (server priced, 8,000) -> `POST /bookings/{id}/order` -> pay cash with change through `/payments` -> booking CONFIRMED -> QR entitlement (ACCESS + RENTAL) -> QR token printed on the receipt (text preview + ESC/POS bytes) -> the gate resolves the same token -> slot shows full |
| `reception-race` | second terminal takes the slot first: 409 `slot_unavailable` explained to the cashier, slots refresh; cancel frees it; own hold cancelled |
| `reception-pool-tickets` | 3 adult + 2 child pool tickets: 5 entitlements, 5 distinct QR, one receipt + 5 slips; `POST /entitlements` idempotent (group of 5) |
| `offline-replay` | node **stopped**: queue order + line + CASH payment (encrypted file, no plaintext), transfer refused (never queued), estimate labelled; node **started**: connectivity Restored, ordered replay (3/3) with original keys and client ids, node prices the order itself, tendered/change preserved, second drain no-op; an entry the node refuses (unknown order) is surfaced with its reason |

## Bugs found

### POS side (all fixed in this PR, each with a regression test in `RealNodeFidelityTests` or the harness)

1. **NFC login could never work**: node wants `NFC_CARD` `identifier`=card uid, `secret`=PIN (one call, card alone refused); the POS sent the uid as `secret` and did a second PIN login. Login view-model, mock, fixtures and tests changed; card tap always goes to the PIN step.
2. **Health probe path**: `GET /api/v1/health/live` is 404; the probe lives at the server root. `PingAsync` always returned false, so the connectivity probe would have reported the node down forever.
3. **Realtime**: `system/info` says `realtime.scheme: "http"`; the POS used it as the WebSocket scheme (exception). Mapped to `ws`/`wss`.
4. **Pay timing not modelled**: at the Restaurant the node only takes payment for a SERVED order; the POS offered Pay on a DRAFT order and the cashier got a raw conflict. Added `paymentTiming`, a disabled Pay + banner, and a **Mark served** action (`order.serve`); a Reception (PAY_FIRST) order stays DRAFT after payment so the cart now clears on `balanceDue == 0` instead of waiting for SETTLED.
5. **Reception booking model was invented**: the node's hold has no order, `Booking.total` is the slot fee only, the paying order is created by the client and attached with `POST /bookings/{id}/order`, and `POST /bookings/{id}/confirm` returns no receipt/change. Rebuilt on: hold -> order (slot-fee product) -> attach -> rentals -> `POST /payments`. TIME_SLOT courts no longer send a party size.
6. **Several tickets, several QRs**: an order of individual pool tickets yields one entitlement each (`groupEntitlementIds`); the POS printed only the first. Now one slip per QR; the receipt was also being **printed twice** on ticket sales - fixed.
7. **Percent discount wire format**: `10.0000` is rejected (`^\d{1,3}(\.\d{1,2})?$`); now `10` / `12.5`; 3 decimals refused in the dialog.
8. **Shift report**: cashiers get 403 (`report.view`); the POS now prints the session summary from `GET /cash-sessions/{id}` (`totals`) and only uses the report screen with `report.view`.
9. **Wire tolerance**: `problem.status` as a string (node bug) and `totals.nonCash` as `[]` vs `{}` no longer break parsing (`LenientInt32Converter`, `DecimalMapJsonConverter`).
10. Device registration now sends `mode: "POS"` and a real platform string; `minClientVersion` is read from the `pos` key (the node's), `POS_TERMINAL` still accepted.
11. Receipt: prints BALANCE DUE (partial payment), cash "Tendered", DUPLICATE from the node's snapshot, business name, terminal.
12. Mock fidelity (so the offline unit tests keep protecting the same behaviours): node-style booking flow, payment timing rules (`UsePaymentTiming`), `pos` version key, root health path, cashier holds `order.serve`.

### API side (007resort-api PR #14 into `integration/mvp`, branch `fix/pos-real-node`)

1. `ProblemRenderer` let domain extensions overwrite RFC 7807 members: `order_state_invalid` shipped `"status":"DRAFT"`. Fixed (`entityStatus` carries the domain value); unit test + updated payments test.
2. `totals.nonCash` serialised as `[]` (empty) or `{}` (filled). Now always an object; feature test.
3. OpenAPI said NFC_CARD carries the uid in `secret` with no `identifier` (the implementation needs uid + PIN); corrected, and the additive fields the POS relies on are documented (`paymentTiming`, receipt extras, `groupEntitlementIds`, `CashSession.totals`).
   Noted, not changed: the cashier lacks `report.view`; percent values allow 2 decimals; registration/login rate limits.

## Notes on the node's behaviour worth knowing

- `If-Match` is accepted as `"v3"` and as the bare `"3"` the node emits in `ETag`.
- A failed/interrupted run leaves demo tables occupied and stock depleted (`insufficient_stock` on the Restaurant's plantain); reseed with `scripts/local-node.sh stop && seed && start` if a scenario fails for that reason.
- `X-Offline-Captured-At` / `X-Offline-Staff-Id` headers on replay are accepted (and ignored).
- The demo `KDS_MAIN_KITCHEN` token + kitchen1 is enough to move Restaurant tickets to READY/DISPENSED, which is what lets an order be served.

## Still needs Windows hardware / a Windows host

Everything in [windows-hardware-verification.md](windows-hardware-verification.md) is unchanged by this work, in particular:
the WPF views and touch behaviour (XAML binding is guarded by `XamlBindingTests`, but nothing has been rendered), DPAPI (`DpapiKeyProtector`) for the
queue and the device identity (the harness uses `InsecureKeyProtector`, which the app must never select against a real node), the raw Windows spooler
printer + real ESC/POS byte output on a thermal printer (bytes were verified structurally and against the node's amounts, not on paper), the cash-drawer kick,
a keyboard-wedge NFC reader/barcode scanner (input routing is unit-tested with simulated devices), idle lock, and Reverb lifecycle across
sleep/wake and network changes on a real till. The Windows CI job builds and tests everything except the `RealNode` category.
