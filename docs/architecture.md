# Architecture (A1: portable core)

## Components

```
src/LabelPrinter.Agent.Core            net10.0, no packages, no OS-specific code
  Contracts/Contract.cs                envelope, output profiles, limits, status attribute, ERP baseline commit
  Contracts/EnvelopeValidator.cs       structural + integrity checks
  Jobs/JobModel.cs                     JobKey, JobState, state machine, content hash, events
  Journal/JournalCodec.cs              record framing, hash chain, decode with tail recovery
  Journal/Storage.cs                   IJournalStore / IBlobStore + file implementations
  Spooling/ISpooler.cs                 the only path to a printing side effect
  JobLedger.cs                         the core: accept, submit, recover, outbox
tests/LabelPrinter.Agent.Core.Tests    in-repo runner, fake spooler, fault-injecting stores
```

```
src/LabelPrinter.Agent.Core (continued)
  Erp/IErpClient.cs, ErpSync.cs        sync cycle (docs/a2-mock-integration.md)
  Erp/HttpErpClient.cs                 HTTPS transport for /api/label-agent/v1
  Runtime/IdentityStore.cs             pairing facts + protected credential, pending rotation
  Runtime/AgentRunner.cs               the loop: heartbeat, rotation, backoff, stop conditions
src/LabelPrinter.Agent                 console front end (LabelPrinterAgent.exe) + host
  Windows/Native.cs                    DPAPI, file ACL, HKCU autostart
  Windows/RawSpooler.cs                winspool RAW document, queue listing, job status
src/LabelPrinter.Agent.Background      same sources built windowless (LabelPrinterAgentBg.exe): loop only
installer/LabelPrinterAgent.iss        per-user Inno Setup installer
```

There is deliberately no plugin system, DI container or async pipeline.

## Transport

- Origin must be `https://host[:port]`; plain http is accepted for loopback only (local test ERP). No path,
  query or credentials in the origin. Certificate validation is the platform default and cannot be disabled.
- Redirects are not followed and cookies are not used, so the credential cannot leave the origin.
- The credential exists in memory and in the DPAPI-protected file only; it is sent in `Authorization` and
  appears in no URL, argument, log line or exception message.
- Every request has a timeout; answers are size-bounded (JSON 1 MiB, payload 8 MiB).
- The transport never retries. 4xx becomes `ErpException` (status + `error_code`); 5xx, 3xx, timeouts, dropped
  connections and unusable answers become `ErpUnavailableException`, which means "the ERP may or may not have
  processed this". Retrying is the sync cycle's job and is safe there because: accept is idempotent on the
  server, events are deduplicated by `eventId`/`seq`, reconcile is a read with an idempotent implicit ack,
  a lost claim answer is repaired by redelivery of the same job, printer report is a replace. **Pairing is
  not retried**: a pairing code is consumed by the first attempt.
- An envelope with a missing or mistyped field makes the whole answer unusable; no identity is guessed.
  Unknown fields are ignored.
- Heartbeat is sent every `heartbeatSec` (from pairing, clamped 10–300 s) and carries cancel requests back.
  There is no lease-renewal endpoint in the ERP and none is invented: `/accept` extends the lease to the
  job's authorization expiry on the server.

## Runner

One iteration: heartbeat if due → rotate credential if older than 60 days → one sync cycle → choose a delay.

| Outcome | Next step |
|---|---|
| work done | 0.2 s |
| idle | 1 s (the claim itself long-polls up to 15 s) |
| ERP paused (feature flag off for the tenant) | 30 s |
| no usable answer | exponential backoff with full jitter, 2 s base, 5 min cap, never under 0.5 s |
| 429 | `Retry-After`, bounded to 5 s – 15 min |
| 403 | 5 min |
| 401 | stop (`CredentialRejected`); if a rotated credential was pending, drop it and fall back once |
| 426 | stop (`ContractUnsupported`) |
| storage failure | stop the process; the next start recovers from disk |

