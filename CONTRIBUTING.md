# Contributing to otueke-pos-desktop

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
- **Hardware via abstractions.** Use `Otueke.Pos.Devices` interfaces; vendor drivers live in
  separate adapters. Every device has a `Simulated*` implementation for dev/test.
- Keep `Otueke.Pos.Core` free of WPF so it stays unit-testable on any OS.

## Data rules

- **Money:** `decimal` only — never `float`/`double`. Display amounts exactly as returned by the API.
- **Time:** UTC everywhere (`DateTimeOffset`, `TimeProvider`); local time only for display.
- **Identifiers:** UUIDs for anything created on the terminal (orders, queued operations).
- **Idempotency:** every mutating API call carries an `Idempotency-Key`; retries and offline replays
  reuse the same key.
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

- Unit-test Core logic and device simulators (`tests/Otueke.Pos.Tests`).
- Use a fake `HttpMessageHandler` for API client tests — no live API calls in unit tests.
