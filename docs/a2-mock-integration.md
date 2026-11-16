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
   waits; when `agentMustNotSubmitAfter` passes it becomes Failed (`AUTHORIZATION_EXPIRED`) without being
   submitted.
3. If `/accept` shows a server state the agent can explain (`unknown` after the server gave up on delivery), a
   never-submitted job is abandoned and reported `failed`. If it shows a *submitted* state for a job the
   journal holds as never submitted, the ledger is quarantined (rule 10).
4. Payload: envelope validated before download; `X-Payload-Sha256` and the envelope hash must both match the
   bytes; one refetch, then the job is refused.
5. A refused job (invalid envelope, payload that never matched) is written to the journal as Failed and
   reported to the ERP with one `failed` event (`delivered → failed` is permitted by the ERP). An envelope
   without a usable identity (empty job id, another agent's id) is only noted locally; nothing is invented.
6. Same job id with different immutable content: reported, the local job is abandoned, nothing is submitted.
7. Events go in `seq` order, ≤ 20 per call, only after the accept is settled. `applied`, `duplicate` and
   `stale` all count as delivered; only events covered by the answer are acknowledged.
8. A 404/409/422 or an incomplete answer **blocks** that job's outbox: a journal record (`outbox_blocked`)
   keeps code, count and time. Nothing is dropped and a 404 never counts as an acknowledgement. Delivery is
   retried after 1 min × 2^(blocks−1), capped at 1 h, across restarts; a real acknowledgement clears the block.
9. Reconcile (at start, after `LEASE_LOST`, and every 10 min) is advisory: `report_events` trims the outbox up
   to `fromSeq − 1`; `resume` grants nothing; `abandon` puts a never-submitted job on hold until `/accept`
   says why — cancel requested → **Cancelled**; lease lost / expired → **Failed**; 404 → Failed and blocked.
10. Quarantine: when the ERP has events or a submission the journal does not know (`fromSeq − 1 > lastSeq`,
    or a submitted state for a locally Accepted job), the journal is presumed older than reality. A durable
    `quarantine` record stops all submissions and claims. Clearing it is an explicit operator action that
    first fails every still-Accepted job. Not every rollback is detectable this way (see durability.md).
11. A job left in Submitting because the spooler's answer could not be recorded becomes Unknown on the next
    cycle of the same process (or at restart); it is never resubmitted.
12. `cancelJobIds` / `cancelRequested` cancel only jobs still Accepted.
13. Unknown is never resubmitted: `ErpSync` only calls `Submit` for Accepted jobs and the ledger refuses
    anything else. A reprint is a different job id.

### Failed versus Cancelled

- **Failed**: it is known that nothing was handed to a spooler (expired, refused, abandoned, spooler rejected
  with nothing written).
- **Cancelled**: the ERP explicitly asked for cancellation while the job was still Accepted.
- Neither is used for a job that may have reached a spooler; that is Unknown.

## Verified against ERP code (commit 83a936a3, agent-facing files identical to 0f48373f)

- Credential generator/regex → `AgentCredential` (tests build a synthetic value at run time; no
  credential-shaped literal is committed).
- Error body → flat, code in `error_code`; all label-agent codes are registered in `error-catalog.ts`, so the
  handler emits them. The nested shape of the contract draft yields no code.
- Unknown transitions and `resume`-for-unknown → reproduced in the mock and asserted.

## Limitations

- The mock is a transcription by reading; it can be wrong where the reading was wrong. See
  `docs/integration-testing.md` for what was run against the real ERP code.
- Rule 2 trades availability for control: while the ERP is unreachable after a claim, accepted jobs are not
  submitted.
- "Submitted once" in tests means one call to the fake spooler. It is not a statement about paper.
- Blocked outboxes are retried forever at most once per hour per job; there is no retention/compaction yet.
- Single-threaded cycle.
