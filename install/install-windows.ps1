# hearken — Windows dependency + setup installer. Run in PowerShell.
#   powershell -ExecutionPolicy Bypass -File install\install-windows.ps1
# Assumes helper SOURCES are vendored into the repo at windows\lib\ (capture.cs, play.cs, NAudio.dll).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$lib  = "$env:USERPROFILE\audio-bridge\lib"
New-Item -ItemType Directory -Force -Path $lib | Out-Null

Write-Host "== 1/3  NAudio + compile capture.exe / play.exe ==" -ForegroundColor Cyan
Copy-Item "$repo\windows\lib\NAudio.dll" $lib -Force
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x not found ($csc). Install .NET Framework 4.8." }
foreach ($name in 'capture','play') {
  & $csc /nologo /target:exe /out:"$lib\$name.exe" /r:"$lib\NAudio.dll" "$repo\windows\lib\$name.cs"
  Write-Host "  built $lib\$name.exe"
}

Write-Host "== 2/3  hearken.exe ==" -ForegroundColor Cyan
if (Get-Command wails -ErrorAction SilentlyContinue) {
  Push-Location $repo; wails build; Pop-Location
  Write-Host "  built $repo\build\bin\hearken.exe"
} else {
  Write-Host "  Go/Wails not found — install Go + wails, or drop a prebuilt hearken.exe"
  Write-Host "  into $repo\build\bin\ (from a GitHub release)."
}

Write-Host "== 3/3  Tailscale (optional; a plain LAN IP also works) ==" -ForegroundColor Cyan
if (-not (Get-Command tailscale -ErrorAction SilentlyContinue)) {
  Write-Host "  Install Tailscale (https://tailscale.com/download) and log in, or use a LAN IP."
}

Write-Host ""
Write-Host "Done. Launch hearken.exe, put the HOST's IP in 'Peer IP', Save." -ForegroundColor Green
Write-Host "Windows defaults to CLIENT (it dials the host). It auto-connects on every launch thereafter."
