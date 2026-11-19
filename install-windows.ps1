$ErrorActionPreference = 'Stop'
$command = Get-Command codex -ErrorAction SilentlyContinue | Select-Object -First 1
$cli = if ($command) { $command.Source }
if (!$cli) {
    $package = Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1
    if ($package) {
        $candidate = Join-Path $package.InstallLocation 'app/resources/codex.exe'
        if (Test-Path -LiteralPath $candidate) { $cli = $candidate }
    }
}
if (!$cli) { throw 'Codex CLI was not found. Install the Codex desktop app or make codex available in PATH.' }

& $cli plugin marketplace add $PSScriptRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $cli plugin add 'codex-token-meter@codex-token-meter-local'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'Installed. Open a new Codex chat and invoke $token-meter.'