Rotation: `POST /credential/rotate` → store as pending → present it on the next call → the first success
promotes it (the ERP commits a rotated credential on first use and revokes the previous one then).

## Windows host

- **Execution model:** an ordinary process of the logged-in user. No Windows Service: a service would need
  administrator installation, would run as another identity (other DPAPI scope, other printer visibility)
  and is not needed to test. Consequence: the agent runs only while that user is logged in.
- **Autostart** is an HKCU `Run` value pointing at the windowless build, written only by
  `autostart enable`. The installer writes none.
- **Stopping:** a named event per data directory; `stop --wait N` signals it and waits for the journal lock
  to be released. The current ERP call (≤ 15 s long-poll) finishes first.
- **Single owner:** the journal file lock. A second instance exits with code 6.
- **Start conditions:** paired, a printer selected explicitly, and that queue exists. Otherwise the process
  exits without contacting the ERP for jobs. The default printer is never looked up.
- **Admission:** a job is refused before download unless its ERP printer id, language and DPI equal the
  operator's selection; the refusal is journaled and reported as `failed`.
- **RAW spooler:** `OpenPrinter` → `StartDocPrinter("RAW")` → `StartPagePrinter` → `WritePrinter` in 64 KiB
  chunks, continuing short writes → `EndPagePrinter` → `EndDocPrinter`. Failure before any byte was written
  (and a successful abort) is "rejected, nothing written"; any failure after the first write is
  indeterminate → Unknown. Handles are closed on every path.
- **Completion evidence:** only if the spooler flags the job PRINTED/COMPLETE while the agent is watching.
  A job that just leaves the queue stays `submitted_to_spooler`. A job seen as deleted becomes Unknown.
  Watching is in-memory and ends with the process.
- **Credential:** DPAPI, CurrentUser scope, fixed application entropy, ACL restricted to the user, SYSTEM
  and Administrators before content is written. This does not protect against software running as the
  same user, or against an administrator.
- **Changing ERP:** the journal records its origin and agent id. `pair` refuses another origin while a
  pairing exists; the ledger refuses a journal of another origin or agent.

## Job identity

`JobKey = (agentId, jobId)`. The ERP never sends a tenant id to the agent; the agent id is issued per tenant
and install, so the key is tenant-scoped by construction. One journal belongs to one agent id (written in its
first record); opening it under another id is refused, and an envelope naming another agent is rejected.

The **content hash** is SHA-256 over a canonical encoding of every immutable envelope field. `deliveryAttempt`
and `leaseUntil` are excluded because the ERP changes them when it redelivers the same job. It is unkeyed: it
detects divergence, it does not authenticate.

## States

| Local state | Meaning | Wire name |
|---|---|---|
| Accepted | acceptance record is durable; proven never submitted | `accepted_by_agent` |
| Submitting | submission intent is durable; spooler may or may not have run | `submitting` |
| SubmittedToSpooler | spooler acknowledged; **not** physical completion | `submitted_to_spooler` |
| ConfirmedCompleted | completion recorded with evidence | `confirmed_completed` |
| Failed | known failure with nothing written to a spooler | `failed` |
| Cancelled | cancelled before any submission intent | `cancelled` |
| Unknown | physical outcome cannot be determined | `unknown` |

```
Accepted ──► Submitting ──► SubmittedToSpooler ──► ConfirmedCompleted (evidence)
   │            │  │               │
   │            │  └► Unknown ◄────┘
   │            └► Failed (spooler: rejected, nothing written)
   ├► Failed (expired, local payload missing/altered)
   └► Cancelled
Unknown ──► ConfirmedCompleted (evidence) | SubmittedToSpooler (observed spooler reference)
```

Rules, each covered by a test:

1. Acceptance may be acknowledged to the ERP only after `Accept` returned `AcceptedNew`/`AlreadyKnown`,
   i.e. after the record was flushed.
2. The Submitting record is flushed before `ISpooler.Submit` is called. If that write fails, the spooler is
   not called and the job stays Accepted.
