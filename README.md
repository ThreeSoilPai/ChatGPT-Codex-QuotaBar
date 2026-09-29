# Codex 剩余额度条（便携版）

一个适用于 Windows 的轻量桌面工具，在 Codex 输入框附近显示账户剩余额度。它读取本机 Codex App Server 的额度数据，不修改 Codex 安装文件，也不读取或保存登录令牌。

## 下载与使用

在 [Releases 页面](../../releases)下载 **`Codex剩余额度条.exe`**。这是约 71 MB 的自包含单文件，已经带有它所需的 .NET 运行时。把它放在固定文件夹后双击，再打开 Codex 对话窗口，额度条就会出现在输入框附近。程序没有普通主窗口；可在系统托盘查看状态和退出。

电脑需要满足以下条件：Windows 10/11 64 位；已安装并登录 Codex 桌面应用，本机的 `codex.exe app-server` 可用。此 EXE 不包含 Codex 本身。

默认双击会为当前用户登记登录自启动（Windows 任务计划和注册表 Run 项）。如果只想临时运行、不登记自启动，可在终端执行：

```powershell
.\Codex剩余额度条.exe --overlay
```

## 功能

- 显示剩余额度百分比，并随 Codex 窗口和输入框移动。
- 切换到其他应用、输入框不可见或定位不确定时自动隐藏。
- 每 60 秒刷新额度；连接中断时显示状态，不编造百分比。
- 在系统托盘显示状态和完整重置时间。

## 从源码构建

需要 Windows 和 .NET 9 SDK：

```powershell
dotnet publish .\src\CodexQuotaBar.Portable\CodexQuotaBar.Portable.csproj -c Release -o .\release
```

生成的 `release/Codex剩余额度条.exe` 是便携版。推送 `v*` 版本标签时，GitHub Actions 会构建并发布这个 EXE 到 Releases 页面。

## 常见问题

**双击后看不到窗口？** 程序会在后台等待 Codex 窗口。打开 Codex 对话页并检查系统托盘；切换到其他应用时额度条会隐藏。

**复制到另一台电脑后不显示额度？** 请先确认那台电脑的 Codex 桌面应用已安装、已登录并可正常打开。额度需要由那台电脑上的 Codex App Server 提供。

**如何移除？** 先退出托盘中的额度条并结束后台监听器；若已登记自启动，还需移除当前用户的 `CodexQuotaBarWatcherTask` 计划任务及 `CodexQuotaBarWatcher` Run 值，然后删除 EXE。只删除 EXE 不会自动清除自启动项。

本项目是与 Codex 配合使用的独立工具，不属于 OpenAI 官方产品。
