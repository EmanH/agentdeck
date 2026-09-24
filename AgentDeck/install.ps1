# Build AgentDeck and install it to %LOCALAPPDATA%\Programs\AgentDeck, then (re)start it.
# Usage: powershell -ExecutionPolicy Bypass -File install.ps1 [-NoStart]
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\AgentDeck'
$exe = Join-Path $target 'AgentDeck.exe'

$running = Get-Process AgentDeck -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping running AgentDeck (open terminals will close)...'
    $running | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process AgentDeck -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
}

# Start clean so files from an older build can't linger (only ever deletes our own install folder).
# Retry briefly: an exiting process can hold file handles for a moment.
if (Test-Path (Join-Path $target 'AgentDeck.exe')) {
    for ($i = 1; $i -le 10; $i++) {
        try { Remove-Item $target -Recurse -Force -ErrorAction Stop; break }
        catch { if ($i -eq 10) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

# Self-contained: AgentDeck carries its own .NET runtime, so Visual Studio / .NET updates can't
# pull files out from under it while it's running.
dotnet publish "$PSScriptRoot\AgentDeck.csproj" -c Release -r win-x64 --self-contained true -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Installed to $target"

if (-not $NoStart) { Start-Process $exe }
