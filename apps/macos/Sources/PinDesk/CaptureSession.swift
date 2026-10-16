import AppKit
import AVFoundation
import CoreMedia
import CoreVideo
import PinDeskCore
import ScreenCaptureKit

enum CaptureState: Equatable {
    case starting, live, paused, interacting, failed, closed
    var label: String {
        switch self {
        case .starting: return "正在连接"
        case .live: return "实时镜像"
        case .paused: return "已暂停"
        case .interacting: return "正在操作原窗口"
        case .failed: return "捕获已停止"
        case .closed: return "已解除"
        }
    }
}

@MainActor
final class CaptureSession: ObservableObject, Identifiable {
    let id = UUID()
    let windowID: CGWindowID
    let source: SCWindow
    private let sourceProcessIdentity: SourceProcessIdentity?
    let displayLayer = AVSampleBufferDisplayLayer()
    @Published private(set) var state: CaptureState = .starting
    @Published private(set) var message = "正在等待首帧…"
    @Published private(set) var isBusy = false
    @Published private(set) var lastFrameDate: Date?
    @Published private(set) var sourceReturnMessage = ""
    @Published private(set) var isMirrorHidden = false
    private let filter: SCContentFilter
    private let frameRateProvider: @MainActor () -> Int
    private var stream: SCStream?
    private var frameOutput: FrameOutput?
    private var delegate: StreamDelegate?
    private var generation = 0
    private var closed = false
    private var lifecycleTask: Task<Void, Never>?
    private var sourceFocusTask: Task<Void, Never>?
    private var revealAfterNextFrame = false

    var displayTitle: String {
        let app = source.owningApplication?.applicationName ?? "应用"
        let title = source.title?.isEmpty == false ? source.title! : "无标题窗口"
        return "\(app) · \(title)"
    }

    init(filter: SCContentFilter, window: SCWindow, frameRateProvider: @escaping @MainActor () -> Int) {
        self.filter = filter; self.source = window; self.windowID = window.windowID
        if let processID = window.owningApplication?.processID,
           let launchDate = NSRunningApplication(processIdentifier: processID)?.launchDate {
            self.sourceProcessIdentity = SourceProcessIdentity(processID: processID, launchDate: launchDate)
        } else {
            self.sourceProcessIdentity = nil
        }
        self.frameRateProvider = frameRateProvider
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = NSColor.black.cgColor
    }

    func owns(_ candidate: SCStream) -> Bool { stream === candidate }

    func start() {
        guard !closed, !isBusy else { return }
        lifecycleTask = Task { @MainActor [weak self] in await self?.startCapture() }
    }

    func togglePause() { state == .paused ? resume() : pause() }

    func pause() {
        guard !closed, state != .paused else { return }
        suspendCapture(as: .paused, message: "捕获已暂停，当前显示最后一帧。")
    }

    private func suspendCapture(as newState: CaptureState, message newMessage: String) {
        generation += 1
        state = newState
        message = newMessage
        // Wait for an in-flight start before stopping: SCStream start/stop must not race.
        let previous = lifecycleTask
        lifecycleTask = Task { @MainActor [weak self] in
            await previous?.value
            guard let self else { return }
            await self.detachAndStop()
            self.isBusy = false
        }
    }

    func resume() {
        guard !closed, state == .paused || state == .interacting else { return }
        let previous = lifecycleTask
        lifecycleTask = Task { @MainActor [weak self] in
            await previous?.value
            guard let self, !self.closed, self.state == .paused || self.state == .interacting else { return }
            await self.startCapture()
        }
    }

    func markClosed() {
        closed = true
        generation += 1
        state = .closed
        frameOutput?.invalidate()
        sourceFocusTask?.cancel()
    }

    func stop() async {
        markClosed()
        await lifecycleTask?.value
        await detachAndStop()
        await clearDisplayedImage()
    }

    private func startCapture() async {
        guard !closed else { return }
        generation += 1
        let token = generation
        isBusy = true
        state = .starting
        message = "正在连接所选窗口…"
        lastFrameDate = nil
        await clearDisplayedImage()
        guard token == generation, !closed else { isBusy = false; return }

        let dimensions = CaptureDimensions(pointWidth: filter.contentRect.width,
                                           pointHeight: filter.contentRect.height,
                                           pixelScale: Double(filter.pointPixelScale))
        let configuration = SCStreamConfiguration()
        configuration.width = dimensions.width
        configuration.height = dimensions.height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(frameRateProvider()))
        configuration.queueDepth = 3
        configuration.showsCursor = false
        configuration.capturesAudio = false
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.scalesToFit = true
        configuration.preservesAspectRatio = true

