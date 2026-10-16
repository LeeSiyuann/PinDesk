import AppKit
import AVFoundation
import Combine
import SwiftUI

@MainActor
final class FloatingPanelController: NSWindowController, NSWindowDelegate {
    private var onClose: (() -> Void)?
    private var coordinatorClosing = false
    private var visibilityObservation: AnyCancellable?

    init(session: CaptureSession, onClose: @escaping () -> Void) {
        self.onClose = onClose
        let sourceSize = session.source.frame.size
        let width = min(640, max(320, sourceSize.width))
        let ratio = max(0.25, min(4, sourceSize.height / max(1, sourceSize.width)))
        let panel = ReferencePanel(contentRect: NSRect(x: 0, y: 0, width: width, height: width * ratio + 88),
                                   styleMask: [.titled, .closable, .resizable, .nonactivatingPanel],
                                   backing: .buffered, defer: false)
        panel.title = session.displayTitle
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.isFloatingPanel = true
        panel.minSize = NSSize(width: 300, height: 220)
        panel.contentView = NSHostingView(rootView: FloatingContent(session: session, onClose: onClose))
        super.init(window: panel)
        panel.delegate = self
        panel.center()
        keepOnVisibleScreen()
        visibilityObservation = session.$isMirrorHidden.removeDuplicates().sink { [weak self] hidden in
            if hidden { self?.window?.orderOut(nil) }
            else { self?.window?.orderFrontRegardless() }
        }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is unsupported") }

    func windowWillClose(_ notification: Notification) {
        if !coordinatorClosing { onClose?() }
    }

    func closeFromCoordinator() {
        coordinatorClosing = true
        window?.close()
        visibilityObservation = nil
        onClose = nil
    }

    func keepOnVisibleScreen() {
        guard let window, let screen = window.screen ?? NSScreen.main else { return }
        let visible = screen.visibleFrame
        var frame = window.frame
        frame.size.width = min(frame.width, visible.width)
        frame.size.height = min(frame.height, visible.height)
        frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
        frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
        window.setFrame(frame, display: true)
    }
}

private final class ReferencePanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private struct FloatingContent: View {
    @ObservedObject var session: CaptureSession
    let onClose: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Label(session.state.label, systemImage: "pin.fill").font(.caption)
                Spacer()
                Button("操作原窗口") { session.returnToSource() }
                Button(session.state == .paused ? "继续" : "暂停") { session.togglePause() }
                    .disabled(session.isBusy || session.state == .failed || session.state == .interacting)
                Button("解除", action: onClose)
            }
            .padding(8)
            ZStack {
                CaptureSurface(displayLayer: session.displayLayer, onClick: { session.returnToSource() })
                if !session.message.isEmpty {
                    Text(session.message)
                        .font(.caption).padding(10)
                        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 8))
                        .padding(16).allowsHitTesting(false)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            VStack(alignment: .leading, spacing: 3) {
                Text("点击画面仅返回原窗口；请在原窗口中继续操作。")
                if !session.sourceReturnMessage.isEmpty {
                    Text(session.sourceReturnMessage).foregroundStyle(.orange)
                }
            }
            .font(.caption2).foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        }
    }
}

private struct CaptureSurface: NSViewRepresentable {
    let displayLayer: AVSampleBufferDisplayLayer
    let onClick: () -> Void

    func makeNSView(context: Context) -> VideoView {
        let view = VideoView(displayLayer: displayLayer)
        view.onClick = onClick
        return view
    }

    func updateNSView(_ nsView: VideoView, context: Context) { nsView.onClick = onClick }
}

private final class VideoView: NSView {
    let displayLayer: AVSampleBufferDisplayLayer
    var onClick: (() -> Void)?
    init(displayLayer: AVSampleBufferDisplayLayer) {
        self.displayLayer = displayLayer
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.addSublayer(displayLayer)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unsupported") }
    override func layout() { super.layout(); displayLayer.frame = bounds }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { onClick?() }
}
