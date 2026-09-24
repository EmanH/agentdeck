# Regenerates docs/screenshots without touching a running AgentDeck or the Stream Deck hardware.
#  - Stream Deck mockups: AgentDeck.exe --render-deck (real key art, device frame, exits immediately).
#  - UI screenshots: the real web UI served locally with mock-bridge.js standing in for the C# host,
#    captured with headless Microsoft Edge.
# Usage: powershell -ExecutionPolicy Bypass -File docs\tools\screenshots.ps1   (needs Python 3 on PATH)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\..\.."
$out = Join-Path $repo 'docs\screenshots'
$work = Join-Path $env:TEMP 'agentdeck-screenshots'
New-Item -ItemType Directory -Force $out | Out-Null

# 1. Stream Deck mockups.
dotnet build "$repo\AgentDeck\AgentDeck.csproj" -nologo -v q
Start-Process "$repo\AgentDeck\bin\Debug\net10.0-windows\AgentDeck.exe" -ArgumentList '--render-deck', $out -Wait

# 2. UI: copy wwwroot and load mock-bridge.js before app.js.
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
Copy-Item "$repo\AgentDeck\wwwroot" "$work\site" -Recurse
Copy-Item "$PSScriptRoot\mock-bridge.js" "$work\site\mock-bridge.js"
$html = [IO.File]::ReadAllText("$work\site\index.html")
[IO.File]::WriteAllText("$work\site\index.html",
    $html.Replace('<script src="app.js"></script>', '<script src="mock-bridge.js"></script><script src="app.js"></script>'),
    (New-Object Text.UTF8Encoding $false))

$server = Start-Process python -ArgumentList '-m', 'http.server', '8765', '--bind', '127.0.0.1', '--directory', "$work\site" -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 2
    $edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
    foreach ($scene in 'main', 'workflows', 'editor', 'models', 'dictations') {
        $file = Join-Path $out "ui-$scene.png"
        $p = Start-Process $edge -PassThru -WindowStyle Hidden -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1.25',
            '--window-size=1500,920', "--user-data-dir=$work\edge", '--virtual-time-budget=6000',
            "--screenshot=$file", "http://127.0.0.1:8765/index.html?scene=$scene")
        if (-not $p.WaitForExit(60000)) { $p.Kill() }
        Write-Host "ui-$scene.png"
    }
}
finally { Stop-Process -Id $server.Id -Force }
