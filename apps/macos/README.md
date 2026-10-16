# PinDesk for macOS

macOS 15.2+ 的原生菜单栏开发预览版。它把系统选择器授权的**单个窗口画面**放进可缩放悬浮窗口，最多同时 3 个。镜像用于参考，交互必须返回源窗口；该实现不修改第三方应用窗口层级，也不向其他应用注入或转发鼠标、键盘输入。

## 构建与运行

需要 macOS 15.2+ 与 Xcode 16.2+（包含 macOS 15.2 SDK）。依赖全部来自系统框架，无第三方包：

```sh
swift build --package-path apps/macos -c release
swift test --package-path apps/macos
bash scripts/package-macos.sh 0.1.0
```

生成 `dist/PinDesk-macos-arm64-0.1.0.zip` 或 `dist/PinDesk-macos-x86_64-0.1.0.zip`，取决于构建机器。解压后将 `PinDesk.app` 放在固定位置运行，以减少权限身份变化。打包产物是开发预览，未使用 Developer ID 签名，未公证；系统可能阻止第一次运行。仅在确认文件来源后，使用 macOS 提供的“隐私与安全性 → 仍要打开”流程。此仓库没有 Apple 签名证书，不能提供已公证安装包。

## 使用流程

1. 点击菜单栏图钉，选择“选择窗口…”，或按默认快捷键 `Command + Option + P`。
2. 在系统窗口选择器中选择一个第三方应用窗口。取消时不会创建会话。
3. 悬浮参考窗口显示该窗口的实时画面；失去输入焦点后仍显示。可以移动、缩放，重复选择可创建多个参考窗口。
4. 点击画面或“操作原窗口”，返回源窗口，同时隐藏镜像并暂停捕获；切换到其他应用或同一应用的其他窗口后，自动重新捕获，在收到有效画面后恢复镜像。首次回源时按需申请辅助功能权限；允许后重试。拒绝辅助功能权限仍可观看画面。菜单栏“显示”可手动结束交接并恢复参考。
5. “暂停”停止捕获并保留最后一帧，“继续”重新开始；“解除”或关闭悬浮窗口释放捕获。菜单栏“解除全部”关闭所有会话。
6. 菜单栏“设置…”可修改快捷键与捕获帧率，保存后在此 Mac 的 UserDefaults 中保留。快捷键支持 Ctrl、Option、Shift、Command 中一个或多个加单个 A–Z 字母；冲突时不会释放旧快捷键或覆盖已保存设置。设置窗口可正常输入，镜像窗口继续保持不抢焦点。
7. 捕获帧率上限支持 15 / 30 fps，保存后用于新捕获、暂停后继续以及回源后的恢复；已有实时镜像需“暂停 → 继续”应用新帧率。没有新增捕获权限请求。
8. 退出时等待捕获停止，再结束进程。睡眠时暂停，唤醒时只恢复睡前运行的会话。外接屏变化时将悬浮窗口移回可见屏幕。

启动时如果已保存的快捷键被其他软件占用，菜单栏会提示，可从设置选择其他组合；仍可通过菜单选择窗口。当前预览版未实现开机启动、云同步或固定窗口列表恢复；退出后不会自动恢复已固定窗口。最多同时悬浮 3 个窗口，上限在菜单中明确显示。

## 权限与隐私

- 使用 `SCContentSharingPicker` 让系统管理窗口选择和捕获授权，不主动申请整个屏幕的访问权限。系统可能显示屏幕共享指示器或授权提示。
- 仅在用户明确点击“操作原窗口”时调用辅助功能权限提示。无权限不会阻止镜像。
- 捕获画面仅保留在本机内存，不写截图、视频、音频，不访问网络；不采集音频，默认不显示鼠标。
- 返回源窗口只使用公共 AX API，绑定会话创建时源进程的 PID 与启动时间，避免进程退出后 PID 复用定位到新应用；按已选窗口的 PID、标题和边界在全部 AX 窗口中唯一匹配。交接期间也对全部 AX 窗口验证唯一性，再对比焦点 AX 元素。出现权限缺失、源窗口关闭、标题缺失、边界无法读取、进程身份变化或多个候选时，明确失败或保守恢复参考，用户可手动切换。不会猜测其他窗口，也不使用私有 CGWindowID → AX 桥接。

## 实现与限制

- 捕获设置：默认 15 fps，可设为 30 fps；最长边 1920 像素、队列深度 3。该帧率是上限，静止窗口按系统实际内容更新。
- 输出帧来自 ScreenCaptureKit 的独立串行队列；单帧待处理门闩限制 UI 队列持帧量。所有 AppKit 和 AVSampleBufferDisplayLayer 操作在主线程执行。
- NSPanel 使用 `.nonactivatingPanel` 和 `.floating`，不成为 key/main window。该层级不能保证覆盖系统安全界面、独占全屏应用或其他高层级窗口。
- 收到首个有效完整帧前状态保持“正在连接”；系统报告静止帧（`SCFrameStatus.idle`）时保留现有画面，静止是正常状态，不因长时间无内容变化而误报异常。暂停或捕获失败时显示明确状态。
- 成功回源后隐藏镜像并停止捕获，只在交接期间每 350 毫秒检查前台应用与源应用的 AX 焦点窗口，不监听输入事件。离开源窗口后重新捕获，首个有效帧到达后才恢复显示；如重建捕获失败则显示错误状态。焦点信息失效或权限被撤销时保守恢复参考，用户仍可从菜单解除。
- 源窗口最小化、关闭、受保护视频、权限撤销等情况依赖系统捕获结果，可能冻结、黑屏或停止。停止后先解除并重新选择，不自动扩大捕获范围。
- 源窗口改变标题后，如果系统无法读取新标题，回源匹配会失败；返回源窗口的点击不会落到源应用按钮上。
- 当前版本只构建本机架构，不是 Universal Binary。Apple Silicon 与 Intel 都需要分别进行实机验收。

## 验证状态

实现环境为 Windows，无法在此环境执行 Swift/macOS 构建或实机 UI 测试。macOS CI 会编译并运行纯模型测试，但 CI 通过不能替代屏幕授权、焦点、睡眠唤醒、中文输入、多屏或实际应用兼容性验收。请以根目录发布说明与测试记录中的实际结果为准。

`PinDeskCoreTests` 验证捕获尺寸上限与异常输入、按 PID/标题/边界匹配、候选缺失与歧义拒绝策略、源进程身份与 PID 复用拒绝、旧捕获流的迟到 Picker 更新拒绝，以及快捷键组合校验、帧率校验、配置编码往返与损坏配置拒绝。快捷键系统冲突与 UserDefaults 实际保存需在 Mac 上验收；这些模型测试不伪装成系统 UI 测试。

## 官方接口参考

- [SCContentSharingPicker](https://developer.apple.com/documentation/screencapturekit/sccontentsharingpicker)
- [SCContentFilter.includedWindows](https://developer.apple.com/documentation/screencapturekit/sccontentfilter/includedwindows)（macOS 15.2+）
- [ScreenCaptureKit 捕获示例](https://developer.apple.com/documentation/screencapturekit/capturing-screen-content-in-macos)
- [AXUIElement](https://developer.apple.com/documentation/applicationservices/axuielement)
- [NSPanel](https://developer.apple.com/documentation/appkit/nspanel)
