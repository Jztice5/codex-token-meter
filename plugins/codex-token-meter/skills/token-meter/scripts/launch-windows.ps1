param(
    [ValidateSet('Start','Status')]
    [string]$Action = 'Status'
)

$ErrorActionPreference = 'Stop'
$running = Get-Process -Name CodexTokenMeter -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running) {
    'Codex Token Meter is running.'
    exit 0
}
if ($Action -eq 'Status') {
    'Codex Token Meter is not running.'
    exit 0
}

$assets = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) 'assets'
$file = 'CodexTokenMeter.exe'
$payload = Join-Path $assets $file
$hashes = Get-Content -LiteralPath (Join-Path $assets 'sha256.json') -Raw | ConvertFrom-Json
$expected = $hashes.$file
if (!$expected -or (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -ne $expected) {
    throw 'The bundled Windows executable failed its SHA-256 check.'
}

$directory = Join-Path $env:LOCALAPPDATA 'CodexTokenMeter/plugin-bin/1.2.2'
$null = New-Item -ItemType Directory -Path $directory -Force
$program = Join-Path $directory $file
if (!(Test-Path -LiteralPath $program) -or (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash -ne $expected) {
    Copy-Item -LiteralPath $payload -Destination $program -Force
}
$process = Start-Process -FilePath $program -WindowStyle Normal -PassThru
if ($process.WaitForExit(350) -and !(Get-Process -Name CodexTokenMeter -ErrorAction SilentlyContinue)) {
    throw 'The floating panel exited during startup.'
}
'Codex Token Meter is running.'
