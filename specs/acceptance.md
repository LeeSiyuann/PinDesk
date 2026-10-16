# PinDesk acceptance evidence

Automated checks and manual acceptance answer different questions. A successful workflow must never be described as proof that interactive window management works on every application or display configuration.

## Automated gates

| Platform | Required check | What it establishes |
| --- | --- | --- |
| Windows | Release build of `apps/windows/PinDesk.Windows/PinDesk.Windows.csproj` | The client and native declarations compile against .NET 10. |
| Windows | Console runner in `tests/PinDesk.Core.Tests` | State-management behavior and restoration decisions pass without an external test framework. |
| Windows | Console runner in `tests/PinDesk.Windows.Integration` | Real Win32 window-management behavior passes for the windows created by the test process. See runner output for exact assertions. |
| macOS | `swift build --configuration release` | Swift/AppKit/ScreenCaptureKit code compiles with the macOS runner SDK. |
| macOS | `swift test --parallel` | The behavioral tests supplied by the package pass. This does not grant screen-recording or Accessibility permission. |
| Both | Package generated only after checks pass | The archive can be produced from the same source revision. This does not establish signing or notarization. |

GitHub-hosted Windows runners may have different desktop access from a signed-in user's session. A native integration runner must fail explicitly when its required operation fails; it must not silently substitute a mock or report skipped behavior as passed. The build log is the record of the operations actually exercised.

## Recorded automated evidence

On 2026-10-16, the Windows implementation at commit `941453d30281306905e98df6f5b3575b2b33d1c0` passed a local .NET SDK 10.0.402 Release build with zero warnings/errors, 22 core checks, eight real Win32 integration checks, an offscreen WPF rendering/exit smoke test and a self-contained package startup smoke test. Native integration needed a desktop session outside the execution sandbox. The self-contained package rendered the dashboard without requiring the machine's installed .NET 9 runtime.

[PR 1 CI run](https://github.com/LeeSiyuann/PinDesk/actions/runs/38909544326) independently passed the Windows Release build, core checks, native integration checks and packaging. These controlled-window checks do not fill in the broader application and display matrix below.

[PR 2 final CI run](https://github.com/LeeSiyuann/PinDesk/actions/runs/38910772554), head `05155a3f8950a302f0f7551227c68484e20ac0ec`, passed macOS arm64 compilation, all 11 XCTest model/configuration checks, packaging and Windows regression checks. Earlier Mac runs failed on a Carbon parameter type and an invalid test JSON fixture; those failures were corrected before merge rather than skipped.

[PR 3 CI run](https://github.com/LeeSiyuann/PinDesk/actions/runs/38910853121), head `d3cb19be4a9dad33a2b4b0aae71f5360d79beff3`, passed all nine native Windows checks, including invoking the actual WPF recovery executable after its owner process had already exited. The same nine checks passed locally.

## Real-device checklist

Record OS version, CPU architecture, display count/scaling, application versions, tested source commit, date, and observed result. Use `Not tested`, `Pass`, or `Fail`; leave unexecuted items as `Not tested`.

| Case | Windows 11 x64 | macOS 15.2+ Apple Silicon |
| --- | --- | --- |
| Open and exit app; tray/menu-bar action remains reachable | Not tested | Not tested |
| Fixed window stays visible while another ordinary app has focus | Not tested | Not tested |
| Release a single fixed window and release all windows | Not tested | Not tested |
| Fix two windows, interact with another app, verify focus is preserved | Not tested | Not tested |
| Existing topmost status is respected when releasing a source window | Not tested | Not applicable to preview |
| Normal app exit restores owned native topmost changes | Not tested | Not applicable to preview |
| Forced process exit triggers recovery behavior where implemented | Not tested | Not applicable to preview |
| Source window closed or minimized while fixed | Not tested | Not tested |
| Source moved/resized; external display disconnected | Not tested | Not tested |
| Mixed display scaling; suspend and resume | Not tested | Not tested |
| Window whose process has higher privileges | Not tested | Not applicable |
| Grant, deny, and revoke screen-recording permission | Not applicable | Not tested |
| Grant, deny, and revoke Accessibility permission | Not applicable | Not tested |
| Preview updates when source is fully occluded | Not applicable | Not tested |
| Return to original window without accidental click-through | Not applicable | Not tested |
| Chinese input and selection after returning to original window | Not tested | Not tested |
| Full-screen applications and Spaces behavior documented from observation | Not tested | Not tested |

Repeat the applicable cases with a browser, text editor/IDE, PDF reader and messaging app. Protected/DRM content may refuse capture and is outside preview compatibility promises. The utility does not bypass the secure desktop or security permission prompts.

## Promotion rule

Initial artifacts are preview builds. Publish prereleases until the relevant real-device cases have recorded results and material defects are resolved. A macOS compilation result is not a claim of a tested macOS user experience. Signed distribution additionally requires an actual configured Windows signing certificate or Apple Developer ID plus notarization, neither of which is implied by this repository's ZIP packaging.
