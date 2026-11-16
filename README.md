# label-printer

Windows print agent for the jeans-production-erp label workflow. **Status: A1 + the approved subset of A2** —
the platform-independent core and its ERP synchronisation logic, tested against a mock. There is no
networking, no Windows printing, no installer and no release.

| Area | State |
|---|---|
| Job identity, content hash, state machine | implemented, synthetic tests |
| Durable file-backed journal, idempotent acceptance, outbox | implemented, synthetic tests |
| Spooler abstraction + deterministic fake | implemented |
| Contract models | read from ERP source, never exercised over HTTP — not production-ready |
| ERP synchronisation (claim, accept, events, reconcile) | implemented against an in-memory mock only |
| HTTP/TLS client, pairing, credential storage, DPAPI, service, printer discovery, TSPL/ZPL handling, CI | not started |

## Build and test

Requires the .NET 10 SDK (see `docs/tooling.md`). No NuGet packages are used.

```bash
dotnet restore LabelPrinter.slnx --locked-mode
```

```bash
dotnet build LabelPrinter.slnx --no-restore
```

```bash
dotnet run --project tests/LabelPrinter.Agent.Core.Tests --no-build
```

The test program prints one line per test and exits non-zero if any test fails. An optional argument filters
by name, e.g. `-- Journal`.

## Documents

- `docs/architecture.md` — components, state machine, idempotency, payload and copy safety
- `docs/durability.md` — journal format, guarantees, and what is *not* guaranteed
- `docs/erp-baseline.md` — what was verified in the ERP source and where it disagrees with its own contract draft
- `docs/tooling.md` — SDK provenance, lockfiles, pending test-package approval
- `docs/a2-mock-integration.md` — what the mock ERP integration does and does not cover
- `docs/a2-proposal.md` — original A2 proposal (only part of it was approved)
