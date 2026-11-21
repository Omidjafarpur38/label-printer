# Automatic update notice, installed only with the user's approval

## What the user sees
- The program checks for a newer version about 45 seconds after it starts and then every ~6 hours.
- When one exists, a banner appears at the top of the control window: "نسخه‌ی جدید X آماده است" with **Install and
  update** and **Later**. Tab 3 has an "Update" card with the last check and a "Check now" button.
- **Nothing is downloaded or installed until the user presses the button and confirms.** After confirming, the
  program downloads the installer, verifies it, starts it silently, and the installer stops the agent the same
  graceful way every upgrade does (so no submission is cut), replaces the files and opens the window again.
  Settings, pairing, printer choice and the job journal are kept. The autostart choice is kept.

## Why this is safe without an Authenticode signature
A downloaded program that updates itself is a remote-code-execution path if it trusts the network. So the update
channel has its own signature, independent of Windows' publisher signature:

1. `agent-update.json` (version, installer file name, size, SHA-256, issue time, short notes) is signed with an
   **ECDSA P-256 / SHA-256** key. The matching **public key is compiled into the program**
   (`src/LabelPrinter.Agent.Core/Updates/TrustedKeys.cs`). The private key exists only as the repository secret
   `UPDATE_SIGNING_KEY` and in the owner's offline backup.
2. The program checks the signature **before it even parses** the manifest. Then it applies strict rules: schema 1,
   channel `experimental`, the file name must be exactly the installer of that version, a lowercase 64-hex hash,
   a size between 1 byte and 200 MB.
3. Only a **strictly newer** version is offered: no downgrade and no replay of an old signed manifest.
4. The installer is accepted only if its size and SHA-256 equal the signed values (checked while streaming; any
   extra byte aborts). It is stored in `%LOCALAPPDATA%\LabelPrinterAgent\update\` and started only after that.
5. Transport: HTTPS only (loopback HTTP only in tests), **redirects are not followed**, no cookies, size caps,
   timeouts. The update address is the same built-in server as everything else and is never shown.
6. With **no key built in, the feature is off** and the Update card says so. It can never accept an unsigned or
   unknown-key manifest. Two key slots allow rotation (ship a release that trusts both, then retire the old one).

A stolen web server or a compromised download link therefore cannot push code to the PCs; only whoever holds the
private key can. Keep that key out of the repository, out of chat and out of logs.

## One-time key creation (owner)
Needs a PC with the .NET 10 SDK.

1. In a clone of this repository:
   `dotnet run tools/update/sign-update.cs -- keygen --private-out update-private.key`
   The private key goes to the file only; the **public** key is printed.
2. Put the public key in `TrustedKeys.SpkiBase64` (one string in the array) and commit it.
3. In GitHub: Settings > Secrets and variables > Actions > New repository secret, name `UPDATE_SIGNING_KEY`, value =
   the full content of `update-private.key`. Then store a backup of the file offline (password manager or an
   encrypted drive) and delete the working copy.
4. Release as usual. The release workflow signs the manifest, verifies it against the key built into that very
   build, and attaches `agent-update.json` and `agent-update.sig` to the release. If the secret is missing the
   release simply has no update manifest; if the secret and the public key do not match the release fails.

Lost private key: generate a new one, ship a release (installed by hand) that trusts it, and from then on updates
work again. Leaked private key: treat as an incident, rotate as above, and stop publishing the manifest until a
release that no longer trusts the old key has been installed everywhere.

## Publishing to the server
The ERP's isolated publish workflow copies `agent-update.json`, `agent-update.sig` and the installer into the
server's `downloads/` folder, next to `installers.json` (see the ERP repository's
`docs/label-agent/experimental-download.md`). Installed copies read `/downloads/agent-update.json` and
`/downloads/agent-update.sig` from the built-in server.

## Limits
- The first version that contains this feature must be installed by hand once; older versions cannot update.
- Not tested with a real installer on a real desktop: the flow is tested over loopback HTTP with a fake installer
  launcher (check, confirmation, hash/size/signature/redirect/downgrade refusals), and the installer's
  `/UPDATE=1` re-open step is exercised only by the Windows CI build compiling it.
- Per-user install only; the installer never asks for administrator rights.
- No staged rollout, no automatic rollback to the previous version (the previous installer stays on the Releases page).
