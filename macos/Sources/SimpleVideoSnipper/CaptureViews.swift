import AppKit
import AVFoundation
import ScreenCaptureKit
import SwiftUI

@MainActor
final class SnipperPanel: NSPanel {
    var onClose: (() -> Void)?

    init<Content: View>(
        _ title: String, size: CGSize, floating: Bool = false, @ViewBuilder content: () -> Content
    ) {
        let style: NSWindow.StyleMask = floating
            ? [.titled, .nonactivatingPanel] : [.titled, .closable]
        super.init(contentRect: CGRect(origin: .zero, size: size), styleMask: style, backing: .buffered, defer: false)
        self.title = title
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        contentView = NSHostingView(rootView: content().frame(width: size.width, height: size.height))
        if floating {
            level = .floating
            collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        } else {
            center()
        }
    }

    func present() {
        NSApp.activate(ignoringOtherApps: true)
        makeKeyAndOrderFront(nil)
    }

    override func close() {
        let callback = onClose
        onClose = nil
        super.close()
        callback?()
    }
}

struct WindowSelectionView: View {
    let windows: [SCWindow]
    let onSelect: (SCWindow) -> Void
    let onCancel: () -> Void

    @State private var query = ""
    @State private var selected: CGWindowID?

    private var filtered: [SCWindow] {
        guard !query.isEmpty else { return windows }
        return windows.filter {
            ($0.title ?? "").localizedCaseInsensitiveContains(query)
                || ($0.owningApplication?.applicationName ?? "").localizedCaseInsensitiveContains(query)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            TextField("Find a window", text: $query)
                .textFieldStyle(.roundedBorder)
            if filtered.isEmpty {
                ContentUnavailableView("No windows found", systemImage: "macwindow")
            } else {
                List(filtered, id: \.windowID, selection: $selected) { window in
                    VStack(alignment: .leading, spacing: 3) {
                        Text(window.title.flatMap { $0.isEmpty ? nil : $0 } ?? "Untitled window")
                        Text(window.owningApplication?.applicationName ?? "")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .lineLimit(1)
                    .padding(.vertical, 3)
                    .tag(window.windowID)
                    .contentShape(Rectangle())
                    .onTapGesture(count: 2) { onSelect(window) }
                }
            }
            HStack {
                Spacer()
                Button("Cancel", action: onCancel).keyboardShortcut(.cancelAction)
                Button("Continue") {
                    if let selectedWindow { onSelect(selectedWindow) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selectedWindow == nil)
            }
        }
        .padding(18)
        .onAppear { selected = windows.first?.windowID }
        .onChange(of: query) { _, _ in
            if selectedWindow == nil { selected = filtered.first?.windowID }
        }
    }

    private var selectedWindow: SCWindow? { filtered.first { $0.windowID == selected } }
}

struct AudioConfigurationView: View {
    let title: String
    let start: (AudioOptions) -> Void
    let cancel: () -> Void

    @State private var systemAudio = false
    @State private var microphone = false
    @State private var selectedMic = ""
    @StateObject private var meter = MicrophoneMeter()

    private var meterKey: String? { microphone ? selectedMic : nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(title).font(.title2.weight(.semibold))
            Toggle("System audio", isOn: $systemAudio)
            Toggle("Microphone", isOn: $microphone)
            if microphone {
                Picker("Input", selection: $selectedMic) {
                    Text("System default").tag("")
                    ForEach(meter.devices, id: \.uniqueID) { device in
                        Text(device.localizedName).tag(device.uniqueID)
                    }
                }
                ProgressView("Microphone level", value: meter.level)
                    .font(.caption)
                if let error = meter.error {
                    Text(error).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            HStack {
                Spacer()
                Button("Cancel") {
                    meter.stop()
                    cancel()
                }
                .keyboardShortcut(.cancelAction)
                Button("Record") {
                    meter.stop()
                    start(AudioOptions(system: systemAudio, microphone: microphone,
                                       microphoneID: selectedMic.isEmpty ? nil : selectedMic))
                }
                .keyboardShortcut(.defaultAction)
                .disabled(microphone && !meter.ready)
            }
        }
        .padding(20)
        .task(id: meterKey) {
            meter.stop()
            if let meterKey { await meter.start(deviceID: meterKey) }
        }
        .onDisappear { meter.stop() }
    }
}

@MainActor
private final class MicrophoneMeter: ObservableObject {
    @Published private(set) var devices = [AVCaptureDevice]()
    @Published private(set) var level = 0.0
    @Published private(set) var ready = false
    @Published private(set) var error: String?

    private let input = MeterInput()
    private var generation = UUID()

    func start(deviceID: String) async {
        let current = generation
        let authorized = await AVCaptureDevice.requestAccess(for: .audio)
        guard current == generation, !Task.isCancelled else { return }
        guard authorized else {
            error = "Allow microphone access in System Settings → Privacy & Security."
            return
        }
        devices = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.microphone, .external], mediaType: .audio, position: .unspecified
        ).devices
        guard let device = deviceID.isEmpty
                ? AVCaptureDevice.default(for: .audio) : devices.first(where: { $0.uniqueID == deviceID }) else {
            error = "The selected microphone is unavailable."
            return
        }
        input.start(device: device, update: { [weak self] level in
            DispatchQueue.main.async {
                guard self?.generation == current else { return }
                self?.level = level
            }
        }, prepared: { [weak self] result in
            DispatchQueue.main.async {
                guard self?.generation == current else { return }
                switch result {
                case .success: self?.ready = true
                case .failure(let error): self?.error = error.localizedDescription
                }
            }
        })
    }

    func stop() {
        generation = UUID()
        ready = false
        level = 0
        error = nil
        input.stop()
    }
}

private final class MeterInput: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    private let queue = DispatchQueue(label: "SimpleVideoSnipper.MicrophoneMeter")
    private var session: AVCaptureSession?
    private var update: ((Double) -> Void)?
    private var lastSample: TimeInterval = 0

