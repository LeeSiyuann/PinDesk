import Foundation

/// A PID alone may be reused after the selected application's process exits.
public struct SourceProcessIdentity: Equatable, Sendable {
    public let processID: Int32
    public let launchDate: Date

    public init(processID: Int32, launchDate: Date) {
        self.processID = processID; self.launchDate = launchDate
    }

    public func matches(processID: Int32, launchDate: Date?) -> Bool {
        self.processID == processID && launchDate == self.launchDate
    }
}

public enum PickerUpdatePolicy {
    /// Existing-stream updates include late notifications for streams a user has already closed.
    /// Source changes are disabled, so only a genuinely new, unassociated selection may add a session.
    public static func shouldCreateSession(hasAssociatedStream: Bool, isShuttingDown: Bool) -> Bool {
        !hasAssociatedStream && !isShuttingDown
    }
}

/// Pure model code stays independent of AppKit so the conservative matching policy is testable.
public struct WindowBounds: Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x; self.y = y; self.width = width; self.height = height
    }

    public func approximatelyEquals(_ other: WindowBounds, tolerance: Double = 6) -> Bool {
        abs(x - other.x) <= tolerance && abs(y - other.y) <= tolerance &&
        abs(width - other.width) <= tolerance && abs(height - other.height) <= tolerance
    }
}

public struct WindowIdentity: Equatable, Sendable {
    public let processID: Int32
    public let title: String
    public let bounds: WindowBounds

    public init(processID: Int32, title: String, bounds: WindowBounds) {
        self.processID = processID; self.title = title; self.bounds = bounds
    }
}

public enum WindowMatcher {
    /// AX has no public stable CGWindowID bridge. Refuse ambiguity rather than activate the wrong window.
    public static func uniqueMatch(source: WindowIdentity, candidates: [WindowIdentity]) -> Int? {
        let matches = candidates.indices.filter { index in
            let candidate = candidates[index]
            return source.processID == candidate.processID && source.title == candidate.title &&
                source.bounds.approximatelyEquals(candidate.bounds)
        }
        return matches.count == 1 ? matches.first : nil
    }
}

public struct CaptureDimensions: Equatable, Sendable {
    public let width: Int
    public let height: Int

    /// Never upscale, cap the longest edge, and use even dimensions for efficient video surfaces.
    public init(pointWidth: Double, pointHeight: Double, pixelScale: Double, maximumEdge: Int = 1920) {
        let width = pointWidth.isFinite ? max(2, pointWidth) : 2
        let height = pointHeight.isFinite ? max(2, pointHeight) : 2
        let scale = pixelScale.isFinite ? max(1, pixelScale) : 1
        let limit = Double(max(2, maximumEdge))
        let factor = min(scale, limit / max(width, height))
        self.width = max(2, Int((width * factor).rounded(.down)) / 2 * 2)
        self.height = max(2, Int((height * factor).rounded(.down)) / 2 * 2)
    }
}
