# Recovery

The journal (`%LOCALAPPDATA%\LabelPrinterAgent\journal.lpj`) is the agent's only record of which jobs it
handed to the Windows spooler. The rules below exist to avoid printing labels twice.

## Never do this

- Do not copy an older journal back over a newer one (backup restore, VM snapshot revert, disk image).
- Do not move a journal between computers, Windows accounts or ERP installations.
- Do not edit or truncate the journal.

A restored journal can show a job as "accepted, never submitted" although it was printed later. The agent
cannot always notice: it can only notice when the ERP holds newer facts about that job than the journal.

## "QUARANTINED"

The ERP knows of events or a submission that this journal does not contain, so the journal is probably older
than reality. While quarantined the agent submits nothing and claims nothing; it still delivers reports.

1. Stop the agent. Run `LabelPrinterAgent.exe status` and note the listed jobs.
2. Find out physically which labels exist.
3. Run `LabelPrinterAgent.exe recover --clear-quarantine` to see which accepted jobs would be given up, then
   again with `--yes`. Those jobs are marked failed **without being printed**.
4. Reprint what is really missing from the ERP (each reprint is a new job).

## "journal is corrupt"

A record failed verification. The agent refuses to run because it can no longer know what was submitted.

1. Keep the file. Do not delete it yet; note the byte offset from the message.
2. In the ERP, look at the recent jobs of this agent and at the labels that exist.
3. To start over: stop the agent, **rename** the data directory, pair again. Jobs that were in progress stay
   in the ERP as they were (typically `unknown` or `submitted_to_spooler`) and are not redelivered. Resolve
   them in the ERP by hand.

A journal whose last record was cut off by a crash is repaired automatically and reported in the log; that is
not corruption.

## "journal belongs to a different agent identity / ERP origin"

The data directory was created for another pairing. Rename the data directory before pairing with a
different ERP. The agent will not reuse a journal across ERP installations.

## Unknown jobs

A job is `unknown` when a submission was started and its result could not be determined (crash, power loss,
spooler error after bytes were written, disk failure). It is never sent again. Check the label; in the ERP
mark it as printed or not printed; reprint from the ERP if needed.

## Blocked reports

`status` lists jobs whose reports the ERP refused (for example 404). They are kept and retried with growing
delays, at most hourly. A persistent `JOB_NOT_FOUND` for many jobs usually means the agent is pointed at the
wrong ERP.
