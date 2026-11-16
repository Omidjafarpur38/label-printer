# Durability

## Persistence boundary

One append-only journal file is the single source of truth for: acceptance, identity and content hash, every
state transition, and the outbox (accept acknowledgements and status events owed to the ERP). Memory is rebuilt
from it on start and is only ever updated *after* a record has been written and flushed, through the same
`Apply` routine that replays the journal. There is no second store that could disagree with it.

Payload bytes live outside the journal, one file per job id (`payloads/<jobId>.bin`), and are subordinate to it:

- written (temp file → flush → rename) **before** the acceptance record;
- meaningful only while the journal says the job is Accepted;
- verified against the envelope's SHA-256 every time they are read for submission;
- a payload file with no Accepted job is deleted on start.

A crash between the two writes leaves an orphan payload and no job — the acceptance was never acknowledged, so
the ERP redelivers.

## Record format (version 1)

```
"LPJ1" | u32 LE payload length | 4-byte header check | payload (UTF-8 JSON) | 32-byte record hash
header check = SHA-256("LPJ1" | length | previous record hash)[0..4]
record hash  = SHA-256(previous record hash | payload)          (first record chains from 32 zero bytes)
```

- JSON fields: `V` (format version, 1), `N` (record number from 0, contiguous), `T` (type), `At`, then
  type-specific fields. Types: `header` (agent id), `accepted` (full envelope + content hash), `redelivered`,
  `transition` (from, to, seq, event id, error/spooler/evidence), `accept_acked`, `events_acked`.
- Maximum payload 1 MiB. A record with another `V`, an unknown type, a non-contiguous `N`, or a transition the
  state machine forbids makes the journal unreadable (refused, see below).
- The magic's trailing `1` versions the framing; `V` versions the content. A future incompatible change must
  bump one of them and ship an explicit migration; this version refuses what it does not understand.

## Writing

- **Serialization:** every mutation runs under the ledger lock: encode → append → flush → apply to memory.
- **Single writer:** the journal is opened with `FileShare.None` and held for the process lifetime. On Windows
  this is enforced by the OS. On Unix .NET uses an advisory lock that only cooperating openers respect; that is
  sufficient for tests, not a security boundary.
- **Flush:** `FileStream.Flush(flushToDisk: true)` (`FlushFileBuffers` / `fsync`) after every record. One
  flush per state change is the intended cost.

## Recovery on open

| Finding | Action |
|---|---|
| Final record cut short (header, body or hash incomplete) | truncate to the last complete record, flush, report byte count in `RecoveryReport.TornBytesDiscarded` |
| Tail consisting only of zero bytes | same (space allocated, data never written) |
| Any complete record failing header check, hash, JSON, version, numbering or state-machine replay — **including the last one** | `JournalCorruptException` with the byte offset; the ledger does not open |
| Journal of another agent id | `JournalMismatchException` |
| Job in Submitting | journaled transition to Unknown (`INTERRUPTED_SUBMISSION`), listed in `MarkedUnknown` |
| Job in Accepted | untouched, listed in `Resumable` |

Nothing is skipped. A corrupt journal needs an operator: without the full history the agent cannot know which
jobs were submitted, and guessing risks duplicate labels. Until repaired the agent must not claim or print.
Repair tooling does not exist yet.

A full-length final record with a bad hash is treated as corruption rather than a torn write. This is
deliberately conservative: on a filesystem that can expose garbage in a partially written tail after power
loss, the agent will stop and ask instead of dropping a record that might have been a submission intent.

## Storage failures

| Failure | Behaviour |
|---|---|
| Payload write fails (disk full, access denied) | exception; no journal write; job unknown to the ledger |
| Journal append fails | partial bytes are truncated away; `JournalWriteException`; the operation did not happen; ledger stays usable |
| …and that truncation also fails | additionally the ledger is **faulted** |
| Journal flush fails | `JournalWriteException`; ledger **faulted** (after a failed flush the file state is unknowable, a retry proves nothing) |
| Faulted ledger | every mutating call throws `LedgerFaultedException`; reopen to recover from what is actually on disk |
| Failure while persisting Submitting | spooler not called; job stays Accepted |
| Failure while recording the spooler's answer | exception; job stays Submitting in memory (not submittable) and becomes Unknown at next start |
| Payload delete fails | ignored; start-up cleanup retries; counted in `BlobCleanupFailures` |

## What is guaranteed, and under which assumptions

Given that (a) a successful flush means the bytes survive power loss, (b) the storage returns what was written
or an error, and (c) only this process writes the directory:

- an acceptance reported to the caller survives a crash;
- the spooler is never invoked for a job without a durable Submitting record;
- a job is handed to the spooler at most once across any number of crashes and restarts;
- a state change and its outbound event are recorded atomically;
- accidental modification, removal or reordering of *earlier* records is detected.

## What is not guaranteed

- **Lying storage.** Consumer SSDs, USB bridges and some virtual disks acknowledge flushes they have not
  performed; a volatile write cache without power-loss protection can lose flushed records. If a flushed
  Submitting record is lost this way, the job reappears as Accepted and could be submitted twice. No software
  layer above the device can close this gap.
- **Loss of whole trailing records.** If the file is cut exactly at a record boundary (lost writes as above,
  or restoring an old copy), the result is a valid shorter journal and cannot be detected locally. Restoring a
  journal from backup is unsafe for the same reason.
- **Directory entries.** .NET has no portable directory fsync. Creation of the journal file and the rename of
  a payload file may not be durable at the instant of a crash on some filesystems. Effect: a missing journal
  right after first creation (nothing accepted yet), or a missing payload for an Accepted job (the job then
  fails with `LOCAL_PAYLOAD_MISSING` without printing).
- **Tampering.** The hash chain is unkeyed. Anyone who can write the file can produce a valid journal.
- **At-least-once printing.** The design chooses at-most-once submission; a crash around the spooler call
  yields Unknown, which a human resolves, rather than a retry.
- **The spooler's own durability and the physical outcome** are outside the journal entirely.
- **Disk exhaustion** stops acceptance and submission (fail closed); there is no reserved space.
- Real disk-full, access-denied and power-loss behaviour was simulated by fault injection, not provoked on
  real hardware or on Windows/NTFS.

## Retention and compaction (not implemented)

The journal only grows: roughly 0.7 KB per acceptance and 0.3 KB per transition, read fully into memory on
open. Before production use it needs compaction:

- write a new journal containing a header, a snapshot record per retained job and the open outbox, to a temp
  file; flush; atomically replace the old file; keep the single-writer handle semantics;
- a job is droppable only when it is terminal, its outbox is empty, **and** its authorization window has
  passed (the ERP can redeliver until `authorizationExpiresAt`, currently 1 h, at most 24 h by agent bound) —
  earlier removal would let a redelivered job be accepted and submitted again;
- Unknown jobs are retained until resolved by an operator;
- a size or age threshold triggers it; sealed old journals are retained for a configurable audit period.