    func start(device: AVCaptureDevice, update: @escaping (Double) -> Void,
               prepared: @escaping (Result<Void, Error>) -> Void) {
        queue.async { [self] in
            do {
                let session = AVCaptureSession()
                let input = try AVCaptureDeviceInput(device: device)
                let output = AVCaptureAudioDataOutput()
                guard session.canAddInput(input), session.canAddOutput(output) else {
                    throw SnipperError.problem("Could not monitor this microphone.")
                }
                session.addInput(input)
                session.addOutput(output)
                self.update = update
                output.setSampleBufferDelegate(self, queue: queue)
                session.startRunning()
                guard session.isRunning else {
                    throw SnipperError.problem("Could not start the microphone meter.")
                }
                self.session = session
                prepared(.success(()))
            } catch {
                prepared(.failure(error))
            }
        }
    }

    func stop() {
        queue.async { [self] in
            session?.stopRunning()
            session = nil
            update = nil
        }
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastSample > 0.05 else { return }
        lastSample = now
        let decibels = Double(connection.audioChannels.map(\.averagePowerLevel).max() ?? -70)
        update?(max(0, min(1, (decibels + 55) / 55)))
    }
}

struct RecordingControls: View {
    @ObservedObject var model: SnipperModel

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 14) {
                Circle()
                    .fill(model.phase == .recording ? .red : .secondary)
                    .frame(width: 9, height: 9)
                    .accessibilityHidden(true)
                if let start = model.recordingStarted, model.phase == .recording {
                    TimelineView(.periodic(from: start, by: 1)) { context in
                        let seconds = max(0, Int(context.date.timeIntervalSince(start)))
                        Text(String(format: "%02d:%02d", seconds / 60, seconds % 60))
                            .font(.headline.monospacedDigit())
                            .accessibilityLabel("Recording elapsed time")
                    }
                } else {
                    Text(model.phase.label)
                }
                Spacer()
                Button("Stop recording") { model.stop() }
                    .disabled(model.phase != .recording)
            }
            if let notice = model.captureNotice {
                Text(notice).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 14)
    }
}
