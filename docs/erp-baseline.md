# ERP baseline and contract discrepancies

Baseline: `Omidjafarpur38/jeans-production-erp`. A1 inspected commit
`0f48373fa70d8ab234e362def62913bac95ec6cf`; A2 re-checked at `83a936a3573b89dda95990d04576e8d3b38cd266`
(2026-11-15). Between the two, only `backend/src/label-print/output.ts` (lazy encoder loading) and documents
changed; the agent service, print service, controller, routes, error handler and migration are identical.
Read-only inspection of source; the ERP was not run and not modified. No separate server verification report
was available.

Files read: `backend/src/label-print/{agent.service,print.service,errors,output,flag}.ts`,
`backend/src/controllers/label-agent.controller.ts`, `backend/src/routes/label-agent.routes.ts`,
`backend/src/app.ts` (global error handler), `backend/migrations/0246_label_agent.sql`,
`docs/label-agent/{contract-v0.1-draft,agent-maintainer-handoff}.md`.

Everything below is "verified against source", which is weaker than "observed on the wire".

## 1. Credential format — resolved (format only)

- Generator: `lpa1_<keyId>.<secret>`; `keyId = base64url(16 random bytes)` (22 chars),
  `secret = base64url(32 random bytes)` (43 chars). Server stores `sha256(secret)` only.
- Parser: `^Bearer lpa1_([A-Za-z0-9_-]{16,48})\.([A-Za-z0-9_-]{32,64})$` on the `Authorization` header.
- Lifetime 90 days; rotation creates a `pending_rotation` credential (24 h) that is committed on first use and
  revokes its predecessor; re-pairing revokes all earlier credentials of that agent.
- The repository contains no credential value. `AgentCredential` (A2) only recognises the shape and redacts the secret; nothing stores or sends one.

## 2. HTTP error structure — discrepancy, isolated

- Contract draft §9: `{ "error": { "code", "message", "retryable", "retryAfterSec" } }`.
- Actual (`app.ts` `setErrorHandler`): flat
  `{ "error": <JS error name, normally "Error">, "message", "trace_id", "error_code", "user_friendly_message_fa" }`.
  `error_code` is the label-print code only if registered in `error-catalog.ts`; otherwise a generic
  `SYSTEM_HTTP_ERROR` / `SYSTEM_AUTH_ERROR`. Validation failures: 422, `error: "VALIDATION_ERROR"`,
  `error_code: "SYSTEM_VALIDATION_ERROR"`, plus `details`.
- There is no `retryable` field. Retryable catalog codes get a `retry-after: 30` header.
- Two paths bypass the handler: the events route returns `{ error: "VALIDATION_ERROR", message }` with **no**
  `error_code` when an event's `jobId` differs from the path; rate-limit (429) bodies were not traced.
- 426 `CONTRACT_VERSION_UNSUPPORTED` carries the server versions only inside the Persian message text, not as
  the `serverSupported[]` field the draft promises.
- Agent position: `ErpErrorBody` models the flat shape and is marked **Draft**. A2 must key on HTTP status +
  `error_code`, tolerate its absence, and never parse `message`.

## 3. Claim envelope — resolved against source

`POST /jobs/claim` → `{ jobs: Envelope[], cancelJobIds: string[], paused: boolean }`. Envelope fields emitted:
`jobId, idempotencyKey, contractVersion ("0.1"), agentId, printerId, profileId, profileVersion, kind (test|label),
output{language, profile, contentType, sizeBytes, sha256, pages, dpi, mediaMm{w,h}, gapMm}, copies, createdAt,
authorizationExpiresAt, agentMustNotSubmitAfter, correlationId, reprintOf?, deliveryAttempt, leaseUntil`.

- `templateId` / `templateVersion` from the draft are **not** emitted.
- **No tenant id reaches the agent**, in the pair response or the envelope. Job identity is therefore scoped
  as `(agentId, jobId)`; the ERP binds `agentId` to one tenant (`label_agents` unique on `(tenantId, installId)`).