        let output = FrameOutput { [weak self] sample in self?.render(sample, generation: token) }
        let bridge = StreamDelegate { [weak self] error in self?.captureFailed(error, generation: token) }
        let newStream = SCStream(filter: filter, configuration: configuration, delegate: bridge)
        frameOutput = output; delegate = bridge; stream = newStream
        do {
            try newStream.addStreamOutput(output, type: .screen, sampleHandlerQueue: output.queue)
            try await newStream.startCapture()
            guard token == generation, !closed else {
                output.invalidate()
                try? await newStream.stopCapture()
                if stream === newStream { stream = nil; frameOutput = nil; delegate = nil }
                isBusy = false
                return
            }
            // startCapture completing does not prove that a usable image exists. Only render()
            // promotes this session to live, including when a first frame arrives before this await.
            if state == .starting { message = "正在等待首帧…" }
        } catch {
            if token == generation, !closed { captureFailed(error, generation: token) }
            output.invalidate()
            if stream === newStream { stream = nil; frameOutput = nil; delegate = nil }
        }
        isBusy = false
    }

    private func detachAndStop() async {
        let oldStream = stream
        frameOutput?.invalidate()
        stream = nil; frameOutput = nil; delegate = nil
        if let oldStream { try? await oldStream.stopCapture() }
    }

    private func clearDisplayedImage() async {
        // The renderer is the supported enqueue/flush API on macOS 14+. Wait for the flush
        // completion before accepting a replacement stream's first frame.
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            displayLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: {
                continuation.resume()
            })
        }
    }

    private func render(_ sample: CMSampleBuffer, generation token: Int) {
        guard token == generation, !closed, state == .live || state == .starting else { return }
        let renderer = displayLayer.sampleBufferRenderer
        if renderer.status == .failed { renderer.flush() }
        guard renderer.isReadyForMoreMediaData else { return }
        // A reference view should show the newest image immediately, not build a playback timeline.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true) as? [NSMutableDictionary],
           let first = attachments.first { first[kCMSampleAttachmentKey_DisplayImmediately] = true }
        renderer.enqueue(sample)
        lastFrameDate = Date()
        state = .live
        message = ""
        if revealAfterNextFrame {
            revealAfterNextFrame = false
            isMirrorHidden = false
        }
    }

    private func captureFailed(_ error: Error, generation token: Int) {
        guard token == generation, !closed else { return }
        state = .failed
        message = "捕获已停止：\(error.localizedDescription)。请解除后重新选择窗口。"
        frameOutput?.invalidate()
        revealAfterNextFrame = false
        isMirrorHidden = false
        // Release the capture task after a system stop, including revoked permissions / closed sources.
        let previous = lifecycleTask
        lifecycleTask = Task { @MainActor [weak self] in
            await previous?.value
            await self?.detachAndStop()
        }
    }

    func returnToSource() {
        guard !closed else { return }
        switch SourceWindowActivator.activate(window: source, processIdentity: sourceProcessIdentity) {
        case .success:
            sourceReturnMessage = ""
            isMirrorHidden = true
            suspendCapture(as: .interacting, message: "正在原窗口中操作；离开后自动恢复参考画面。")
            observeSourceFocus()
        case .failure(let explanation): sourceReturnMessage = explanation
        }
    }

    /// A user can explicitly bring back a handed-off reference from the menu bar.
    func revealReference() {
        guard !closed else { return }
        if state == .interacting { restoreAfterSourceInteraction() }
        else if !revealAfterNextFrame { isMirrorHidden = false }
    }

    private func observeSourceFocus() {
        sourceFocusTask?.cancel()
        sourceFocusTask = Task { @MainActor [weak self] in
            // Activating an application is asynchronous. Let focus settle before examining it.
            try? await Task.sleep(nanoseconds: 500_000_000)
            while !Task.isCancelled {
                guard let self, !self.closed, self.state == .interacting else { return }
                // Check the focused AX window as well as the application so switching documents
                // within the same application also restores the reference.
                if SourceWindowActivator.isSourceFocused(window: self.source, processIdentity: self.sourceProcessIdentity) != true {
                    self.restoreAfterSourceInteraction()
                    return
                }
                try? await Task.sleep(nanoseconds: 350_000_000)
            }
        }
    }

    private func restoreAfterSourceInteraction() {
        sourceFocusTask?.cancel()
        sourceFocusTask = nil
        revealAfterNextFrame = true
        // The panel stays hidden until the replacement stream supplies a valid complete frame.
        resume()
    }
}

/// ScreenCaptureKit calls on its own serial queue. At most one sample is dispatched to the UI queue;
/// this prevents a stalled UI from accumulating unbounded retained screen frames.
private final class FrameOutput: NSObject, SCStreamOutput {
    let queue = DispatchQueue(label: "app.pindesk.capture.frames", qos: .userInteractive)
    private let lock = NSLock()
    private var pending = false
    private var active = true
    private let onSample: @MainActor (CMSampleBuffer) -> Void

    init(onSample: @escaping @MainActor (CMSampleBuffer) -> Void) { self.onSample = onSample }

    func invalidate() { lock.lock(); active = false; lock.unlock() }

    private func isActive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return active
    }

    private func finishPendingDelivery() {
        lock.lock(); pending = false; lock.unlock()
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, CMSampleBufferIsValid(sampleBuffer), CMSampleBufferGetImageBuffer(sampleBuffer) != nil,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete else { return }
        lock.lock()
        guard active, !pending else { lock.unlock(); return }
        pending = true
        lock.unlock()
        Task { @MainActor [weak self] in
            guard let self else { return }
            if self.isActive() { self.onSample(sampleBuffer) }
            self.finishPendingDelivery()
        }
    }
}

private final class StreamDelegate: NSObject, SCStreamDelegate {
    private let onError: @MainActor (Error) -> Void
    init(onError: @escaping @MainActor (Error) -> Void) { self.onError = onError }
    func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor [onError] in onError(error) }
    }
}
