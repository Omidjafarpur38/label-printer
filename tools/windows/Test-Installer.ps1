# Installs, upgrades and uninstalls the agent on this machine and checks what each step must and must not do.
# For disposable machines (CI): it installs into the current user's profile and removes everything again.
param(
  [Parameter(Mandatory)] [string] $Installer,
  [Parameter(Mandatory)] [string] $NextInstaller,
  [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\LabelPrinterAgent'
$data = Join-Path $env:LOCALAPPDATA 'LabelPrinterAgent'
$exe = Join-Path $app 'LabelPrinterAgent.exe'
$bgExe = Join-Path $app 'LabelPrinterAgentBg.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Check([bool] $condition, [string] $what) {
  if (-not $condition) { throw "FAILED: $what" }
  Write-Host "ok    $what"
}
function Agent([string[]] $arguments, [int[]] $allowed = @(0)) {
  $out = & $exe @arguments 2>&1 | Out-String
  if ($allowed -notcontains $LASTEXITCODE) { throw "LabelPrinterAgent $($arguments -join ' ') exited $LASTEXITCODE`n$out" }
  return $out
}
function Run-Setup([string] $file, [string[]] $arguments) {
  $p = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "$file exited $($p.ExitCode)" }
}
function Autostart-Value { return (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).LabelPrinterAgent }
function Wait-Until([scriptblock] $condition, [string] $what, [int] $seconds = 30) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) { if (& $condition) { return }; Start-Sleep -Milliseconds 300 }
  throw "FAILED: timed out waiting for $what"
}
function Panel-Session {
  # What the Start-menu icon does: read the local panel address and token, exchange the token for the cookie.
  Wait-Until { Test-Path (Join-Path $data 'ui.json') } 'the control panel to publish its address'
  $info = Get-Content (Join-Path $data 'ui.json') -Raw | ConvertFrom-Json
  $base = "http://127.0.0.1:$($info.Port)"
  $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  Invoke-WebRequest "$base/?t=$([uri]::EscapeDataString($info.Token))" -WebSession $session -UseBasicParsing -MaximumRedirection 5 | Out-Null
  return @{ Base = $base; Session = $session }
}

Check (-not (Test-Path $app)) 'machine is clean: program folder absent'
Check (-not (Test-Path $data)) 'machine is clean: data folder absent'

Write-Host "`n== install $Version (silent: default choices)"
Run-Setup $Installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\install.log")
Check (Test-Path $exe) 'console program installed'
Check (Test-Path $bgExe) 'control-window program installed'
Check (Test-Path (Join-Path $app 'setup-guide.md')) 'setup guide installed'
Check ((Agent @('version')).Trim() -eq $Version) "reports version $Version"
Check (-not (Test-Path $data)) 'installing created no data (no pairing, no journal)'
Check (-not (Get-Process LabelPrinterAgent, LabelPrinterAgentBg -ErrorAction SilentlyContinue)) 'a silent install started nothing'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Label Printer Agent (experimental)'
Check (Test-Path (Join-Path $startMenu 'Label Printer Agent.lnk')) 'Start-menu icon exists'
Check ((Autostart-Value) -like '*LabelPrinterAgentBg.exe" --background') 'autostart is on by default and starts without a window'

Write-Host "`n== idle until configured: the control panel runs, claims and prints nothing"
$agentProc = Start-Process $bgExe -ArgumentList '--background' -PassThru
$panel = Panel-Session
$state = Invoke-RestMethod "$($panel.Base)/api/state" -WebSession $panel.Session
Check ($state.state -eq 'NotPaired') 'unpaired: the panel says it needs a connection'
Check ($null -eq $state.printer) 'no printer is selected by default'
Check ($state.queues.Count -ge 1) 'the panel lists the Windows print queues'
$denied = try { Invoke-WebRequest "$($panel.Base)/api/state" -UseBasicParsing -ErrorAction Stop; 200 } catch { [int]$_.Exception.Response.StatusCode }
Check ($denied -eq 401) 'the panel refuses a request without the session cookie'
$second = Start-Process $bgExe -ArgumentList '--background' -PassThru -Wait
Check ($second.ExitCode -eq 0) 'a second launch exits at once (single instance)'
Check (-not $agentProc.HasExited) 'the first instance is still running'

