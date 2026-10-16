# PinDesk

轻量窗口固定工具，支持 Windows 原窗口置顶与 macOS 实时参考悬浮。

项目提供原生客户端的开发预览。Windows 已通过本机编译、22 项核心测试、8 项真实 Win32 窗口集成测试和管理界面冒烟测试；完整应用兼容性与多显示器人工验收仍需按矩阵执行。两端构建与测试记录见仓库 Actions。

| 平台 | 目标能力 | 首版目标 |
| --- | --- | --- |
| Windows | 对原窗口设置置顶，继续正常点击和输入 | Windows 11 x64 |
| macOS | 系统选择窗口，将实时画面显示在悬浮面板，明确返回原窗口 | macOS 15.2+ |

屏幕画面在本机内存中处理，默认不录制、不保存、不上传。Mac 悬浮画面与 Windows 原窗口置顶的能力不同。

## 项目资料

- [完整产品与技术方案](方案/PinDesk跨平台窗口固定工具完整方案.docx)
- [协作与提交约定](CONTRIBUTING.md)

## Windows 使用

解压 Release 的整个 Windows ZIP，运行 `PinDesk.Windows.exe`，无需另装 .NET。预览版尚未做 Authenticode 签名。

- 选中需要固定的普通应用窗口，按 `Ctrl + Alt + P` 固定或解除。
- 双击托盘图标管理窗口；托盘菜单的“选择窗口固定”会打开管理列表。
- “设置”可修改快捷键和边框显示。关闭管理窗口后工具继续在托盘运行，选择“退出”会还原本工具修改的窗口状态。
- 保留目标原先已有的置顶状态；窗口自行取消置顶时不反复强制恢复。
- 默认普通用户权限；系统安全界面、独占全屏、管理员窗口和其他置顶工具的并发操作不属于通用兼容承诺。

恢复助手只在准备固定窗口时启动，等待主进程退出后尽力还原；不能保证系统同时终止所有进程时立即恢复。下次启动会重新检查恢复日志，身份不匹配的窗口不会被修改。

## Windows 开发

安装 .NET 10 SDK，在项目根目录执行：

```powershell
dotnet build apps/windows/PinDesk.Windows/PinDesk.Windows.csproj -c Release
dotnet run --project tests/PinDesk.Core.Tests -c Release
dotnet run --project tests/PinDesk.Windows.Integration -c Release
dotnet run --project apps/windows/PinDesk.Windows -c Release
```

Win32 集成测试需要有窗口激活权限的桌面会话。它会创建临时测试窗口，不测试或修改现有用户窗口。测试不能执行时报告失败，不将其标为已通过。

生成自包含 ZIP：`./scripts/package-windows.ps1 -Version 0.1.0-alpha.1`。

## macOS 使用与开发

Mac 版要求 macOS 15.2 或更新版本。解压 Mac ZIP 后打开 `PinDesk.app`；这是未使用 Developer ID 签名、未公证的开发预览，系统可能阻止启动。架构以下载文件名为准。

- 按 `Command + Option + P`，通过系统选择器选择一个窗口，最多同时显示 3 个参考浮窗。
- 浮窗提供暂停、继续、解除和回到原窗口。查看镜像不改变源窗口层级，也不转发鼠标或键盘输入。
- 回源操作按需请求辅助功能权限；无法唯一匹配窗口时明确提示，不猜测目标。
- 操作原窗口时镜像隐藏；离开源窗口后，取得新的有效画面再恢复显示。
- 画面授权使用系统窗口选择器的会话授权，不启动时自动枚举并捕获其他窗口；退出后不自动恢复捕获。

详见 [Mac 客户端说明](apps/macos/README.md)。在 Mac 上构建与测试：

```bash
cd apps/macos
swift build -c release
swift test --parallel
cd ../..
bash scripts/package-macos.sh 0.1.0-alpha.1
```

Mac 的自动测试覆盖模型与配置；系统授权、跨应用捕获和实际回源体验需要 Mac 实机验收，不能仅凭编译通过宣称全部验证完成。

## 开发约定

每个功能与修复使用独立分支；完成验证后提交详细的 commit 说明，创建 PR，检查通过后合入。初次创建空仓库所需的基础提交直接建立 `main`，后续功能均通过 PR。

Release 只包含通过相应构建和测试的产物。没有签名、公证或完整实机验证的版本必须标为开发预览，并写清限制。

## License

[MIT](LICENSE)