- `mediaMm.w/h` and `gapMm` can be absent (profile lookup may return nothing): modelled as optional.
- `agentMustNotSubmitAfter` always equals `authorizationExpiresAt` (currently created + 1 h).
- Draft proposes pages ≤ 2000; the code sets `pages = copies` (≤ 5000) for test prints. The agent bounds pages
  at 5000 to stay compatible.
- `copies` is informational: the ERP encodes one page per copy into the payload bytes.

## 4. Reconcile actions — resolved, with a safety caveat

`POST /jobs/reconcile` body `{ jobs: [{ jobId, state, lastSeq }] }` (≤ 100) → `{ directives: [{ jobId, action, fromSeq? }] }`,
`action ∈ resume | abandon | report_events`.

- `report_events` (with `fromSeq = serverLastSeq + 1`) for local `submitting | submitted_to_spooler | confirmed_completed`.
- `abandon` if the job is missing, terminal, or has a cancel request.
- `resume` otherwise — **including for a job the agent reports as `unknown`**, because `unknown` is not in the
  server's "locally submitted" list.
- Agent rule (enforced by the core regardless of what the server says): `resume` can only continue a job whose
  local state is Accepted. `JobLedger.Submit` refuses every other state.
- Recommended ERP correction: treat local `unknown`/`failed`/`cancelled` like `report_events`, never `resume`.
- The implicit-ack branch promotes `delivered → accepted_by_agent` without checking `authorizationExpiresAt`.
  Harmless for printing (the agent enforces `agentMustNotSubmitAfter` itself) but inconsistent with `/accept`.

## 5. Version negotiation — resolved, with naming inconsistencies

- `SERVER_CONTRACTS = ['0.1']`; negotiation is exact string intersection of `supportedContracts`
  (1–10 strings ≤ 16 chars); no match → 426.
- The documentation's `0.1.0-draft` is not a wire value; sending it would fail negotiation.
- Pair response field is `negotiatedContract`; heartbeat response field is `negotiated`.
- `X-Agent-Contract` / `X-Agent-Version` headers from the draft are not read by the server.

## 6. Unknown-state transitions — discrepancy, core is stricter

Server `AGENT_TRANS`:

```
delivered            → accepted_by_agent | failed | cancelled | unknown
accepted_by_agent    → submitting | failed | cancelled | unknown
submitting           → submitted_to_spooler | failed | unknown
submitted_to_spooler → confirmed_completed | unknown
unknown              → confirmed_completed | failed | submitted_to_spooler
```

| Server allows | Core | Reason |
|---|---|---|
| `unknown → failed` | **forbidden** | "failed" asserts nothing reached the spooler; Unknown is the absence of that knowledge. Operator resolution exists server-side (`resolution`). |
| `unknown → submitted_to_spooler` | allowed only with an observed spooler job reference | otherwise it is a guess |
| `unknown → confirmed_completed` | allowed only with evidence | same as server |
| `submitting → failed` | allowed only for a spooler result of "rejected, nothing written" | everything else is Unknown |
| `accepted_by_agent → unknown` | not produced | a never-submitted job is not unknown |

Other verified server behaviour the agent relies on: evidence levels `spooler_reported_complete |
device_reported_complete` (required for `confirmed_completed`, else 422); events deduplicated by `eventId` and
by `(jobId, seq)`; stale or post-terminal events are stored but not applied; lease expiry re-delivers the same
`jobId` to the same agent only, with `deliveryAttempt + 1` (max 5, then server-side `unknown`);
`/accept` requires `attempt == deliveryAttempts` while the job is `delivered`, and answers `duplicate: true`
for already-progressed states. The server never creates a replacement job on its own; reprint is a new job
with `reprintOf`.

## Unresolved (does not block A1)

1. 429 body shape and whether `Retry-After` is present.
2. `error_code`: the label-agent codes used by the agent routes are registered in `error-catalog.ts` at the
   baseline, so the handler emits them; this stays true only while the catalog and `errors.ts` stay in sync.
3. Exact JSON types on the wire (e.g. numeric `profileVersion`, ISO timestamps with `Z`) — expected from
   source, to be pinned by fixture tests in A2 using `docs/label-agent/fixtures` from the ERP repo.
4. Payload retrieval headers (`X-Payload-Sha256`, `ETag`) are emitted per source; not yet consumed.
