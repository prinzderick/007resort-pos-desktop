# Contributing to 007resort-pos-desktop

## Workflow

- `main` is protected: changes arrive via pull request with green CI (Windows) and review.
- Branch names: `feature/<short-name>`, `fix/<short-name>`, `docs/<short-name>`, `chore/<short-name>`.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/)
  (`feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`, `build:`, `ci:`).
- Fill in the PR template checklist. Warnings are errors — fix, don't suppress.

## Client rules

- **The API is the brain.** Never implement pricing, tax, discount, permission, stock or
  entitlement rules in the POS. Ask the API; render the answer.
- **No local database.** The only local persistence allowed is the encrypted emergency offline
  queue and OS-protected device credentials.
- **One app for all stations.** Never branch on station names; use the capabilities/permissions the
  API returns for the registered device, facility, operating point and staff member.
- **Hardware via abstractions.** Use `R007.Pos.Devices` interfaces; vendor drivers live in
  separate adapters. Every device has a `Simulated*` implementation for dev/test.
- Keep `R007.Pos.Core`, `R007.Pos.Devices` and `R007.Pos.ViewModels` free of WPF so they stay unit-testable on any OS.
  Only `R007.Pos.App` (views, composition root) may reference WPF, and it contains no logic worth testing.
- **Screens follow features:** add a screen by adding a flag to `TerminalFeatures` (from capabilities + permissions)
  and a tab in `MainViewModel.BuildItems`; never test a facility or station name.

## Data rules

- **Money:** `decimal` only — never `float`/`double`. Display amounts exactly as returned by the API.
- **Time:** UTC everywhere (`DateTimeOffset`, `TimeProvider`); local time only for display.
- **Identifiers:** UUIDs for anything created on the terminal (orders, queued operations).
- **Idempotency:** every mutating API call carries an `Idempotency-Key`; retries and offline replays
  reuse the same key. Generate the key once per *user intent* (e.g. per payment attempt) and keep it until the
  intent changes. `If-Match` uses the aggregate's `rowVersion` (`"v{n}"`).
- **Client ids:** orders, lines, tabs and tenders created on the terminal carry a client UUIDv7 `id`
  (`ClientIds.New()`), so they can be created offline and replayed safely.
- **Financial records are immutable:** corrections are reversals issued through the API.

## Offline queue

Encrypted at rest, append-only, idempotency-keyed, replayed in order **through the API only**.
The API decides which operations may be queued and re-validates everything on replay; rejected
entries are surfaced to staff, never silently dropped.

## Security

- **No secrets in git.** No device keys, tokens, passwords or `.env` files. CI runs gitleaks.
- Never log PINs, NFC UIDs in clear, tokens or payment data.
- Sensitive actions (drawer open, voids, refunds, overrides) must be authorized by the API, which
  audits them — the client never decides on its own.

## Testing

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet test R007.Pos.sln -c Release
```

- View-model tests run against `TestPos` (`tests/R007.Pos.Tests/TestPos.cs`): the **real** client stack over the
  in-memory `MockApiHandler`, an encrypted temp queue and a simulated printer. Prefer this to hand-rolled fakes so
  retry, idempotency, `If-Match`, approvals and replay are exercised end to end. `MockApiHandler` knobs
  (`Offline`, `FailNextWith503`, `LoseNextResponses`, `ExpireAccessTokens`, `DecideApprovalAsSupervisor`) simulate outages.
- Dialogs are driven by `ScriptedNavigator` (no window needed).
- **When you change a contract type**, update `ContractFixtures/` from `api/openapi/v1.yaml`:
  `ContractConformanceTests` fails if we send a property the spec lacks, omit a required one, or drop a required
  response field.
- **When you add or change XAML**, add `d:DataContext="{d:DesignInstance Type=...}"` to the view:
  `XamlBindingTests` resolves every `{Binding}` path against the view model by reflection and checks two-way
  bindings have setters. WPF binding errors otherwise only show at run time, and the app cannot be run on macOS/CI.
- Receipt layout has a snapshot test (`Receipt_Snapshot_80mm_48Columns`); review the diff when it changes.
- Never call a live API in unit tests.