3. A job found in Submitting at start becomes Unknown.
4. Unknown has no path to Submitting. `Submit` acts only on Accepted.
5. Accepted jobs survive restart with their payload and may be submitted.
6. A reprint is a different `jobId` carrying `reprintOf`; it is an ordinary new job to the core.
7. Lease expiry / redelivery of a known job returns its current state and changes nothing except the delivery
   attempt owed in the accept acknowledgement.
8. `SpoolOutcome.Accepted` leads to SubmittedToSpooler, never further. ConfirmedCompleted requires an
   `Evidence` with a recognised level.
9. Failed is used only where it is known that no byte reached a spooler. The core cannot check a spooler's
   honesty about "nothing written"; a real spooler adapter must use that outcome conservatively.

Compatibility gaps with the ERP's transition table are listed in `erp-baseline.md` §6.

## Idempotent acceptance

| Incoming | Result |
|---|---|
| unknown job, valid envelope + matching payload, not expired | payload stored, acceptance record flushed → `AcceptedNew` |
| known job, same content hash | `AlreadyKnown` + current state; no write (one small record if the delivery attempt advanced and the accept is still owed) |
| known job, different content hash | `Conflict`; nothing changes |
| invalid | `Rejected` with field-level codes; nothing stored |

All ledger operations take one lock, including the spooler call. Concurrent duplicates therefore produce one
acceptance and one submission; the cost is that submissions are serialized, which is acceptable for a label
printer and can be revisited per printer later.

## Outbox

The journal also records what is still owed to the ERP: the accept acknowledgement (with the current delivery
attempt) and one status event per transition (`eventId`, per-job `seq` from 1, state, time, error code, spooler
reference, evidence). Events are created in the same record as the transition, so a state change and its
event cannot be separated by a crash. `AcknowledgeAccept` / `AcknowledgeEvents` remove them durably. Event ids
and sequence numbers are stable across restarts, which is what lets the ERP deduplicate re-sent events.

## Payload and copy safety

- The agent submits the approved bytes once. `SpoolRequest` has no copy count; the ERP already encodes copies
  as pages inside the payload, so multiplying anywhere would over-print.
- Checked at acceptance: supported contract version, declared output profile and its language, content type,
  size (1 B – 8 MiB), SHA-256 format, payload length and hash, numeric bounds, time window, agent id.
  Re-checked at submission: authorization window and payload hash of the locally stored bytes.
- These checks establish that the bytes are the ones the envelope describes. They do **not** establish who
  produced the envelope; that depends on TLS and the agent credential (A2).
- Language, render mode, transport and verification status are separate types. `tspl-bitmap-v1` and
  `zpl-bitmap-v1` are the initial profiles, both `Unverified`; bitmap-only reflects the ERP's current encoder,
  not a limit of the design.

### Payload command validation (future, not in A1)

A1 does not look inside the payload. The ERP handoff asks the agent to reject configuration/reset/firmware
commands; that needs a real constrained parser per profile, designed separately:

- a tokenizer for the exact byte grammar each profile's ERP encoder emits (TSPL: text commands with a
  length-delimited binary `BITMAP` body; ZPL: `^`/`~` commands with compressed `^GF` data);
- an **allow-list** of commands and argument ranges, rejecting everything else, with binary bodies skipped by
  declared length rather than scanned;
- cross-checks against the envelope (page count, label size, copies);
- a corpus of ERP-generated fixtures plus negative cases.

Searching the payload for forbidden substrings is not that: bitmap data can contain any byte sequence (false
positives) and command syntax has many spellings (false negatives). No such filter is included.

## Sensitive data

- Journal: ids, hashes, timestamps, states, short error codes, spooler references. No payload bytes, no
  credentials. Free text is clipped and stripped of control characters before it is written.
- Payload files: only for Accepted jobs; removed after the single submission attempt, on failure/cancel, and by
  start-up cleanup (including files left by a crash before the acceptance record).
- No logging exists yet. File permissions/ACLs are a deployment concern for the Windows service milestone.
