# Creates a print queue whose port writes to a file, for tests of the RAW spooler path. No physical printer.
# Exports LP_TEST_PRINT_QUEUE / LP_TEST_PRINT_FILE to later workflow steps. Intended for disposable CI machines.
$ErrorActionPreference = 'Stop'
$queue = 'LP Test Queue'
$dir = Join-Path $env:RUNNER_TEMP 'lp-print'
New-Item -ItemType Directory -Force $dir | Out-Null
$file = Join-Path $dir 'out.prn'

Start-Service Spooler
$driver = 'Generic / Text Only'
if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name $driver }
if (-not (Get-PrinterPort -Name $file -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name $file }
if (-not (Get-Printer -Name $queue -ErrorAction SilentlyContinue)) { Add-Printer -Name $queue -DriverName $driver -PortName $file }
Get-Printer -Name $queue | Format-List Name, DriverName, PortName, PrinterStatus

"LP_TEST_PRINT_QUEUE=$queue" >> $env:GITHUB_ENV
"LP_TEST_PRINT_FILE=$file" >> $env:GITHUB_ENV
