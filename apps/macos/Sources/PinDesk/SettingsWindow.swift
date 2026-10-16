import AppKit
import PinDeskCore
import SwiftUI

@MainActor
enum PreferencesStore {
    private static let key = "PinDesk.preferences.v1"

    static func load() -> PinDeskPreferences {
        guard let data = UserDefaults.standard.data(forKey: key),
              let preferences = try? JSONDecoder().decode(PinDeskPreferences.self, from: data),
              (try? preferences.validate()) != nil else { return .defaults }
        return preferences
    }

    static func save(_ preferences: PinDeskPreferences) throws {
        try preferences.validate()
        let data = try JSONEncoder().encode(preferences)
        UserDefaults.standard.set(data, forKey: key)
    }
}

@MainActor
final class SettingsWindowController: NSWindowController {
    init() {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 460),
                              styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "PinDesk 设置"
        window.isReleasedWhenClosed = false
        super.init(window: window)
        window.center()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is unsupported") }

    func present(coordinator: CaptureCoordinator) {
        // Recreate the draft from saved preferences whenever the settings window is opened.
        window?.contentView = NSHostingView(rootView: SettingsContent(coordinator: coordinator))
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }
}

@MainActor
private struct SettingsContent: View {
    @ObservedObject var coordinator: CaptureCoordinator
    @State private var letter: String
    @State private var modifiers: ShortcutModifiers
    @State private var framesPerSecond: Int
    @State private var feedback = ""
    @State private var saveSucceeded = false

    init(coordinator: CaptureCoordinator) {
        self.coordinator = coordinator
        let preferences = coordinator.preferences
        _letter = State(initialValue: preferences.shortcut.letter)
        _modifiers = State(initialValue: preferences.shortcut.modifiers)
        _framesPerSecond = State(initialValue: preferences.framesPerSecond)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("窗口选择快捷键").font(.headline)
            HStack(spacing: 12) {
                modifierToggle("Ctrl", .control)
                modifierToggle("Option", .option)
                modifierToggle("Shift", .shift)
                modifierToggle("Command", .command)
            }
            HStack {
                Text("字母")
                TextField("A–Z", text: $letter).textFieldStyle(.roundedBorder).frame(width: 80)
                    .onChange(of: letter) { _, value in letter = value.uppercased() }
                Text("至少一个修饰键 + 单个 A–Z 字母")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text("当前快捷键：\(coordinator.preferences.shortcut.displayLabel)")
                .font(.caption).foregroundStyle(.secondary)
            Divider()
            Picker("捕获帧率上限", selection: $framesPerSecond) {
                Text("15 fps · 节省资源").tag(15)
                Text("30 fps · 更流畅").tag(30)
            }
            Text("保存后用于新捕获与重新开始的捕获。已有实时窗口请暂停后继续，以应用新帧率；回源后恢复也会使用新帧率。静止画面按系统实际更新。")
                .font(.caption).foregroundStyle(.secondary)
            if !feedback.isEmpty {
                Text(feedback).font(.caption)
                    .foregroundStyle(saveSucceeded ? Color.secondary : Color.orange)
            }
            HStack {
                Text("设置保存在此 Mac，固定窗口列表不保存。")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("保存设置") { save() }.buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(width: 480)
    }

    private func modifierToggle(_ title: String, _ modifier: ShortcutModifiers) -> some View {
        Toggle(title, isOn: Binding(
            get: { modifiers.contains(modifier) },
            set: { enabled in
                if enabled { modifiers.insert(modifier) }
                else { modifiers.remove(modifier) }
            }))
            .toggleStyle(.checkbox)
    }

    private func save() {
        let preferences = PinDeskPreferences(shortcut: .init(letter: letter, modifiers: modifiers),
                                             framesPerSecond: framesPerSecond)
        if let failure = coordinator.savePreferences(preferences) {
            feedback = failure
            saveSucceeded = false
        } else {
            feedback = "已保存。快捷键立即生效；已有捕获暂停后继续，可应用新的帧率。"
            saveSucceeded = true
        }
    }
}
