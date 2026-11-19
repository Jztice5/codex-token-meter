# Codex Token Meter · macOS 构建

macOS 14 或更新版本使用自带 .NET 运行时的应用包。请从对应架构的 ZIP 安装，具体步骤见 `安装说明.txt`。

在包含相邻 `codex-token-meter` 源码目录的环境中，可使用 .NET 10 SDK 发布 `dotnet publish CodexTokenMeter.csproj -c Release -r osx-arm64 --self-contained true`。Intel 对应 `osx-x64`。将发布目录放入 `Codex Token Meter.app/Contents/MacOS`，将 `Info.plist` 放入 `Contents`，然后签名。内含运行时的发行包由用户按架构选择。macOS 原生界面需要实机验证。

会话匹配、流式 JSONL 读取和今日统计与 Windows 版共用 `Follow.cs`、`Usage.cs`、`Today.cs`。Mac 版使用系统辅助功能读取窗口和标题。安装需要用户在系统设置允许辅助功能。本地设置位于 `~/Library/Application Support/Codex Token Meter/placement.json`。
