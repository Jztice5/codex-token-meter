# Codex Token Meter

Windows 10/11 上的 Codex 桌面浮条，需要 .NET Framework 4.8。它使用独立窗口和系统托盘。

## 使用

1. 双击 `CodexTokenMeter.exe`，回到 Codex 桌面窗口。浮条会出现在输入区域附近。
2. 拖动左边的点阵手柄调整位置。浮条会记住位置。右键可重置位置或退出。
3. 浮条默认自动跟随 Codex 里当前打开的对话。点「明细 → 当前对话」可查看会话累计、最近一次模型请求、输入、输出和缓存输入。
4. 点「明细 → 今日用量」，查看北京时间每天 00:00 起的本机 Codex 总用量和各模型用量。
5. 托盘菜单中的「显示浮条（键盘可操作）」可将焦点移到浮条。按 Ctrl 加方向键移动，按 Esc 收起明细。

当前聊天通过 Windows 辅助功能识别。客户端提供明确的会话地址时优先使用 ID，否则核对可见标题和本地索引。切换过程中标题冲突会暂时清空用量。遇到同名或缺少索引，可通过右键菜单指定聊天链接或 `/status` 显示的 ID，再用「自动跟随当前对话」恢复自动模式。界面在 Codex 窗口位于前台时显示，使用辅助功能定位输入区域，无法读取位置时按窗口尺寸预定位。

用量随 Codex 写入记录自动刷新，包含缓存输入。今日统计覆盖本机可读且保存了用量的会话和归档。响应级记录与重放快照去重。模型归属无法确认的用量显示为「模型未知」，缺失的统计会提示覆盖不完整。应用只读 `$CODEX_HOME` 下的本地会话记录和索引，默认目录是 `%USERPROFILE%\.codex`。浮条偏移和跟随设置保存在 `%LOCALAPPDATA%\CodexTokenMeter\placement.txt`。

## 从源码编译

在此目录中运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`。脚本使用 Windows 自带的 .NET Framework 编译器生成 `CodexTokenMeter.exe`。
