## Summary

<!-- What does this change do and why? Link the issue / ADR. -->

## Type

- [ ] feat
- [ ] fix
- [ ] docs
- [ ] chore / refactor / build / ci

## Checklist

- [ ] Tests added or updated; `dotnet build` and `dotnet test` pass (Windows CI green)
- [ ] No business rules implemented in the client (pricing, tax, permissions, stock, entitlements come from the API)
- [ ] Behaviour is driven by device registration / facility capabilities / staff permissions — no per-station hard-coding
- [ ] Offline queue changes (if any) keep entries encrypted, append-only, idempotency-keyed and replayed via the API only
- [ ] Hardware access goes through `Otueke.Pos.Devices` abstractions (no vendor coupling in Core/App)
- [ ] Money is `decimal` and displayed from API values; timestamps are UTC
- [ ] Sensitive actions (drawer open, voids, refunds, overrides) are authorized and audited by the API
- [ ] No secrets, credentials, device keys or `.env` files committed; nothing sensitive logged (PINs, NFC UIDs, tokens)
- [ ] Docs updated (README / CONTRIBUTING / docs/)
