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

Check (-not (Test-Path $app)) 'machine is clean: program folder absent'
Check (-not (Test-Path $data)) 'machine is clean: data folder absent'

Write-Host "`n== install $Version"
Run-Setup $Installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\install.log")
Check (Test-Path $exe) 'console program installed'
Check (Test-Path (Join-Path $app 'LabelPrinterAgentBg.exe')) 'background program installed'
Check (Test-Path (Join-Path $app 'setup-guide.md')) 'setup guide installed'
Check ((Agent @('version')).Trim() -eq $Version) "reports version $Version"
Check (-not (Test-Path $data)) 'installing created no data (no pairing, no journal)'
Check ($null -eq (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).LabelPrinterAgent) 'installing registered no autostart'
Check (-not (Get-Process LabelPrinterAgent, LabelPrinterAgentBg -ErrorAction SilentlyContinue)) 'installing started nothing'

Write-Host "`n== behaviour before configuration"
Agent @('selftest') | Write-Host
$status = Agent @('status')
Check ($status -match 'paired\s+no') 'status: not paired'
Check ($status -match 'none selected') 'status: no printer selected'
& $exe run | Out-Null
Check ($LASTEXITCODE -eq 3) "run refuses to start unpaired (exit $LASTEXITCODE)"
$bg = Start-Process (Join-Path $app 'LabelPrinterAgentBg.exe') -Wait -PassThru
Check ($bg.ExitCode -eq 3) "background program refuses to start unpaired (exit $($bg.ExitCode))"

Write-Host "`n== printer selection is explicit"
& $exe select-printer --queue 'No Such Queue' --language tspl --dpi 203 --width-mm 50 --height-mm 30 2>&1 | Out-Null
Check ($LASTEXITCODE -eq 2) 'a queue that does not exist is refused'
if ($env:LP_TEST_PRINT_QUEUE) {
  Check ((Agent @('printers')) -match [regex]::Escape($env:LP_TEST_PRINT_QUEUE)) 'test queue is listed'
  Agent @('select-printer', '--queue', $env:LP_TEST_PRINT_QUEUE, '--language', 'tspl', '--dpi', '203', '--width-mm', '50', '--height-mm', '30') | Write-Host
  & $exe run | Out-Null
  Check ($LASTEXITCODE -eq 3) 'run still refuses: a selected printer is not enough without pairing'
}

Write-Host "`n== opt-in autostart"
Agent @('autostart', 'enable') | Write-Host
Check ((Get-ItemProperty $runKey).LabelPrinterAgent -like '*LabelPrinterAgentBg.exe*') 'autostart entry points at the background program'

Write-Host "`n== upgrade keeps data"
# Stand-ins for a journal and a credential: the upgrade must leave every byte in the data folder alone.
$marker = Join-Path $data 'journal.lpj'
[IO.File]::WriteAllBytes($marker, [byte[]](1..64))
$before = Get-ChildItem $data -Recurse -File | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" } | Sort-Object
Run-Setup $NextInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\upgrade.log")
Check ((Agent @('version')).Trim() -eq '99.0.0-upgradetest') 'upgraded program reports the new version'
$after = Get-ChildItem $data -Recurse -File | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" } | Sort-Object
Check (($before -join '|') -eq ($after -join '|')) 'data folder is byte-identical after the upgrade'
Check ((Get-ItemProperty $runKey).LabelPrinterAgent -like '*LabelPrinterAgentBg.exe*') 'autostart choice survives the upgrade'
Remove-Item $marker

Write-Host "`n== uninstall (silent) keeps data, removes program and autostart"
$uninstaller = Get-ChildItem $app -Filter 'unins*.exe' | Select-Object -First 1
Run-Setup $uninstaller.FullName @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
Start-Sleep -Seconds 3
Check (-not (Test-Path $exe)) 'program removed'
Check ($null -eq (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).LabelPrinterAgent) 'autostart entry removed'
Check (Test-Path (Join-Path $data 'settings.json')) 'data folder kept by a silent uninstall'
Remove-Item $data -Recurse -Force
Write-Host "`ninstaller smoke test passed"
