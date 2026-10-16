import AppKit
import SwiftUI

@main
@MainActor
struct PinDeskApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var coordinator = CaptureCoordinator.shared

    var body: some Scene {
        MenuBarExtra("PinDesk", systemImage: "pin.fill") {
            MenuContent(coordinator: coordinator)
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var terminationStarted = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApplication.shared.setActivationPolicy(.accessory)
        CaptureCoordinator.shared.install()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !terminationStarted else { return .terminateLater }
        terminationStarted = true
        // Keep the process alive until asynchronous capture shutdown has completed.
        Task { @MainActor in
            await CaptureCoordinator.shared.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

private struct MenuContent: View {
    @ObservedObject var coordinator: CaptureCoordinator

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "pin.fill").foregroundStyle(.tint)
                Text("PinDesk").font(.headline)
                Spacer()
                Text("macOS 预览版").font(.caption).foregroundStyle(.secondary)
            }
            Text("将选定窗口的实时画面悬浮在桌面。点击画面可返回原窗口操作。")
                .font(.caption).foregroundStyle(.secondary)
            Button("选择窗口…   \(coordinator.preferences.shortcut.displayLabel)") { coordinator.showPicker() }
                .buttonStyle(.borderedProminent)
                .disabled(coordinator.sessions.count >= CaptureCoordinator.maximumSessions || coordinator.isShuttingDown)
            if !coordinator.notice.isEmpty {
                Text(coordinator.notice).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
            Divider()
            if coordinator.sessions.isEmpty {
                Text("尚未固定窗口").foregroundStyle(.secondary)
            }
            ForEach(coordinator.sessions) { session in
                SessionMenuRow(session: session, coordinator: coordinator)
            }
            if !coordinator.sessions.isEmpty {
                Button("解除全部") { coordinator.closeAll() }
            }
            Divider()
            Text("快捷键 \(coordinator.preferences.shortcut.displayLabel) 打开窗口选择器；最多同时悬浮 3 个窗口。")
                .font(.caption).foregroundStyle(.secondary)
            Text("画面仅在本机内存中处理，不保存、不上传。")
                .font(.caption).foregroundStyle(.secondary)
            Button("设置…") { coordinator.showSettings() }
            Button("退出 PinDesk") { NSApplication.shared.terminate(nil) }
        }
        .padding(16)
        .frame(width: 360)
    }
}

private struct SessionMenuRow: View {
    @ObservedObject var session: CaptureSession
    let coordinator: CaptureCoordinator

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(session.displayTitle).lineLimit(1)
            HStack {
                Text(session.state.label).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("显示") { coordinator.show(session) }
                Button(session.state == .paused ? "继续" : "暂停") { session.togglePause() }
                    .disabled(session.isBusy || session.state == .failed || session.state == .interacting)
                Button("解除") { coordinator.close(session) }
            }
        }
    }
}
