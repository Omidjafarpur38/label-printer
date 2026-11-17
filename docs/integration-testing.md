# What was tested against what

| Layer | Against | Where it runs | Status |
|---|---|---|---|
| Core, journal, sync logic | in-memory mock ERP, fake spooler, fault-injecting stores | CI (Linux + Windows) | automated |
| HTTP transport | mock ERP served over real loopback sockets | CI (Linux + Windows) | automated |
| HTTP transport + sync | **real ERP backend** (Fastify + PostgreSQL 16 + Redis) on loopback, synthetic data, fake spooler | developer machine only (`tools/test-erp`) | run manually; skipped in CI |
| Windows RAW spooler | real Windows spooler, print queue whose port is a file | CI (Windows runner) | automated |
| DPAPI credential store, ACL, NTFS journal lock | real Windows APIs | CI (Windows runner) | automated |
| Installer | silent install → upgrade → uninstall on a clean Windows Server runner | CI (Windows runner) | automated |
| Agent ↔ real ERP ↔ real Windows spooler in one run | — | — | **not tested** (the ERP repo is private to CI; no Windows machine with ERP access) |
| Interactive installer UI, SmartScreen prompts, Windows 10/11 client editions | — | — | **not tested** |
| Physical label printer | — | — | **not tested** |
| Production ERP | — | — | never contacted, by design |

## Real ERP run (2026-11-16)

ERP commit `98d49cfd` (agent-facing files identical to `83a936a3`), database `lp_test_erp` created for the
purpose, flag `label.print.v2` enabled in that database only. 10 integration tests passed:

- pair → heartbeat → printer report → claim → payload → accept → events → `confirmed_completed`;
  the payload was the ERP's own TSPL output and reached the fake spooler with the envelope's SHA-256;
- lost event acknowledgement + restart → resent → stored once by the ERP;
- crash inside the spooler call → `unknown` on the ERP, no second submission;
- error bodies: 404 `JOB_NOT_FOUND`, 409 `LEASE_LOST`, 422 `EVIDENCE_REQUIRED` arrive in `error_code`;
- 404 blocks the outbox, nothing acknowledged;
- credential rotation commits on first use; the old credential then gets 401 `AGENT_REVOKED`;
- revoking the agent stops the runner; cancel request reaches the agent through heartbeat;
- a job for a printer/DPI the operator did not select is refused and reported `failed`;
- rate limit: the 7th heartbeat in a minute gets 429.

Also run once by hand on Linux in dev mode: the `LabelPrinterAgent` CLI (`pair` with a code file,
`select-printer`, `run`) against the same ERP; a test print reached `submitted_to_spooler` with the dev
discard spooler.

### Facts learned from the real ERP (not visible in the mock)

- A 429 from the ERP carries `error_code: "SYSTEM_INTERNAL_ERROR"` and `Retry-After: 60` — the rate-limit
  answer falls through the ERP's generic error branch. The agent relies on the status and the header only.
- `/pair` is limited to 5 per minute per IP; heartbeat to 6 per minute per credential.
- User-side POSTs with identical bodies within 2 s are rejected (`DUPLICATE_SUBMIT`) unless they carry
  `X-Client-Request-Id`. Agent routes are not affected.
- With `X-Client-Request-Id`, the ERP's idempotency layer stores and can replay the response of
  `POST /api/label-print/pairing-codes`, i.e. a plaintext pairing code, for the replay window. Worth a look on
  the ERP side; not changed from here.

## Limits

- The real-ERP tests used the fake spooler and ran on Linux. The Windows spooler tests used the mock ERP.
  No single run joined real ERP and real Windows spooler.
- The file-port print queue proves that the bytes handed to Windows are the approved bytes. It says nothing
  about any label printer, its language, or its driver.
- "Submitted once" always means one call to the spooler under test.
