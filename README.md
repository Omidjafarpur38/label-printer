# label-printer

Windows print agent for the jeans-production-erp label workflow. **Status: experimental.** An unsigned
test build exists; it has not been used with a physical printer or a production ERP.

| Area | State |
|---|---|
| Core: job identity, state machine, durable journal, outbox | implemented; automated tests |
| ERP synchronisation and HTTPS transport | implemented; tested against a mock and, manually, against the real ERP backend on loopback |
| Windows RAW spooler, DPAPI credential store | implemented; tested on a Windows CI runner with a file-backed print queue |
| Installer (per-user, unsigned) | implemented; install/upgrade/uninstall tested on a Windows CI runner |
| Physical printing | **not tested** |
| Code signing, auto-update, Windows Service, command validation, journal compaction | not implemented |

## Build and test

Requires the .NET 10 SDK (see `docs/tooling.md`). No NuGet packages are referenced.

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

- `docs/setup-guide.md` — install, configure, pair, run, diagnose, remove
- `docs/recovery.md` — quarantine, corrupt or restored journals, unknown jobs
- `docs/integration-testing.md` — what was tested against what, and what was not
- `docs/architecture.md`, `docs/durability.md` — design and its limits
- `docs/a2-mock-integration.md` — synchronisation rules
- `docs/erp-baseline.md` — ERP contract as read from source, and discrepancies
- `docs/tooling.md` — SDK, CI actions, installer tooling
- `docs/releases/` — release notes
