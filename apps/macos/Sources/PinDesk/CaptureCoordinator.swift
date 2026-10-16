import AppKit
import PinDeskCore
import ScreenCaptureKit
import SwiftUI

@MainActor
final class CaptureCoordinator: NSObject, ObservableObject {
    static let shared = CaptureCoordinator()
    static let maximumSessions = 3

    @Published private(set) var sessions: [CaptureSession] = []
    @Published var notice = "从系统选择器中选择一个应用窗口。"
    @Published private(set) var isShuttingDown = false
    @Published private(set) var preferences = PreferencesStore.load()
    private var panels: [UUID: FloatingPanelController] = [:]
    private var hotKey: GlobalHotKey?
    private var workspaceObservers: [NSObjectProtocol] = []
    private var sleepingSessionIDs: Set<UUID> = []
    private var pendingStops: [UUID: Task<Void, Never>] = [:]
    private var installed = false
    private var settingsController: SettingsWindowController?

    func install() {
        guard !installed else { return }
        installed = true
        let picker = SCContentSharingPicker.shared
        var configuration = SCContentSharingPickerConfiguration()
        configuration.allowedPickerModes = [.singleWindow]
        configuration.allowsChangingSelectedContent = false
        if let bundleID = Bundle.main.bundleIdentifier { configuration.excludedBundleIDs = [bundleID] }
        picker.defaultConfiguration = configuration
        picker.maximumStreamCount = Self.maximumSessions
        picker.add(self)
        picker.isActive = true
        hotKey = GlobalHotKey(preference: preferences.shortcut) { [weak self] in self?.showPicker() }
        if hotKey?.isRegistered != true { notice = "已保存的快捷键被占用，请在设置中修改；仍可从菜单栏选择窗口。" }

        let center = NSWorkspace.shared.notificationCenter
        workspaceObservers.append(center.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.prepareForSleep() }
        })
        workspaceObservers.append(center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.resumeAfterSleep() }
        })
        workspaceObservers.append(NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.panels.values.forEach { $0.keepOnVisibleScreen() } }
        })
    }

    func showPicker() {
        guard !isShuttingDown else { return }
        guard sessions.count < Self.maximumSessions else {
            notice = "已达到 3 个窗口上限，请先解除一个窗口。"; return
        }
        SCContentSharingPicker.shared.present(using: .window)
    }

    private func add(filter: SCContentFilter) {
        guard !isShuttingDown else { return }
        guard filter.includedWindows.count == 1, let source = filter.includedWindows.first else {
            notice = "请选择单个窗口，当前选择无法创建可靠的窗口镜像。"; return
        }
        if let existing = sessions.first(where: { $0.windowID == source.windowID }) {
            notice = "这个窗口已固定。"; show(existing); return
        }
        guard sessions.count < Self.maximumSessions else { notice = "已达到窗口上限。"; return }
        if source.owningApplication?.processID == ProcessInfo.processInfo.processIdentifier {
            notice = "请选择其他应用的窗口。"; return
        }
        let session = CaptureSession(filter: filter, window: source, frameRateProvider: { [weak self] in
            self?.preferences.framesPerSecond ?? 15
        })
        let controller = FloatingPanelController(session: session) { [weak self, weak session] in
            guard let session else { return }; self?.close(session)
        }
        sessions.append(session)
        panels[session.id] = controller
        controller.showWindow(nil)
        controller.window?.orderFrontRegardless()
        session.start()
        notice = "已创建悬浮参考窗口。点击画面返回原窗口时，才需要辅助功能权限。"
    }

    func show(_ session: CaptureSession) {
        session.revealReference()
        if !session.isMirrorHidden { panels[session.id]?.window?.orderFrontRegardless() }
    }

    func showSettings() {
        guard !isShuttingDown else { return }
        if settingsController == nil { settingsController = SettingsWindowController() }
        settingsController?.present(coordinator: self)
    }

    /// Reserve the replacement first. A conflicting combination must never release the existing key
    /// or overwrite persisted preferences. The setting transaction is atomic from the user's view.
    func savePreferences(_ replacement: PinDeskPreferences) -> String? {
        do { try replacement.validate() }
        catch { return error.localizedDescription }
        var candidate: GlobalHotKey?
        if replacement.shortcut != preferences.shortcut || hotKey?.isRegistered != true {
            candidate = GlobalHotKey(preference: replacement.shortcut) { [weak self] in self?.showPicker() }
            guard candidate?.isRegistered == true else {
                return "快捷键 \(replacement.shortcut.displayLabel) 已被占用或系统不允许注册。原快捷键和已保存设置保持不变，请选择其他组合。"
            }
        }
        do { try PreferencesStore.save(replacement) }
        catch { return "保存设置失败：\(error.localizedDescription)。原设置仍然有效。" }
        if let candidate { hotKey = candidate }
        preferences = replacement
        notice = "设置已保存；已有捕获暂停后继续，可应用新的帧率。"
        return nil
    }

    func close(_ session: CaptureSession) {
        panels.removeValue(forKey: session.id)?.closeFromCoordinator()
        sessions.removeAll { $0.id == session.id }
        sleepingSessionIDs.remove(session.id)
        session.markClosed()
        let stopID = session.id
        pendingStops[stopID] = Task { @MainActor [weak self] in
            await session.stop()
            self?.pendingStops.removeValue(forKey: stopID)
        }
    }

    func closeAll() { Array(sessions).forEach { close($0) } }

    func shutdown() async {
        guard !isShuttingDown else { return }
        isShuttingDown = true
        SCContentSharingPicker.shared.remove(self)
        SCContentSharingPicker.shared.isActive = false
        hotKey = nil
        settingsController?.close()
        settingsController = nil
        closeAll()
        let stops = Array(pendingStops.values)
        for task in stops { await task.value }
        pendingStops.removeAll()
        for observer in workspaceObservers {
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
            NotificationCenter.default.removeObserver(observer)
        }
        workspaceObservers.removeAll()
    }

    private func prepareForSleep() {
        for session in sessions where session.state == .live || session.state == .starting {
            sleepingSessionIDs.insert(session.id)
            session.pause()
        }
    }

    private func resumeAfterSleep() {
        let ids = sleepingSessionIDs
        sleepingSessionIDs.removeAll()
        for session in sessions where ids.contains(session.id) { session.resume() }
    }
}

extension CaptureCoordinator: SCContentSharingPickerObserver {
    nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker, didCancelFor stream: SCStream?) {
        Task { @MainActor [weak self] in self?.notice = "已取消选择。" }
    }

    nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker, didUpdateWith filter: SCContentFilter, for stream: SCStream?) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            // The picker may send an update from Control Center for an existing stream.
            // Its per-stream configuration disables source changes, so never create a duplicate here.
            if let stream, self.sessions.contains(where: { $0.owns(stream) }) { return }
            self.add(filter: filter)
        }
    }

    nonisolated func contentSharingPickerStartDidFailWithError(_ error: Error) {
        Task { @MainActor [weak self] in
            self?.notice = "无法打开系统窗口选择器：\(error.localizedDescription)"
        }
    }
}
