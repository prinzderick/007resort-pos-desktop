# What can only be verified on Windows hardware

The dev machine that builds this repo is macOS: it **builds** the whole solution (including the WPF project, via
`EnableWindowsTargeting`) and runs **all unit tests** (view models, API client, queue crypto/replay, receipts, XAML
binding guard), but it cannot **run** the WPF app or talk to Windows devices. CI (`windows-latest`) builds and runs the
same tests on Windows. Everything below needs a person with a Windows till, a printer and (ideally) a real backend.

Use mock mode (`R007_MOCK=true`) for steps 1 to 4 so the backend is not a variable; repeat the marked ones against the
real API.

> **Update:** the client layer (API client, pipeline, view-models, queue, receipt rendering) has since been driven against a REAL node from
> macOS (`tools/R007.Pos.Harness`, see [REAL_API_TEST_REPORT.md](REAL_API_TEST_REPORT.md)). Items marked *(client layer verified against the real node)*
> below no longer need the backend to be checked for logic; on Windows they only need the hardware/OS part (WPF, DPAPI, spooler, devices, sleep/wake).

## 1. WPF rendering and touch (run once per screen size)

- [ ] App starts maximised on the till display (1024x768 up to 1920x1080); no clipped controls at 1024x700.
- [ ] Every screen renders (Setup, Login, Sell, Tables & tabs, Reception, Approvals, Cash session, History, Queue) and every
      dialog (Payment, Supervisor approval, Member lookup, Open tab). `XamlBindingTests` proves each binding *path* resolves;
      a human must confirm the pixels (wrapping, scrolling, contrast) and that the Output window shows **no
      `System.Windows.Data Error`** lines while clicking through.
- [ ] Touch: PIN pad, product tiles, quantity +/- and dialogs work with a finger (buttons are >= 48 px); the Windows touch
      keyboard appears for text boxes and does not cover the field being edited.
- [ ] `PasswordBox` fields (login password, supervisor PIN) clear after use.

## 2. Keyboard-wedge scanner and NFC reader (the input hook in `MainWindow`)

- [ ] A USB barcode scanner in keyboard mode (suffix Enter): scanning on Sell adds the product; nothing is typed into the
      search box; the first two characters that slip through before the burst is recognised are removed.
- [ ] A USB NFC reader in keyboard-wedge mode (UID + Enter): tapping on the login screen is treated as a card, elsewhere it
      is ignored. Confirm the UID format the reader emits (hex, byte order) matches what the API stores for `NFC_CARD`.
- [ ] Tune `Pos:WedgeMaxKeyIntervalMs` (default 50 ms) if a slow scanner is mistaken for typing, or fast typing for a scan.
- [ ] `R007_Pos__RequireNfcAndPin=true`: card alone never signs in; card + PIN does; a card/PIN mismatch is refused.

## 3. Receipt printer and cash drawer (ESC/POS over the Windows spooler)

Set `Pos:Printer:Kind=Windows` and `Pos:Printer:Name` to the queue name (driver: "Generic / Text Only" or the vendor's
RAW-capable driver; the app sends RAW bytes, so a graphics driver must not re-render them).

