import Foundation

public struct ShortcutModifiers: OptionSet, Codable, Equatable, Sendable {
    public let rawValue: UInt8
    public init(rawValue: UInt8) { self.rawValue = rawValue }
    public static let control = Self(rawValue: 1 << 0)
    public static let option = Self(rawValue: 1 << 1)
    public static let shift = Self(rawValue: 1 << 2)
    public static let command = Self(rawValue: 1 << 3)
    public static let supported: Self = [.control, .option, .shift, .command]
}

public struct HotKeyPreference: Codable, Equatable, Sendable {
    public var letter: String
    public var modifiers: ShortcutModifiers

    public init(letter: String, modifiers: ShortcutModifiers) {
        self.letter = letter.uppercased()
        self.modifiers = modifiers
    }

    public var displayLabel: String {
        (modifiers.contains(.control) ? "⌃" : "") +
        (modifiers.contains(.option) ? "⌥" : "") +
        (modifiers.contains(.shift) ? "⇧" : "") +
        (modifiers.contains(.command) ? "⌘" : "") + letter
    }

    public func validate() throws {
        guard letter.unicodeScalars.count == 1, let scalar = letter.unicodeScalars.first,
              (65...90).contains(scalar.value) else { throw PreferenceValidationError.invalidLetter }
        guard !modifiers.isEmpty else { throw PreferenceValidationError.missingModifier }
        guard modifiers.subtracting(.supported).isEmpty else { throw PreferenceValidationError.invalidModifier }
    }
}

public struct PinDeskPreferences: Codable, Equatable, Sendable {
    public var shortcut: HotKeyPreference
    public var framesPerSecond: Int
    public static let defaults = Self(shortcut: HotKeyPreference(letter: "P", modifiers: [.command, .option]),
                                      framesPerSecond: 15)

    public init(shortcut: HotKeyPreference, framesPerSecond: Int) {
        self.shortcut = shortcut; self.framesPerSecond = framesPerSecond
    }

    public func validate() throws {
        try shortcut.validate()
        guard [15, 30].contains(framesPerSecond) else { throw PreferenceValidationError.unsupportedFrameRate }
    }
}

public enum PreferenceValidationError: LocalizedError, Equatable {
    case invalidLetter, missingModifier, invalidModifier, unsupportedFrameRate

    public var errorDescription: String? {
        switch self {
        case .invalidLetter: return "快捷键必须使用单个 A–Z 字母。"
        case .missingModifier: return "请至少选择 Ctrl、Option、Shift 或 Command 中的一项。"
        case .invalidModifier: return "快捷键包含不支持的修饰键。"
        case .unsupportedFrameRate: return "帧率只能选择 15 或 30 fps。"
        }
    }
}
