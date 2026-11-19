# Codex Token Meter 插件

插件目录里包含 Windows 和 macOS 的运行程序。通过 Codex 的本地插件机制安装，使用同一个浮条显示模型、当前对话和今日用量。macOS 安装包已包含运行时，会用到系统的辅助功能授权。

## 安装

也可以从 [GitHub Releases](https://github.com/Jztice5/codex-token-meter/releases) 下载 Windows 可执行文件或适合本机芯片的 macOS 安装包，直接运行浮条。

安装插件时，先解压插件包并将整个目录放在固定位置。Windows 可在 PowerShell 运行 `./install-windows.ps1`。macOS 可在终端运行 `bash install-macos.sh`。也可以在解压目录使用已配置的 Codex CLI。

```text
codex plugin marketplace add .
codex plugin add codex-token-meter@codex-token-meter-local
```

打开新的 Codex 对话，输入 `$token-meter 启动浮条`。如果桌面应用仍沿用旧插件清单，重启后再打开新对话。输入 `$token-meter 检查状态` 可查看浮条进程是否运行。

如果 macOS 阻止首次启动，可按「系统设置 → 隐私与安全性」中的提示确认打开，再在「辅助功能」里授权 Codex Token Meter。macOS 应用为临时签名测试版。

## 使用和更新

浮条随 Codex 窗口显示，拖动点阵改变位置。点「明细」可在「当前对话」和「今日用量」间切换。今日总量包括本机缓存输入，按北京时间 00:00 起算。

浮条和明细会跟随 Codex 的浅色或深色外观。Codex 选择跟随系统时，浮条也会随系统切换。

插件启动时会优先复用正在运行的浮条。Mac 上也会复用已安装的同版本应用。更新插件后退出旧浮条，再运行启动技能。
