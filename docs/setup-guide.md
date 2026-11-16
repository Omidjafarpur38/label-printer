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
| Execution model | A normal process of the logged-in user. No Windows Service. It runs while that user is logged in. |
| Data | `%LOCALAPPDATA%\LabelPrinterAgent` — journal, payloads awaiting submission, credential (DPAPI, current user), settings, log. |
| Autostart | Off. You may enable it per user; see below. |

## Before you install

1. Compare the installer's SHA-256 with the published value:
   `Get-FileHash .\LabelPrinterAgent-<version>-win-x64-unsigned-setup.exe -Algorithm SHA256`
2. Windows shows "Unknown publisher"; SmartScreen may show "Windows protected your PC" → "More info" →
   "Run anyway". This is expected for an unsigned file. Do **not** disable Defender or SmartScreen.

## Install

Run the installer. It copies files and adds two Start-menu shortcuts. It does not pair, start, register
autostart or print.

Silent: `LabelPrinterAgent-<version>-win-x64-unsigned-setup.exe /VERYSILENT /SUPPRESSMSGBOXES`

## Configure (in a Command Prompt in the installation folder)

```
LabelPrinterAgent.exe selftest
LabelPrinterAgent.exe printers
LabelPrinterAgent.exe select-printer --queue "EXACT QUEUE NAME" --language tspl --dpi 203 --width-mm 50 --height-mm 30
```

- The agent never uses the default printer. It uses only the queue named here.
- Language (`tspl` or `zpl`), DPI (203, 300 or 600) and label size are **your declaration**. The agent cannot
  detect them. A wrong language sends commands the printer does not understand.
- The queue must be able to pass RAW data to the printer (the vendor driver or "Generic / Text Only").

## Pair with a test ERP

1. In the ERP (label printing must be enabled for your tenant), create a pairing code. It is valid for
   10 minutes and works once.
2. Run and type the code when asked (it is not shown and is not a command-line argument):

```
LabelPrinterAgent.exe pair --origin https://your-test-erp.example --name "Test PC"
```

Only `https://` origins are accepted. The credential returned by the ERP is stored encrypted for your
Windows account and is never printed or logged.

## Run

```
LabelPrinterAgent.exe run          (foreground; Ctrl+C stops it)
LabelPrinterAgent.exe status
LabelPrinterAgent.exe stop --wait 30
```

`run` refuses to start if the agent is not paired, no printer is selected, or the selected queue does not
exist. At start it registers the selected printer with the ERP; create a printer profile for it in the ERP
and send a test print from there.

Optional background start at logon, for this user only:

```
LabelPrinterAgent.exe autostart enable
LabelPrinterAgent.exe autostart disable
```

## First test print

Use the ERP's test print, **one job, one copy**, with labels you can afford to waste. Check the label by
eye. "Submitted to spooler" in the ERP means Windows took the job; it does not mean a label was printed.

## Diagnostics

- `LabelPrinterAgent.exe status` — pairing, printer, job counts, blocked reports, unknown jobs, quarantine.
- Log: `%LOCALAPPDATA%\LabelPrinterAgent\agent.log` (codes and ids only; no credential, no label content).
- `LabelPrinterAgent.exe spool-file --queue "NAME" --file sample.prn --yes` sends one file once to a queue.

| Symptom | Meaning |
|---|---|
| exit 3 / "not paired" | run `pair` |
| exit 4 / "no printer selected" or queue missing | run `select-printer` |
| "credential refused by ERP" | the agent was revoked or its credential expired; `unpair`, then `pair` again |
| job stays "unknown" | the outcome of a submission could not be determined; it is not retried. Inspect the labels, resolve it in the ERP, and reprint from the ERP if needed (that creates a new job). |
| "QUARANTINED" | see `recovery.md` |
| "journal is corrupt" | see `recovery.md`; the agent will not run until this is handled |

## Upgrade

Run the newer installer. It asks a running agent to stop, replaces program files and leaves the data
directory untouched. Start the agent again afterwards (or log off and on if autostart is enabled).

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
