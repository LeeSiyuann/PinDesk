import XCTest
@testable import PinDeskCore

final class PreferencesTests: XCTestCase {
    func testDefaultAndSupportedCombinationsValidate() throws {
        try PinDeskPreferences.defaults.validate()
        let combinations: [ShortcutModifiers] = [.control, .option, .shift, .command, [.control, .option, .shift, .command]]
        for modifiers in combinations {
            try PinDeskPreferences(shortcut: .init(letter: "a", modifiers: modifiers), framesPerSecond: 30).validate()
        }
        XCTAssertEqual(HotKeyPreference(letter: "p", modifiers: [.command, .option]).displayLabel, "⌥⌘P")
    }

    func testInvalidKeysAndModifiersAreRejected() {
        for letter in ["", "AB", "1", "中", "ß"] {
            XCTAssertThrowsError(try HotKeyPreference(letter: letter, modifiers: [.command]).validate()) {
                XCTAssertEqual($0 as? PreferenceValidationError, .invalidLetter)
            }
        }
        XCTAssertThrowsError(try HotKeyPreference(letter: "P", modifiers: []).validate()) {
            XCTAssertEqual($0 as? PreferenceValidationError, .missingModifier)
        }
        XCTAssertThrowsError(try HotKeyPreference(letter: "P", modifiers: .init(rawValue: 128)).validate()) {
            XCTAssertEqual($0 as? PreferenceValidationError, .invalidModifier)
        }
    }

    func testUnsupportedFrameRateIsRejected() {
        for frameRate in [-1, 0, 14, 60] {
            let preferences = PinDeskPreferences(shortcut: PinDeskPreferences.defaults.shortcut, framesPerSecond: frameRate)
            XCTAssertThrowsError(try preferences.validate()) {
                XCTAssertEqual($0 as? PreferenceValidationError, .unsupportedFrameRate)
            }
        }
    }

    func testSavedPreferencesRoundTripAndInvalidSavedValuesStillFailValidation() throws {
        let preferences = PinDeskPreferences(shortcut: .init(letter: "K", modifiers: [.control, .shift]), framesPerSecond: 30)
        XCTAssertEqual(try JSONDecoder().decode(PinDeskPreferences.self, from: JSONEncoder().encode(preferences)), preferences)
        // RawRepresentable + Codable encodes this OptionSet as its single UInt8 value,
        // not an object containing rawValue. Preserve that valid wire shape while corrupting only fps.
        let invalidJSON = #"{"shortcut":{"letter":"P","modifiers":8},"framesPerSecond":99}"#.data(using: .utf8)!
        let decoded = try JSONDecoder().decode(PinDeskPreferences.self, from: invalidJSON)
        XCTAssertThrowsError(try decoded.validate())
    }
}
