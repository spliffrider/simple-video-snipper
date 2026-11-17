import AVFoundation
import Foundation
import ScreenCaptureKit

struct AudioOptions {
    var system = false
    var microphone = false
    var microphoneID: String?
}

@MainActor
final class NativeRecorder: NSObject {
    enum Event {
        case started
        case finishing
        case notice(String)
        case finished(Result<Void, Error>)
    }

    private let onEvent: (Event) -> Void
    private var stream: SCStream?
    private var output: SCRecordingOutput?
    private var monitor: Task<Void, Never>?
    private var startDeadline: Task<Void, Never>?
    private var finishDeadline: Task<Void, Never>?
    private var disconnectObserver: NSObjectProtocol?
    private var began = false
    private var stopping = false
    private var terminal = false

    init(onEvent: @escaping (Event) -> Void) {
        self.onEvent = onEvent
    }

    func start(
        source: CaptureSource, audio: AudioOptions, to destination: URL,
        includingOwnWindow windowID: CGWindowID? = nil
    ) async throws {
        var (filter, config) = try source.streamSetup()
        if case .area(let display, _) = source {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let ownApplications = content.applications.filter {
                $0.processID == ProcessInfo.processInfo.processIdentifier
            }
            let includedWindows = content.windows.filter { $0.windowID == windowID }
            filter = SCContentFilter(
                display: display, excludingApplications: ownApplications, exceptingWindows: includedWindows
            )
        }
        config.capturesAudio = audio.system
        config.excludesCurrentProcessAudio = true
        config.sampleRate = 48_000
        config.channelCount = 2
        config.captureMicrophone = audio.microphone
        config.microphoneCaptureDeviceID = audio.microphoneID

        let recordingConfig = SCRecordingOutputConfiguration()
        guard recordingConfig.availableOutputFileTypes.contains(.mp4),
              recordingConfig.availableVideoCodecTypes.contains(.h264) else {
            throw SnipperError.problem("ScreenCaptureKit could not provide H.264 MP4 on this Mac.")
        }
        recordingConfig.outputURL = destination
        recordingConfig.outputFileType = .mp4
        recordingConfig.videoCodecType = .h264

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        let output = SCRecordingOutput(configuration: recordingConfig, delegate: self)
        try stream.addRecordingOutput(output)
        self.output = output
        self.stream = stream
        startDeadline = Task { [weak self] in
            try? await Task.sleep(for: .seconds(15))
            guard !Task.isCancelled else { return }
            self?.finish(.failure(SnipperError.problem("ScreenCaptureKit did not begin recording in time.")))
        }

        do {
            try await stream.startCapture()
        } catch {
            startDeadline?.cancel()
            guard !terminal else { return }
            terminal = true
            self.stream = nil
            self.output = nil
            throw error
        }
        guard !terminal else { return }
        if let windowID = source.windowID {
            monitor = Task { [weak self] in
                var consecutiveMisses = 0
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(1))
                    guard !Task.isCancelled else { break }
                    guard let content = try? await SCShareableContent.excludingDesktopWindows(
                        true, onScreenWindowsOnly: true
                    ) else { continue }
                    let available = content.windows.contains { $0.windowID == windowID }
                    consecutiveMisses = available ? 0 : consecutiveMisses + 1
                    if consecutiveMisses >= 2 {
                        await self?.stop()
                        break
                    }
                }
            }
        }
        if audio.microphone {
            let micID = audio.microphoneID ?? AVCaptureDevice.default(for: .audio)?.uniqueID
            disconnectObserver = NotificationCenter.default.addObserver(
                forName: AVCaptureDevice.wasDisconnectedNotification, object: nil, queue: .main
            ) { [weak self] notification in
                guard let device = notification.object as? AVCaptureDevice, device.uniqueID == micID else { return }
                Task { @MainActor in self?.onEvent(.notice("The selected microphone disconnected.")) }
            }
        }
    }

    func stop() async {
        guard let stream, !terminal, !stopping else { return }
        stopping = true
        onEvent(.finishing)
        waitForFinalFile()
        do {
            try await stream.stopCapture()
        } catch {
            if let output {
                // The source may already have ended. Ask the writer to finish independently.
                try? stream.removeRecordingOutput(output)
            }
            if !began { finish(.failure(error)) }
        }
    }

    private func waitForFinalFile() {
        guard finishDeadline == nil else { return }
        finishDeadline = Task { [weak self] in
            try? await Task.sleep(for: .seconds(20))
            guard !Task.isCancelled else { return }
            self?.finish(.failure(SnipperError.problem("The recording did not finish writing in time.")))
        }
    }

    private func finish(_ result: Result<Void, Error>) {
        guard !terminal else { return }
        terminal = true
        monitor?.cancel()
        startDeadline?.cancel()
        finishDeadline?.cancel()
        if let disconnectObserver { NotificationCenter.default.removeObserver(disconnectObserver) }
        disconnectObserver = nil
        if !stopping, let stream {
            Task { try? await stream.stopCapture() }
        }
        stream = nil
        output = nil
        onEvent(.finished(result))
    }
}

extension NativeRecorder: SCRecordingOutputDelegate, SCStreamDelegate {
    nonisolated func recordingOutputDidStartRecording(_ recordingOutput: SCRecordingOutput) {
        Task { @MainActor in
            guard !terminal, !stopping else { return }
            began = true
            startDeadline?.cancel()
            onEvent(.started)
        }
    }

    nonisolated func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        Task { @MainActor in finish(.success(())) }
    }

    nonisolated func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        Task { @MainActor in finish(.failure(error)) }
    }

    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in
            guard !terminal, !stopping else { return }
            guard began else { finish(.failure(error)); return }
            stopping = true
            onEvent(.finishing)
            waitForFinalFile()
            if let output { try? stream.removeRecordingOutput(output) }
        }
    }
}
