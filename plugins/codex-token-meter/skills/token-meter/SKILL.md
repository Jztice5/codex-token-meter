---
name: token-meter
description: Start the local Codex Token Meter floating panel or check whether it is running. Use when the user explicitly asks to start, open, or check Codex Token Meter. Use the meter panel for token breakdowns and daily usage.
---

# Codex Token Meter

Use the launcher shipped with this skill on the computer running the Codex desktop app. Resolve the script path relative to this SKILL.md. Use the status action for checks and for a setup step with no request to open the panel.

- Windows — run `powershell.exe -NoProfile -File "<skill directory>\scripts\launch-windows.ps1" -Action Start` to open the floating panel. Use `-Action Status` to check it.
- macOS — run `/bin/bash "<skill directory>/scripts/launch-macos.sh" start` to open the panel. Use `status` to check it.

If tools run on a different host from the user's desktop, direct the user to the installer on their desktop. On macOS, a launch request can require the user's approval in Privacy & Security and permission in Accessibility. Only report that the panel is running after the launcher reports a running process. The floating panel's visibility depends on the Codex desktop window being in the foreground.