Write-Host "`n== configuring through the panel"
function Post($path, $body) {
  Invoke-RestMethod "$($panel.Base)$path" -Method Post -WebSession $panel.Session -ContentType 'application/json' -Headers @{ 'X-LP' = '1' } -Body ($body | ConvertTo-Json) -SkipHttpErrorCheck -StatusCodeVariable code | Out-Null
  return $code
}
Check ((Post '/api/printer' @{ queue = 'No Such Queue'; language = 'tspl'; dpi = 203; widthMm = 50; heightMm = 30 }) -eq 400) 'a queue that does not exist is refused'
if ($env:LP_TEST_PRINT_QUEUE) {
  Check ((Post '/api/printer' @{ queue = $env:LP_TEST_PRINT_QUEUE; language = 'tspl'; dpi = 203; widthMm = 50; heightMm = 30 }) -eq 200) 'the test queue can be selected'
  $state = Invoke-RestMethod "$($panel.Base)/api/state" -WebSession $panel.Session
  Check ($state.state -eq 'NotPaired') 'a selected printer alone does not start anything: still needs a connection'
}
Check ((Post '/api/pause' @{ paused = $true }) -eq 200) 'pause works'
Check ((Post '/api/pause' @{ paused = $false }) -eq 200) 'resume works'

Write-Host "`n== stop"
Agent @('stop', '--wait', '30') | Write-Host
Wait-Until { $agentProc.HasExited } 'the agent to exit after stop'
Check $true 'stop closes the running agent (this is what the installer does before an upgrade)'
Check (-not (Test-Path (Join-Path $data 'ui.json'))) 'the panel address file is removed on exit'

Write-Host "`n== console program rules"
Agent @('selftest') | Write-Host
& $exe run | Out-Null
Check ($LASTEXITCODE -eq 3 -or $LASTEXITCODE -eq 4) "console 'run' still refuses to start unconfigured (exit $LASTEXITCODE)"

Write-Host "`n== autostart choice"
Agent @('autostart', 'disable') | Write-Host
Check ($null -eq (Autostart-Value)) 'autostart can be switched off'

Write-Host "`n== upgrade keeps data and the user's autostart choice"
$marker = Join-Path $data 'journal.lpj'
[IO.File]::WriteAllBytes($marker, [byte[]](1..64))
$before = Get-ChildItem $data -Recurse -File | Where-Object { $_.Name -ne 'agent.log' -and $_.Name -ne 'ui.json' } | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" } | Sort-Object
Run-Setup $NextInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\upgrade.log")
Check ((Agent @('version')).Trim() -eq '99.0.0-upgradetest') 'upgraded program reports the new version'
$after = Get-ChildItem $data -Recurse -File | Where-Object { $_.Name -ne 'agent.log' -and $_.Name -ne 'ui.json' } | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" } | Sort-Object
Check (($before -join '|') -eq ($after -join '|')) 'data folder is byte-identical after the upgrade'
Check ($null -eq (Autostart-Value)) 'an upgrade does not re-enable autostart that the user switched off'
Agent @('autostart', 'enable') | Write-Host
Check ((Autostart-Value) -like '*LabelPrinterAgentBg.exe" --background') 'autostart can be switched on again'

Write-Host "`n== uninstall (silent) keeps data, removes program and autostart"
$uninstaller = Get-ChildItem $app -Filter 'unins*.exe' | Select-Object -First 1
Run-Setup $uninstaller.FullName @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
Start-Sleep -Seconds 3
Check (-not (Test-Path $exe)) 'program removed'
Check ($null -eq (Autostart-Value)) 'autostart entry removed'
Check (-not (Test-Path (Join-Path $startMenu 'Label Printer Agent.lnk'))) 'Start-menu icon removed'
$kept = Get-ChildItem $data -Recurse -File | Where-Object { $_.Name -ne 'agent.log' -and $_.Name -ne 'ui.json' } | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" } | Sort-Object
Check (($before -join '|') -eq ($kept -join '|')) 'data folder is byte-identical after a silent uninstall'
Remove-Item $data -Recurse -Force
Write-Host "`ninstaller smoke test passed"
