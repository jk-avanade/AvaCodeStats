# Publishes AvaCodeStats to %LOCALAPPDATA%\Programs\AvaCodeStats and starts it.
# Re-run after pulling changes. "Start with Windows" (tray menu) points at this installed copy.
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\AvaCodeStats'
$project = Join-Path $PSScriptRoot '..\src\AvaCodeStats\AvaCodeStats.csproj'

Get-Process AvaCodeStats -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish $project -c Release -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

if (-not $NoStart) { Start-Process (Join-Path $target 'AvaCodeStats.exe') }
Write-Host "Installed to $target"
