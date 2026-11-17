# Label Printer Agent — setup guide (experimental, unsigned)

This build is for testing. It is not signed and has not been verified with a physical printer unless the
release notes of your exact version say so.

## What it is

A per-user Windows program that fetches print jobs from one ERP over HTTPS and hands the ERP's printer
commands, unchanged, to one Windows print queue that you select. Rendering happens in the ERP.

| Item | Value |
|---|---|
| Windows | x64. Built on .NET 10, which Microsoft supports on Windows 11 and the Windows 10 LTSC/Enterprise editions it lists; installer minimum is build 17763 (1809). Other Windows 10 editions may run it but are outside Microsoft's support. |
| Runtime | Self-contained: the .NET runtime ships inside the installation folder. Nothing else to install. |
| Rights | No administrator rights. Installs to `%LOCALAPPDATA%\Programs\LabelPrinterAgent`. |
| Execution model | A normal process of the logged-in user, started at logon if you chose autostart. No Windows Service. It runs while that user is logged in. |
| Data | `%LOCALAPPDATA%\LabelPrinterAgent` — journal, payloads awaiting submission, credential (DPAPI, current user), settings, log. |
| Autostart | An installer choice (on by default, visible, changeable in the control window). Starts without a window. |

## Before you install

1. Compare the installer's SHA-256 with the published value:
   `Get-FileHash .\LabelPrinterAgent-<version>-win-x64-unsigned-setup.exe -Algorithm SHA256`
2. Windows shows "Unknown publisher"; SmartScreen may show "Windows protected your PC" → "More info" →
   "Run anyway". This is expected for an unsigned file. Do **not** disable Defender or SmartScreen.

## Install

Run the installer and finish the wizard. It asks two things, both optional:

- **Start automatically when Windows starts** (checked): the agent then runs in the background without a window.
- **Desktop icon**.

At the end, "Open Label Printer Agent now" opens the control window. The installer does not pair, select a
printer or print; the program stays idle until you do both.

Silent: `LabelPrinterAgent-<version>-win-x64-unsigned-setup.exe /VERYSILENT /SUPPRESSMSGBOXES`
(add `/TASKS="!autostart"` to skip autostart).

## The control window

Open **Start menu → Label Printer Agent (experimental) → Label Printer Agent** (or the desktop icon). A window
opens with its own taskbar entry. It is a local web page shown by Microsoft Edge in app mode (Chrome, then the
default browser, if Edge is missing). Closing the window does not stop the agent; it keeps working in the
background. **Quit completely** is the button at the bottom of the window.

Three steps, in order:

1. **Connect to the server.** Enter the ERP address (`https://…`), the pairing code (created in the ERP's label
   printing section; valid 10 minutes, works once) and a name for this computer.
2. **Printer.** Choose the print queue, the printer language (TSPL or ZPL), the resolution and the label size.
   The agent never uses the Windows default printer. Language and resolution are *your declaration*; the agent
   cannot check them. A wrong choice wastes labels.
3. **Control.**
   - **Stop sending / Continue sending** pauses the agent: no new jobs are taken or printed (for example while
     you change the paper). Jobs already taken wait and continue with "Continue".
   - **Start with Windows** switches autostart on or off.

The **job list** shows each job's state in plain words and offers only safe actions:

| State | Meaning | Actions |
|---|---|---|
| Waiting to be sent | taken, nothing sent to the printer yet | **Cancel** — nothing will be printed |
| Went to the Windows queue | Windows has the job; this does *not* prove a label came out | **Delete from the Windows queue** — the job becomes *unknown* |
| Printed (Windows confirmed) | Windows reported the job printed | — |
| Failed — nothing printed | known failure before anything was sent | — |
| Cancelled | | — |
| Outcome unknown | a send was started and its result could not be determined | none. It is **never sent again automatically**. Check the label, mark it in the ERP, and reprint from the ERP if needed (that creates a new job). |

There is deliberately no "retry" or "send again" button.

If the window shows **Quarantine**, the ERP knows of events this computer has no record of (for example an old
backup of the journal was restored). Nothing is printed until you read `recovery.md` and clear it.

## First test print

Use the ERP's test print: **one job, one copy**, with labels you can afford to waste. Look at the label. "Went to
the Windows queue" means Windows took the job; it does not mean a label was printed.

## Command line (optional, for support)

Everything above is also available in a Command Prompt in the installation folder:

```
LabelPrinterAgent.exe status
LabelPrinterAgent.exe printers
LabelPrinterAgent.exe select-printer --queue "NAME" --language tspl --dpi 203 --width-mm 50 --height-mm 30
LabelPrinterAgent.exe pair --origin https://your-erp.example
LabelPrinterAgent.exe run            (foreground, no window; Ctrl+C stops)
LabelPrinterAgent.exe gui            (agent + control window, same as the Start-menu icon)
LabelPrinterAgent.exe stop --wait 30
LabelPrinterAgent.exe autostart enable|disable|status
LabelPrinterAgent.exe selftest
```

`run` refuses to start if the agent is not paired, no printer is selected, or the queue does not exist. Only one
instance can run at a time (a second one exits, or just opens the window).

## Diagnostics

- The window's banner and status pill say what is wrong in plain words; "Event log" at the bottom shows details.
- Log file: `%LOCALAPPDATA%\LabelPrinterAgent\agent.log` (codes and ids only; no credential, no label content).
- `LabelPrinterAgent.exe spool-file --queue "NAME" --file sample.prn --yes` sends one file once to a queue.

| Symptom | Meaning |
|---|---|
| "Needs a connection" | pair in step 1 |
| "Needs a printer" / "printer not found" | choose the printer in step 2 (the queue may have been renamed or removed) |
| "Access refused" | the agent was revoked in the ERP or its credential expired; disconnect and connect again |
| "Another instance is running" | close the other one (Quit completely), or use the existing window |
| a job stays "unknown" | see the table above; it is never retried |
| "Quarantine" / "journal problem" | see `recovery.md` |
| the window does not open | start `LabelPrinterAgentBg.exe` again; it reopens the window of the running agent |

## Upgrade

Run the newer installer. It asks a running agent to stop (waiting for its current server call), replaces program
files and leaves the data directory untouched. Your autostart choice is kept: an upgrade never switches
autostart back on. Open the window again afterwards.

## Remove

Uninstall from "Installed apps". The uninstaller stops the agent, removes the autostart entry and the
program files. It asks whether to delete the data directory; the default is to keep it. A silent uninstall
always keeps it. Revoke the agent in the ERP as well.

## Limits of this build

- Unsigned; no automatic updates.
- One ERP, one printer.
- No detection of printer language, DPI or status; no check of the printer commands inside a job.
- Runs only while the user is logged in.
- Completion is reported only if the Windows spooler itself marks the job as printed; many drivers never do.
- Never tested against a production ERP.
