# Security of the control window, and why the installer is unsigned

## What the control window never shows
- **The server address and any port** (the ERP's, and the local address of the window itself). The ERP address is
  compiled into the program (`Redactor.DefaultOrigin`); the window sends only the pairing code and a computer
  name. The state sent to the page has no address field. Messages, the event log tail and error notes pass
  through `Redactor.Apply`, which replaces URLs, `host:port`, IPv4 addresses, `localhost` and the known hosts with
  `[سرور]`, so a stray exception message cannot leak them. `ControlPanelPrivacyTests` pins this.
- Override for tests and support only: `LabelPrinterAgent.exe pair --origin https://HOST` on the command line.
  `status` prints "server address hidden". The agent log file on disk is local to the PC and holds no credentials.

## Protections already in place
- Panel listens on `127.0.0.1` only; `Host` must be loopback; per-run token exchanged for an HttpOnly, SameSite=Strict
  cookie; every change needs the `X-LP` header, a JSON content type and a same-origin `Origin`.
- Strict policy: `default-src 'none'`, scripts/styles/images/fonts/connect only from `'self'`, no inline script, no
  framing, no caching, `nosniff`, no referrer, `Permissions-Policy` that turns off camera, microphone, location,
  USB, serial, Bluetooth and payment, `Cross-Origin-Opener-Policy` and `Cross-Origin-Resource-Policy: same-origin`.
- Toward the server: HTTPS only (loopback HTTP only for tests), no redirects, no cookies, timeouts, no internal
  retries, credential protected with DPAPI (current user), per-job idempotency and the safety rules in
  `docs/durability.md`.
- The program never asks for administrator rights (`asInvoker` manifest), is per-user, and is not packed,
  trimmed or obfuscated (packers are a common cause of antivirus false positives).

## Code signing (not done, and why)
An Authenticode signature needs a code-signing certificate issued to a verified publisher. That is a purchase
and an identity check that only the owner can make; no tool here can create a trusted one, and a self-signed
certificate would make Windows trust *less*, not more. Until then the installer is honestly labelled **unsigned**.

What was done to reduce false alarms without a certificate:
- Complete version information (company, product, description, copyright) in both programs and the installer.
- A normal Windows manifest, a real application icon, an installer wizard image, no packers.
- The SHA-256 of every release is published with it, and the page tells users to verify it. Never turn off
  Defender or SmartScreen.

What gives a real fix (owner action):
1. Get a code-signing certificate for the publisher (an EV certificate gives SmartScreen reputation at once; a
   standard one builds it over time), or use a free open-source signing programme if the repository qualifies.
2. Sign `LabelPrinterAgent.exe`, `LabelPrinterAgentBg.exe` and the setup file with `signtool` and an RFC 3161
   timestamp. Keep the key in a hardware token or a CI secret; never commit it.
3. If Defender still flags a signed or unsigned build, submit the file at
   <https://www.microsoft.com/wdsi/filesubmission> as a false positive.

Do not describe a release as signed until the signature has been verified (right click > Properties > Digital
Signatures, and `Get-AuthenticodeSignature`).
