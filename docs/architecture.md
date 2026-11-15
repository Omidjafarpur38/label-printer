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

There is deliberately no plugin system, DI container or async pipeline. A2 adds an HTTP client that drives
`JobLedger`; a later milestone adds a Windows `ISpooler`.

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
