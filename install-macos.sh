#!/bin/bash
set -euo pipefail

root=$(cd -- "$(/usr/bin/dirname -- "$0")" && pwd)
cli=$(command -v codex || true)
if [[ -z "$cli" ]]; then
    for app in '/Applications/ChatGPT.app' '/Applications/Codex.app' "$HOME/Applications/ChatGPT.app" "$HOME/Applications/Codex.app"; do
        if [[ -x "$app/Contents/Resources/codex" ]]; then
            cli="$app/Contents/Resources/codex"
            break
        fi
    done
fi
if [[ -z "$cli" ]]; then
    printf '%s\n' 'Codex CLI was not found. Make codex available in PATH.' >&2
    exit 1
fi

"$cli" plugin marketplace add "$root"
"$cli" plugin add 'codex-token-meter@codex-token-meter-local'
printf '%s\n' 'Installed. Open a new Codex chat and invoke $token-meter.'
