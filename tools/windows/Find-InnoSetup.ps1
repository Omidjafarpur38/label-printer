# Finds the Inno Setup compiler. Uses the copy preinstalled on the GitHub-hosted Windows image when present;
# otherwise installs the official package through winget (publisher manifest, hash-checked by winget).
$ErrorActionPreference = 'Stop'
function Find-Iscc {
  $candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe")
  $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $found) { $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue; if ($cmd) { $found = $cmd.Source } }
  return $found
}
$iscc = Find-Iscc
if (-not $iscc) {
  Write-Host 'Inno Setup is not preinstalled; installing JRSoftware.InnoSetup with winget.'
  winget install --id JRSoftware.InnoSetup --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity
  $iscc = Find-Iscc
}
if (-not $iscc) { throw 'Inno Setup compiler not found' }
$version = (Get-Item $iscc).VersionInfo.ProductVersion
Write-Host "Inno Setup compiler: $iscc (version $version)"
"iscc=$iscc" >> $env:GITHUB_OUTPUT
"version=$version" >> $env:GITHUB_OUTPUT
