# Tooling record

## .NET SDK (verified 2026-11-15, disposable development container)

| Item | Value |
|---|---|
| OS | Ubuntu 24.04.5 LTS (noble), x86_64 |
| Package source | existing `archive.ubuntu.com` / `security.ubuntu.com`, suites `noble-updates` + `noble-security`, component `main`, `Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg` |
| Signature enforcement | `Acquire::AllowInsecureRepositories "0"`, `AllowWeakRepositories "0"`, `AllowDowngradeToInsecureRepositories "0"`; `apt-get update` produced no signature warnings |
| Package | `dotnet-sdk-10.0` `10.0.115-0ubuntu1~24.04.1` (source package `dotnet10 10.0.115-10.0.15`, Origin: Ubuntu, source-built) |
| Install command | `DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends dotnet-sdk-10.0` |
| Plan (simulated first with `-s`) | 12 new packages, 0 upgraded, 0 removed: the SDK, `dotnet-host/hostfxr/runtime/apphost-pack/targeting-pack-10.0`, `aspnetcore-runtime/targeting-pack-10.0`, `dotnet-templates-10.0`, `liblttng-ust*` |
| After install | SDK `10.0.115`; runtimes `Microsoft.NETCore.App 10.0.15`, `Microsoft.AspNetCore.App 10.0.15` |

No repository was added, no signature check was disabled, no script was downloaded, no other package was upgraded.

### Support status

Source: Microsoft's `release-notes/releases-index.json` in `dotnet/core` (fetched from GitHub; the
`builds.dotnet.microsoft.com` host is not reachable from this container).

| Channel | Type | Phase | EOL |
|---|---|---|---|
| 10.0 | LTS | active | 2028-11-14 |
| 8.0 | LTS | **eol** | 2026-11-10 |
| 9.0 | STS | **eol** | 2026-11-10 |
| 11.0 | STS | active (released 2026-11-10) | 2028-11-14 |

.NET 8 went out of support five days before this work, so the project targets `net10.0`.
The installed runtime `10.0.15` equals the index's latest 10.0 runtime. The installed SDK `10.0.115` is the
Ubuntu source-built 1xx feature band; Microsoft's newest 10.0 SDK is `10.0.404`. The SDK is therefore **not**
the newest SDK feature band, and no claim is made that the Ubuntu package is the latest patch of its band
beyond it carrying the latest runtime.

Difference from the earlier proposal: the earlier proposal text was not available in this session, so it could
not be compared line by line. If it named .NET 8, this record supersedes it for the reason above.

### `global.json`

`version: 10.0.100`, `rollForward: latestFeature`, `allowPrerelease: false`: any installed 10.0 SDK at or above
10.0.100 is used, preferring the highest feature band and patch; it never rolls to 11.x and never to a preview.
This lets the Ubuntu 1xx band and a Windows developer's 4xx band build the same sources. Tighten to an exact
band once CI exists.

## Dependencies and reproducibility

- Production project `LabelPrinter.Agent.Core`: **zero** package references.
- Test project: **zero** package references (project reference only).
- `RestorePackagesWithLockFile=true`; both `packages.lock.json` files are committed and currently list no
  packages. `dotnet restore --locked-mode` passes.

## Test framework: approval pending

No test package was restored. To keep A1 testable without unapproved dependencies, the tests run on a ~90-line
in-repo runner (`tests/.../Harness/Harness.cs`). This is a stopgap; it has no IDE integration, no parallelism
and no result files.

Approval request (exact set, all checked against the official NuGet registration API on 2026-11-15):

| Package | Version | Published | License (NuGet metadata) | Role | Advisories / deprecation in metadata |
|---|---|---|---|---|---|
| `xunit.v3` | 4.0.3 | 2026-11-09 | Apache-2.0 | test framework + assertions (test project only) | none / none |
| `xunit.runner.visualstudio` | 4.0.1 | 2026-10-09 | Apache-2.0 | VSTest adapter (`dotnet test`, IDEs) | none / none |
| `Microsoft.NET.Test.Sdk` | 18.12.0 | 2026-11-11 | MIT | VSTest host/SDK | none / none |

- Framework compatibility: all three ship a `net8.0` asset group, which `net10.0` consumes.
- Each was the highest stable version in its registration index at query time.
- Direct transitive dependencies seen in metadata: `xunit.v3` → `xunit.v3.mtp-v2` → `xunit.v3.core.mtp-v2`,
  `xunit.v3.assert`, `xunit.analyzers` (≥ 2.3.0); `Microsoft.NET.Test.Sdk` → `Microsoft.CodeCoverage`,
  `Microsoft.TestPlatform.TestHost` (≥ 18.12.0).
- **Not yet reviewed:** the deeper graph (notably Microsoft.Testing.Platform packages pulled by
  `xunit.v3.core.mtp-v2`) and their licenses. That can only be listed exactly after a restore, which has not
  been run. After approval: restore, record the resolved graph from `packages.lock.json`, review it, then
  migrate the tests (mechanical: `[Test]` → `[Fact]`, `Check.*` → `Assert.*`).
- Alternative needing fewer packages: run xunit.v3 on Microsoft.Testing.Platform only and drop the two VSTest
  packages. Not evaluated in practice.
