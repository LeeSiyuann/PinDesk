import AppKit
import ApplicationServices
import PinDeskCore
import ScreenCaptureKit

enum SourceActivationResult {
    case success
    case failure(String)
}

@MainActor
enum SourceWindowActivator {
    static func activate(window: SCWindow, processIdentity: SourceProcessIdentity?) -> SourceActivationResult {
        guard let owner = window.owningApplication,
              let app = verifiedApplication(processID: owner.processID, identity: processIdentity) else {
            return .failure("源应用已退出、重新启动或身份无法核实。请解除后重新选择窗口；镜像仍可使用。")
        }
        // Ask only on an explicit return-to-source gesture. Denial never blocks the mirror.
        guard AXIsProcessTrusted() else {
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
            return .failure("请在系统设置 → 隐私与安全性 → 辅助功能中允许 PinDesk，然后重试。镜像仍可使用。")
        }
        guard let source = currentIdentity(of: window) else {
            return .failure("找不到所选源窗口，可能已关闭。请解除后重新选择。")
        }
        let application = AXUIElementCreateApplication(owner.processID)
        guard let candidates = accessibleWindows(application: application, processID: owner.processID) else {
            return .failure("此应用未提供完整的可访问窗口信息，无法可靠定位原窗口。请使用 Dock 或 Mission Control 返回。")
        }
        guard let index = WindowMatcher.uniqueMatch(source: source, candidates: candidates.map(\.identity)) else {
            return .failure("无法唯一匹配原窗口（标题、位置或大小已变化，或存在相同窗口）。请手动返回后重新选择。")
        }
        let target = candidates[index].element
        guard !app.isTerminated,
              processIdentity?.matches(processID: app.processIdentifier, launchDate: app.launchDate) == true else {
            return .failure("源应用身份已变化，请解除后重新选择窗口。")
        }
        // Do not activate the app before a unique match exists: another document might otherwise steal focus.
        _ = AXUIElementSetAttributeValue(target, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
        guard AXUIElementPerformAction(target, kAXRaiseAction as CFString) == .success else {
            return .failure("源应用拒绝提升该窗口，请手动切换到原窗口。")
        }
        _ = AXUIElementSetAttributeValue(application, kAXFocusedWindowAttribute as CFString, target)
        _ = AXUIElementSetAttributeValue(target, kAXMainAttribute as CFString, kCFBooleanTrue)
        guard app.activate(options: []) else {
            return .failure("原窗口已提升，但系统未允许切换应用。请手动激活源应用。")
        }
        return .success
    }

    /// Used only while the mirror is handed off to an already-authorized source window.
    /// nil means metadata/permission is no longer sufficient; restore the reference conservatively.
    static func isSourceFocused(window: SCWindow, processIdentity: SourceProcessIdentity?) -> Bool? {
        guard let owner = window.owningApplication else { return false }
        guard verifiedApplication(processID: owner.processID, identity: processIdentity) != nil else { return nil }
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier == owner.processID else { return false }
        guard AXIsProcessTrusted(), let source = currentIdentity(of: window) else { return nil }
        let application = AXUIElementCreateApplication(owner.processID)
        guard let candidates = accessibleWindows(application: application, processID: owner.processID),
              let index = WindowMatcher.uniqueMatch(source: source, candidates: candidates.map(\.identity)) else { return nil }
        var rawFocused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(application, kAXFocusedWindowAttribute as CFString, &rawFocused) == .success,
              let rawFocused, CFGetTypeID(rawFocused) == AXUIElementGetTypeID() else { return nil }
        let focused = rawFocused as! AXUIElement
        // Focus is proof only when the selected source has a unique match among every AX window.
        // Matching a one-element [focused] list would silently accept duplicated title/geometry.
        return CFEqual(focused, candidates[index].element)
    }

    private static func verifiedApplication(processID: Int32, identity: SourceProcessIdentity?) -> NSRunningApplication? {
        guard let identity, let app = NSRunningApplication(processIdentifier: processID), !app.isTerminated,
              identity.matches(processID: processID, launchDate: app.launchDate) else { return nil }
        return app
    }

    private static func accessibleWindows(application: AXUIElement, processID: Int32) -> [(element: AXUIElement, identity: WindowIdentity)]? {
        var rawWindows: CFTypeRef?
        guard AXUIElementCopyAttributeValue(application, kAXWindowsAttribute as CFString, &rawWindows) == .success,
              let windows = rawWindows as? [AXUIElement] else { return nil }
        var candidates: [(element: AXUIElement, identity: WindowIdentity)] = []
        for element in windows {
            // An unreadable peer window could be an ambiguous match. Do not silently exclude it.
            guard let frame = frame(of: element), let title = stringAttribute(element, kAXTitleAttribute) else { return nil }
            candidates.append((element, WindowIdentity(processID: processID, title: title, bounds: bounds(frame))))
        }
        return candidates
    }

    private static func currentIdentity(of window: SCWindow) -> WindowIdentity? {
        // Refresh only the already selected CGWindowID. This does not enumerate other app windows.
        guard let owner = window.owningApplication,
              let info = (CGWindowListCopyWindowInfo(.optionIncludingWindow, window.windowID) as? [[String: Any]])?.first,
              (info[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == owner.processID,
              let frameDictionary = info[kCGWindowBounds as String] as? [String: Any],
              let frame = CGRect(dictionaryRepresentation: frameDictionary as CFDictionary) else { return nil }
        let title = info[kCGWindowName as String] as? String ?? window.title ?? ""
        return WindowIdentity(processID: owner.processID, title: title, bounds: bounds(frame))
    }

    private static func stringAttribute(_ element: AXUIElement, _ name: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func frame(of element: AXUIElement) -> CGRect? {
        var rawPosition: CFTypeRef?
        var rawSize: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &rawPosition) == .success,
              AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &rawSize) == .success,
              let rawPosition, let rawSize,
              CFGetTypeID(rawPosition) == AXValueGetTypeID(), CFGetTypeID(rawSize) == AXValueGetTypeID() else { return nil }
        let positionValue = rawPosition as! AXValue
        let sizeValue = rawSize as! AXValue
        guard AXValueGetType(positionValue) == .cgPoint, AXValueGetType(sizeValue) == .cgSize else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(positionValue, .cgPoint, &position), AXValueGetValue(sizeValue, .cgSize, &size) else { return nil }
        return CGRect(origin: position, size: size)
    }

    private static func bounds(_ rect: CGRect) -> WindowBounds {
        WindowBounds(x: rect.origin.x, y: rect.origin.y, width: rect.width, height: rect.height)
    }
}