- [ ] Status: unplugged / paper-out / cover-open shows "Printer not ready (...)" and the sale is **not** lost.
- [ ] Layout: 80 mm paper, 48 columns (Font A) lines up; if columns are 42 use `Pos:Printer:CharactersPerLine=42`.
- [ ] Text: Naira prints as `N` (transliterated); accents fall back to ASCII. Confirm no garbage bytes (code page 437).
- [ ] Double-size title, bold TOTAL, centred header, partial cut after the receipt.
- [ ] **QR code** (GS ( k): scans with the Sports Entrance / Store tablets; module size 6 is readable. Some printers need a
      different model/size command; capture the `.bin` from `receipts\` (File printer) and replay it with the vendor tool
      to compare.
- [ ] Cash drawer opens on a **cash** receipt (ESC p 0 25 250 on pin 2); does not open on card or reprint. Some drawers use pin 5
      (`ESC p 1 ...`): adjust `EscPosRenderer.DrawerKick` if needed.
- [ ] Reprint from History prints `*** DUPLICATE ***` and no drawer kick.

## 4. DPAPI and the encrypted stores (Windows only)

- [ ] Registration writes `%LOCALAPPDATA%\R007Pos\site\device.bin`; the file is unreadable text; the app restarts straight to
      Login without asking to register again.
- [ ] Enqueue something offline (see 5): `emergency-queue.bin` and `.key` exist; `.key` differs per Windows user; copying both
      files to another user/machine makes the queue unreadable (moved to `*.unreadable`, reported) rather than crashing.
- [ ] Delete `device.bin` -> Setup screen returns; the queue is untouched.

## 5. Offline behaviour against a real node (repeat with the backend)

*(Queue, ordered idempotent replay, refusal surfacing and the encrypted file were verified against the real node with the node stopped/started: `offline-replay`. On Windows check DPAPI-backed keys and the UI states.)*

- [ ] Pull the network cable mid-sale: status bar turns `OFFLINE`; with `allowOfflineOrders` the cart keeps working as
      **PENDING CONFIRMATION** with a labelled ESTIMATE; card/transfer/Paystack payments are refused with the cash-only message.
- [ ] Take a cash payment offline: dialog says NOT CONFIRMED, no receipt prints, Queue tab lists it.
- [ ] Restore the network: the queue drains in order without operator action; the order settles on the server; the drawer
      count and shift report include the cash.
- [ ] Make the server refuse a replayed item (e.g. pay the order on another till first): it appears under "Refused by the
      server" and stays until acknowledged.
- [ ] Fill the queue (200 items) or wait 30 minutes offline: new offline actions are refused with a clear message.
- [ ] Kill the app process during a drain, restart, sign in: remaining items replay; nothing is applied twice
      (check the API's idempotency replays and payment count).

## 6. Realtime (Reverb) and supervisor approvals (needs a live node)

*(Subscription + `approval.decided` push against the live Reverb: verified by `realtime-approval`. Still to do here: reconnect after Reverb restart, sleep/wake, `device.command`.)*

- [ ] `GET /system/info` advertises `realtime`; the POS subscribes to `private-device.{id}` (check `POST /broadcasting/auth` 200).
- [ ] A void/discount that needs approval: a supervisor tablet gets `approval.requested`; approving there wakes the waiting
      dialog within about a second (otherwise within the 2 s poll).
- [ ] Stop Reverb: the POS keeps working by polling; on restart it re-subscribes and refreshes the approvals badge.
- [ ] `device.command` FORCE_LOGOUT / LOCK / REVOKE signs the terminal out with a banner.
- [ ] Confirm the exact event payload shapes match `api/realtime.md` (the client reads `data.approval.id` and `data.command`).

## 7. Contract-dependent behaviour to confirm with the backend (see api-contract-notes.md)

- [x] `If-Match` values: `"v{rowVersion}"` and the bare `"{rowVersion}"` are accepted by the node (lines, send, void, adjustments, booking order/confirm, tab orders).
- [ ] Barcode lookup through `GET /catalog/products?q=`: does `q` match the barcode/SKU column? (mock does; the contract just says `q`).
- [x] Booking flow: the node's hold has no order; the POS builds the order, attaches it and pays through `POST /payments` (`reception-booking`).
- [x] `requireCashSession` facilities reject cash without a session (`409 cash_session_required`; `cash-session` scenario). Check the POS message on screen.
- [ ] Shift report: `expectedCash`, `countedCash`, `variance` and per-tender totals reconcile with the payments list; the
      `freshness.stale` warning appears when the node is degraded.
- [ ] Time zone: receipts print Africa/Lagos time (UTC+1).

## 7b. Waiter collection desk (WPF rendering not runnable off Windows; logic is covered by view-model tests + the `collect-*` harness scenarios)

- [ ] 'Collected by waiters' tab: rows render, age text turns amber at half the window, red when near expiry (`AgeText` DataTriggers), Confirm/Reject buttons are 48 px touch targets, list updates without focus loss when a `payment.collected` push arrives.
- [ ] Confirm dialog: the reference box, mismatch warning (amber) then second press; receipt prints on the thermal printer and the cash drawer opens for a CASH confirmation.
- [ ] Reject dialog: reason box, red warning banner, supervisor alert banner appears on a supervisor POS on `payment.alert`.
- [ ] Pre-bill on the real printer: the two NOT A RECEIPT banners, 48-column layout, pay-link QR (when `bill_pay_link_enabled`), reprint counter; no drawer kick.
- [ ] Status-bar badge ('N collected by waiters - confirm') and the tab badge; the badge disappears when the list empties.
- [ ] Cash handover desk: Count and receive dialog with the live variance preview; supervisor 'Sign off variance' button only visible on PENDING_SIGNOFF rows.
- [ ] Table tiles, tab rows, open-orders list and cart show the state chips; Reopen bill and Print bill buttons enable/disable with the order state.

## 8. Soak

- [ ] A full trading day on one till in mock mode with the window left open (memory growth, no unhandled exceptions in
      `%LOCALAPPDATA%\R007Pos\logs`); idle lock after `Pos:IdleLockMinutes` returns to Login and keeps the queue.
