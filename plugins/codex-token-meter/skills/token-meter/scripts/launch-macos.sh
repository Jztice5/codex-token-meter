#!/bin/bash
set -euo pipefail

action="${1:-status}"
if [[ "$action" != start && "$action" != status ]]; then
    printf '%s\n' 'Usage: launch-macos.sh [start|status]' >&2
    exit 2
fi
if [[ "$(/usr/bin/uname -s)" != Darwin ]]; then
    printf '%s\n' 'This launcher requires the local macOS desktop.' >&2
    exit 2
fi
if /usr/bin/pgrep -x -u "$(/usr/bin/id -u)" CodexTokenMeter >/dev/null; then
    printf '%s\n' 'Codex Token Meter is running.'
    exit 0
fi
if [[ "$action" == status ]]; then
    printf '%s\n' 'Codex Token Meter is not running.'
    exit 0
fi

assets=$(cd -- "$(/usr/bin/dirname -- "$0")/../../../assets" && pwd)
if [[ "$(/usr/bin/uname -m)" == arm64 || "$(/usr/sbin/sysctl -in hw.optional.arm64 2>/dev/null || true)" == 1 ]]; then
    file='CodexTokenMeter-macOS-AppleSilicon.zip'
else
    file='CodexTokenMeter-macOS-Intel.zip'
fi

directory="$HOME/Library/Application Support/Codex Token Meter/plugin-bin/1.2.0"
app="$directory/Codex Token Meter.app"
for installed in '/Applications/Codex Token Meter.app' "$HOME/Applications/Codex Token Meter.app" "$app"; do
    if [[ -f "$installed/Contents/Info.plist" ]] &&
        [[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$installed/Contents/Info.plist" 2>/dev/null || true)" == local.codex-token-meter.desktop ]] &&
        [[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$installed/Contents/Info.plist" 2>/dev/null || true)" == 1.2.0 ]]; then
        app="$installed"
        break
    fi
done

if [[ ! -d "$app" ]]; then
    expected=$(/usr/bin/awk -v name="$file" '{ sub(/\r$/, "") } $2 == name { print $1 }' "$assets/sha256.txt")
    actual=$(/usr/bin/shasum -a 256 "$assets/$file" | /usr/bin/awk '{print $1}')
    if [[ -z "$expected" || "$actual" != "$expected" ]]; then
        printf '%s\n' 'The bundled macOS app failed its SHA-256 check.' >&2
        exit 1
    fi

    /bin/mkdir -p "$directory"
    if ! /bin/mkdir "$directory/.installing"; then
        printf '%s\n' 'Another launcher is preparing the floating panel. Try Start again shortly.' >&2
        exit 1
    fi
    stage=''
    trap 'if [[ -n "$stage" ]]; then /bin/rm -rf -- "$stage"; fi; /bin/rmdir "$directory/.installing"' EXIT
    stage=$(/usr/bin/mktemp -d "$directory/.stage.XXXXXX")
    /usr/bin/ditto -x -k "$assets/$file" "$stage"
    if [[ ! -d "$app" ]]; then /bin/mv "$stage/Codex Token Meter.app" "$app"; fi
fi

/usr/bin/open "$app"
for _ in 1 2 3 4 5; do
    if /usr/bin/pgrep -x -u "$(/usr/bin/id -u)" CodexTokenMeter >/dev/null; then
        printf '%s\n' 'Codex Token Meter is running.'
        exit 0
    fi
    /bin/sleep 1
done
printf 'macOS accepted the launch request. Check the desktop for approval prompts. App: %s\n' "$app"
