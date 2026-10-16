import XCTest
@testable import PinDeskCore

final class WindowModelsTests: XCTestCase {
    func testCapturePreservesAspectAndCapsRetinaFrames() {
        let dimensions = CaptureDimensions(pointWidth: 2560, pointHeight: 1440, pixelScale: 2)
        XCTAssertEqual(dimensions, CaptureDimensions(pointWidth: 1920, pointHeight: 1080, pixelScale: 1))
        XCTAssertEqual(dimensions.width, 1920)
        XCTAssertEqual(dimensions.height, 1080)
    }

    func testPortraitAndInvalidDimensionsStayBounded() {
        let portrait = CaptureDimensions(pointWidth: 600, pointHeight: 2000, pixelScale: 2)
        XCTAssertEqual(portrait.width, 576)
        XCTAssertEqual(portrait.height, 1920)
        let invalid = CaptureDimensions(pointWidth: .nan, pointHeight: .infinity, pixelScale: .nan)
        XCTAssertEqual(invalid.width, 2)
        XCTAssertEqual(invalid.height, 2)
    }

    func testWindowMatchRequiresSameProcessTitleAndBounds() {
        let source = identity()
        XCTAssertEqual(WindowMatcher.uniqueMatch(source: source, candidates: [identity(pid: 99), source]), 1)
        XCTAssertNil(WindowMatcher.uniqueMatch(source: source, candidates: [identity(title: "Other")]))
        XCTAssertNil(WindowMatcher.uniqueMatch(source: source, candidates: [identity(x: 400)]))
    }

    func testAmbiguousAndMissingWindowNeverMatch() {
        let source = identity()
        XCTAssertNil(WindowMatcher.uniqueMatch(source: source, candidates: [source, source]))
        XCTAssertNil(WindowMatcher.uniqueMatch(source: source, candidates: []))
    }

    func testSmallCoordinateRoundingIsAllowed() {
        XCTAssertEqual(WindowMatcher.uniqueMatch(source: identity(), candidates: [identity(x: 103)]), 0)
    }

    func testPickerUpdatesForAnyExistingOrClosedStreamCannotCreateSession() {
        XCTAssertFalse(PickerUpdatePolicy.shouldCreateSession(hasAssociatedStream: true, isShuttingDown: false))
        XCTAssertFalse(PickerUpdatePolicy.shouldCreateSession(hasAssociatedStream: true, isShuttingDown: true))
        XCTAssertFalse(PickerUpdatePolicy.shouldCreateSession(hasAssociatedStream: false, isShuttingDown: true))
        XCTAssertTrue(PickerUpdatePolicy.shouldCreateSession(hasAssociatedStream: false, isShuttingDown: false))
    }

    func testReusedPIDAndMissingLaunchDateCannotMatchSourceProcess() {
        let firstLaunch = Date(timeIntervalSince1970: 100)
        let source = SourceProcessIdentity(processID: 42, launchDate: firstLaunch)
        XCTAssertTrue(source.matches(processID: 42, launchDate: firstLaunch))
        XCTAssertFalse(source.matches(processID: 42, launchDate: Date(timeIntervalSince1970: 200)))
        XCTAssertFalse(source.matches(processID: 99, launchDate: firstLaunch))
        XCTAssertFalse(source.matches(processID: 42, launchDate: nil))
    }

    private func identity(pid: Int32 = 42, title: String = "Document", x: Double = 100) -> WindowIdentity {
        WindowIdentity(processID: pid, title: title,
                       bounds: WindowBounds(x: x, y: 100, width: 800, height: 600))
    }
}
