# A2 (approved subset): ERP synchronisation against an in-memory mock

Implemented: claim, durable acceptance, payload validation, event delivery and deduplication, reconciliation,
failure and restart tests. **Not** implemented (not approved): HTTP/TLS client, JSON wire mapping of requests,
pairing, credential storage, heartbeat, polling/backoff, rotation, configuration. The agent still cannot talk
to any ERP.

## Pieces

| File | Role |
|---|---|
| `Erp/IErpClient.cs` | transport-neutral port: `Claim`, `GetPayload`, `Accept`, `PostEvents`, `Reconcile`; `ErpException` (status + `error_code`), `ErpUnavailableException` (no usable answer) |
| `Erp/ErpSync.cs` | one synchronisation cycle driving `JobLedger` |
| `Contracts/ErpWire.cs` | `ErpErrorBody.Parse` (flat error body), `AgentCredential` (shape only, redacting) |
| `JobLedger.Abandon` | Accepted → Failed for a job that was never submitted |
| `tests/.../Fakes/MockErp.cs` | in-memory ERP whose rules are transcribed from `print.service.ts` |

## Cycle

`reconcile (after start or LEASE_LOST) → deliver accepts → deliver events → claim → deliver accepts →
submit ready jobs → deliver events`. An unusable ERP answer skips the remaining network steps of that cycle;
local submission of already confirmed jobs still runs. Storage failures propagate to the caller.

## Rules (each has a test)

1. `/accept` is sent only for jobs whose acceptance record is already flushed.
2. A job is submitted only after the ERP answered `/accept` with state `accepted_by_agent`. Until then it
   waits; when `agentMustNotSubmitAfter` passes it becomes Failed (`AUTHORIZATION_EXPIRED`) without printing.
3. If `/accept` shows any other server state (the ERP answers `duplicate` with e.g. `unknown` after giving up
   on delivery), a never-submitted job is abandoned and reported `failed` — the one case where the ERP's
   `unknown → failed` is truthful, because the agent knows nothing was sent.
4. Payload: envelope validated before download; `X-Payload-Sha256` and the envelope hash must both match the
   bytes; one refetch, then the job is left unaccepted (no local record, no ack).
5. Same job id with different immutable content: reported, the local job is abandoned, nothing is printed.
6. Events go in `seq` order, ≤ 20 per call, only after the accept is settled. `applied`, `duplicate` and
   `stale` all count as delivered; only events covered by the answer are acknowledged. 409/422 blocks that
   job's stream until the next reconcile and is reported; 404 clears the outbox for that job (journal keeps
   the history).
7. Reconcile is advisory: `report_events` trims the outbox up to `fromSeq - 1`; `abandon` stops a
   never-submitted job; `resume` grants nothing.
8. `cancelJobIds` / `cancelRequested` cancel only jobs still Accepted.
9. Unknown is never resubmitted: `ErpSync` only calls `Submit` for Accepted jobs and the ledger refuses
   anything else. A reprint is a different job id.

## Verified against ERP code (commit 83a936a3, agent-facing files identical to 0f48373f)

- Credential generator/regex → `AgentCredential` (tests build a synthetic value at run time; no
  credential-shaped literal is committed).
- Error body → flat, code in `error_code`; all label-agent codes are registered in `error-catalog.ts`, so the
  handler emits them. The nested shape of the contract draft yields no code.
- Unknown transitions and `resume`-for-unknown → reproduced in the mock and asserted.

## Limitations

- The mock is a transcription by reading; it can be wrong where the reading was wrong, and it has no HTTP,
  JSON, auth, rate limits, RLS or database semantics. Nothing here was run against a real ERP.
- Rule 2 trades availability for control: while the ERP is unreachable after a claim, accepted jobs do not
  print. This is an agent-side choice and can be revisited.
- A locally invalid envelope or persistently corrupt payload is only reported locally; the ERP is not told and
  will mark the job `unknown` on expiry (no durable way to report a job the agent never accepted).
- An `abandon` caused by a user cancel request is reported as `failed` (`ERP_ABANDON`), not `cancelled`: the
  directive does not say why.
- Blocked event streams are retried only after a reconcile, which currently happens on restart or LEASE_LOST.
- Cancel requests arrive only with claim/accept answers (no heartbeat yet).
- 429 handling, backoff and jitter are absent. Journal compaction is still absent.
- Single-threaded cycle; no long-poll.
