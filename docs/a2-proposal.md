# A2 proposal — ERP client against a local/mock ERP

Status: proposal only. Nothing here is implemented or approved.

Goal: drive `JobLedger` from the ERP's agent API, proven end to end against a local mock, with no production
host, no real credential and no printer (the fake spooler stays).

## Scope

| Topic | Proposal |
|---|---|
| HTTPS and certificates | `HttpClient` with default platform validation, HTTPS-only base address, TLS 1.2+, no redirects, no proxy auto-detection, no option to disable validation. Tests use a loopback test server with a per-run generated CA trusted by that client instance only (never the machine store). Certificate pinning: decide, do not build. |
| Agent identity | Credential from `/pair`, sent as `Authorization: Bearer`. Behind `ICredentialStore`; A2 ships only an in-memory/test-file implementation holding synthetic values. Identity is separate from any user JWT and revocable server-side. DPAPI is a later Windows milestone. |
| Polling | `POST /jobs/claim` long-poll (`waitSec` ≤ 20, `max` 1–5); exponential backoff with full jitter on network errors/5xx/429, honouring `Retry-After`; heartbeat every `heartbeatSec` with version negotiation; stop claiming on 401/403/426 and on `paused`. |
| Claim → durable acceptance | Per envelope: `Accept` for a known job needs no download. For a new job: `GET payload` → `JobLedger.Accept` → only after `AcceptedNew`/`AlreadyKnown` call `/accept` with the pending attempt → `AcknowledgeAccept` on 2xx. `Conflict` is reported, never printed. |
| Payload retrieval | Streamed with a hard size cap from the envelope, compared against `X-Payload-Sha256` and the envelope hash; one refetch on mismatch, then `failed` (never submitted). |
| Events | Send `PendingEvents` in `seq` order, ≤ 20 per request; treat `applied`, `duplicate` and `stale` results as delivered → `AcknowledgeEvents`; 409 `EVENT_CONFLICT`/`TRANSITION_INVALID` stop that job's stream and surface it. Local-only transitions the ERP forbids are mapped explicitly (see `erp-baseline.md` §6). |
| Reconciliation | On start and after `LEASE_LOST`: send non-terminal local jobs; `report_events` → resend from `fromSeq`; `abandon` → cancel if still Accepted, otherwise keep local truth; `resume` → continue **only** if locally Accepted. |
| Cancellation | `cancelJobIds` from claim/heartbeat → `JobLedger.Cancel`; too late is reported, not forced. |
| Rotation / revocation | `/credential/rotate` → store new credential → first successful use commits it; keep the old one until then. `AGENT_REVOKED`/`AGENT_CREDENTIAL_EXPIRED` → stop all network activity, keep the journal, surface state. Tested against the mock. |
| Configuration | Base URL, timeouts and intervals from a config object; test configuration points only at loopback; a guard refuses non-loopback hosts in A2 builds. |
| Contract tests | Wire DTOs + JSON mapping pinned by the ERP's `docs/label-agent/fixtures` (copied at a recorded commit) and by the flat error body. |

## Out of scope for A2

Real pairing against a deployed ERP, production credentials, DPAPI, Windows Service, printer discovery,
P/Invoke, physical printing, TSPL/ZPL parsing, installer, updater, CI, releases.

## Mock ERP

Either an in-process `HttpMessageHandler` fake (no sockets, no packages) or a loopback Kestrel test server
(uses the in-box ASP.NET Core shared framework; real TLS path). Recommendation: handler fake for behaviour,
one small Kestrel suite for the TLS/certificate path.

## Approvals needed before starting

1. Test package set in `docs/tooling.md` (or confirmation to stay on the in-repo runner).
2. Whether the test project may reference the ASP.NET Core shared framework for a loopback server.
3. Copying ERP fixtures into this repo (they are synthetic per the ERP docs; confirm).
4. Decision on the ERP corrections recommended in `erp-baseline.md` (reconcile `resume` for Unknown,
   `unknown → failed`, error body shape, `serverSupported[]`), or acceptance of agent-side handling as is.
5. Journal compaction: in A2 or deferred.
6. Push / PR / CI policy for this repository.

Not a blocker for A2: the Authenticode certificate (public-release gate only).
