import AppKit
import AVFoundation
import KeyboardShortcuts
import ScreenCaptureKit
import SwiftUI

@MainActor
final class SnipperModel: ObservableObject {
    enum Phase: Equatable {
        case idle, selecting, starting, browser, recording, finishing, saving

        var label: String {
            switch self {
            case .idle: "Ready"
            case .selecting: "Selecting a source"
            case .starting: "Starting…"
            case .browser: "Choose a tab in Chrome"
            case .recording: "Recording"
            case .finishing: "Finishing…"
            case .saving: "Saving…"
            }
        }
    }

    @Published private(set) var phase = Phase.idle
    @Published private(set) var recordingStarted: Date?
    @Published private(set) var stagingPath = ""
    @Published private(set) var exportPath = ""
    @Published private(set) var captureNotice: String?

    private let storage = RecordingStorage()
    private let defaults = UserDefaults.standard
    private let areaSelection = AreaSelection()
    private var interaction: SnipperPanel?
    private var settings: SnipperPanel?
    private var controls: SnipperPanel?
    private var savedNotice: SnipperPanel?
    private var native: NativeRecorder?
    private var companion: CompanionServer?
    private var browserURL: URL?
    private var captureID: UUID?
    private var browserStopDeadline: Task<Void, Never>?
    private var previousApp: NSRunningApplication?

    private var exportFolder: URL {
        defaults.string(forKey: "exportFolder").map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? RecordingStorage.defaultExport
    }

    private var preferredFormat: ClipFormat {
        defaults.string(forKey: "preferredFormat").flatMap(ClipFormat.init(rawValue:)) ?? .mp4
    }

    var canStart: Bool { phase == .idle }
    var canStop: Bool { phase == .recording || phase == .browser }
    var isCapturing: Bool { phase == .recording || phase == .starting || phase == .finishing }

    func launch() {
        updateStoragePaths()
        KeyboardShortcuts.onKeyUp(for: .recordArea) { [weak self] in
            Task { @MainActor in self?.shortcut() }
        }
        if storage.root == nil { showSetup() }
    }

    func recordArea() {
        guard prepareSelection(), let screen = NSScreen.screens.first(where: { $0.frame.contains(NSEvent.mouseLocation) })
            ?? NSScreen.main, let displayID = screen.displayID else { return }
        phase = .selecting
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
                    throw SnipperError.problem("The selected display is no longer available.")
                }
                let filter = SCContentFilter(display: display, excludingWindows: [])
                areaSelection.show(on: screen, scale: CGFloat(filter.pointPixelScale)) { [weak self] crop in
                    guard let self else { return }
                    if let crop {
                        confirmAudio(for: .area(display: display, crop: crop),
                                     title: "Area · \(crop.width) × \(crop.height) px")
                    } else {
                        phase = .idle
                    }
                }
            } catch {
                phase = .idle
                alert("Could not select an area", error.localizedDescription)
            }
        }
    }

    func recordWindow() {
        guard prepareSelection() else { return }
        phase = .selecting
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
                let pid = ProcessInfo.processInfo.processIdentifier
                let windows = content.windows.filter {
                    $0.windowLayer == 0 && $0.isOnScreen && $0.owningApplication != nil
                        && $0.owningApplication?.processID != pid && $0.frame.width > 30 && $0.frame.height > 30
                }
                let panel = SnipperPanel("Choose a window", size: CGSize(width: 490, height: 390)) {
                    WindowSelectionView(windows: windows, onSelect: { [weak self] window in
                        self?.confirmAudio(for: .window(window),
                                           title: window.title.flatMap { $0.isEmpty ? nil : $0 } ?? "Window")
                    }, onCancel: { [weak self] in self?.cancelSelection() })
                }
                panel.onClose = { [weak self] in self?.cancelSelection() }
                showInteraction(panel)
            } catch {
                phase = .idle
                alert("Could not list windows", error.localizedDescription)
            }
        }
    }

    func recordChromeTab() {
        guard canStart, ensureStorage() else { return }
        guard let chrome = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.google.Chrome") else {
            alert("Google Chrome is required", "Install Google Chrome to record a browser tab.")
            return
        }
        closeInteraction()
        settings?.orderOut(nil)
        savedNotice?.orderOut(nil)
        captureNotice = nil
        phase = .starting
        let id = UUID()
        captureID = id
        Task {
            do {
                let draft = try storage.draft(source: "Tab", format: .webm)
                let server = try CompanionServer(draft: draft) { [weak self] event in
                    DispatchQueue.main.async { self?.browserEvent(event, id: id, draft: draft) }
                }
                companion = server
                let url = try await server.start()
                browserURL = url
                phase = .browser
                do {
                    try await openChrome(url, application: chrome)
                } catch {
                    guard captureID == id, phase == .browser else { return }
                    throw error
                }
            } catch {
                await closeBrowser()
                resetCapture()
                alert("Could not open the Chrome recorder", error.localizedDescription)
            }
        }
    }

    func stop() {
        if phase == .browser {
            phase = .finishing
            captureID = nil
            Task {
                await closeBrowser()
                resetCapture()
            }
        } else if phase == .recording {
            phase = .finishing
            if let native {
                Task { await native.stop() }
            } else if let companion {
                companion.stopRecording()
                browserStopDeadline = Task { [weak self] in
                    try? await Task.sleep(for: .seconds(20))
                    guard !Task.isCancelled, let self else { return }
                    let serverID = captureID
                    await closeBrowser()
                    if captureID == serverID {
                        resetCapture()
                        alert("Chrome did not finish recording", "Any received data stays in the temporary recordings folder.")
                    }
                }
            }
        }
    }

    func quit() {
        guard phase == .idle || phase == .selecting || phase == .browser else { return }
        if phase == .browser {
            phase = .finishing
            captureID = nil
            Task {
                await closeBrowser()
                NSApp.terminate(nil)
            }
        } else {
            NSApp.terminate(nil)
        }
    }

    func showSettings() {
        guard phase == .idle else { return }
        if let settings {
            settings.present()
            return
        }
        let panel = SnipperPanel("Simple Video Snipper Settings", size: CGSize(width: 555, height: 430)) {
            SnipperSettingsView(model: self)
        }
        panel.onClose = { [weak self] in self?.settings = nil }
        settings = panel
        panel.present()
    }

    func chooseStagingFolder() {
        if let folder = chooseFolder(title: "Choose a temporary recordings folder", initial: storage.root) {
            setStagingFolder(folder)
        }
    }

    func chooseExportFolder() {
        guard let selected = chooseFolder(title: "Choose a save folder", initial: exportFolder) else { return }
        let folder = selected.resolvingSymlinksInPath().standardizedFileURL
        guard !storage.containsStaging(folder.appendingPathComponent("capture.mp4")) else {
            alert("Choose another save folder", "Saved recordings need a folder outside the temporary recording files.")
            return
        }
        defaults.set(folder.path, forKey: "exportFolder")
        updateStoragePaths()
    }

    func showPending() {
        guard phase == .idle else { return }
        let panel = SnipperPanel("Unsaved recordings", size: CGSize(width: 545, height: 320)) {
            PendingClipsView(clips: storage.pending(), save: { [weak self] clip in
                guard let self else { return }
                closeInteraction()
                save(clip, ask: true)
            }, discard: { [weak self] clip in self?.confirmDiscard(clip, refresh: true) })
        }
        showInteraction(panel)
    }

    private func shortcut() {
        switch phase {
        case .idle: recordArea()
        case .recording, .finishing: controls?.orderFrontRegardless()
        case .selecting: interaction?.present()
        case .browser:
            if let url = browserURL, let chrome = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.google.Chrome") {
                Task { try? await openChrome(url, application: chrome) }
            }
        case .starting, .saving: break
        }
    }

    private func prepareSelection() -> Bool {
        guard canStart, ensureStorage() else { return false }
        closeInteraction()
        settings?.orderOut(nil)
        savedNotice?.orderOut(nil)
        if let app = NSWorkspace.shared.frontmostApplication,
           app.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            previousApp = app
        }
        guard CGPreflightScreenCaptureAccess() else {
            if CGRequestScreenCaptureAccess() { return true }
            alert("Screen recording access is required",
                  "Allow Simple Video Snipper in System Settings → Privacy & Security → Screen & System Audio Recording, then reopen the app.")
            return false
        }
        return true
    }

    private func confirmAudio(for source: CaptureSource, title: String) {
        guard phase == .selecting else { return }
        let panel = SnipperPanel("Recording options", size: CGSize(width: 420, height: 255)) {
            AudioConfigurationView(title: title, start: { [weak self] options in
                self?.startNative(source, audio: options)
            }, cancel: { [weak self] in self?.cancelSelection() })
        }
        panel.onClose = { [weak self] in self?.cancelSelection() }
        showInteraction(panel)
    }

    private func cancelSelection() {
        closeInteraction()
        phase = .idle
        previousApp?.activate(options: [])
        previousApp = nil
    }

    private func startNative(_ source: CaptureSource, audio: AudioOptions) {
        guard phase == .selecting else { return }
        closeInteraction()
        captureNotice = nil
        phase = .starting
        let id = UUID()
        captureID = id
        do {
            let draft = try storage.draft(source: source.title, format: .mp4)
            let recorder = NativeRecorder { [weak self] event in
                self?.nativeEvent(event, id: id, draft: draft)
            }
            native = recorder
            showRecordingControls()
            previousApp?.activate(options: [])
            previousApp = nil
            Task {
                do {
                    try await recorder.start(source: source, audio: audio, to: draft.url)
                } catch {
                    guard id == captureID else { return }
                    draft.removeIfEmpty()
                    resetCapture()
                    alert("Could not start recording", error.localizedDescription)
                }
            }
        } catch {
            resetCapture()
            alert("Could not prepare the recording", error.localizedDescription)
        }
    }

    private func nativeEvent(_ event: NativeRecorder.Event, id: UUID, draft: ClipDraft) {
        guard captureID == id else { return }
        switch event {
        case .started:
            didStartRecording()
        case .finishing:
            phase = .finishing
        case .notice(let message):
            captureNotice = message
        case .finished(let result):
            phase = .finishing
            Task {
                do {
                    try result.get()
                    try await NativeAudioMix.combineTracks(in: draft.url)
                    let clip = try draft.publish()
                    resetCapture()
                    save(clip, ask: false)
                } catch {
                    // A source failure may still leave a playable file. Make it available for saving.
                    if await playable(draft.url), let clip = try? draft.publish() {
                        let details = error.localizedDescription
                        resetCapture()
                        alert("The recording ended early", details + " The captured clip is available to save.")
                        save(clip, ask: false)
                    } else {
                        draft.removeIfEmpty()
                        resetCapture()
                        alert("Could not finish recording", error.localizedDescription)
                    }
                }
            }
        }
    }

    private func browserEvent(_ event: CompanionServer.Event, id: UUID, draft: ClipDraft) {
        guard captureID == id else { return }
        switch event {
        case .recording:
            didStartRecording()
        case .finished(let result):
            phase = .finishing
            Task {
                await closeBrowser()
                resetCapture()
                switch result {
                case .success(let clip):
                    save(clip, ask: false)
                case .failure(let error):
                    let kept = FileManager.default.fileExists(atPath: draft.url.path)
                    let details = kept ? "\(error.localizedDescription) Partial data is in \(draft.url.path)." : error.localizedDescription
                    alert("Could not finish the Chrome recording", details)
                }
            }
        }
    }

    private func playable(_ url: URL) async -> Bool {
        guard let tracks = try? await AVURLAsset(url: url).loadTracks(withMediaType: .video) else { return false }
        return !tracks.isEmpty
    }

    private func closeBrowser() async {
        browserStopDeadline?.cancel()
        browserStopDeadline = nil
        let server = companion
        companion = nil
        browserURL = nil
        await server?.close()
    }

    private func openChrome(_ url: URL, application: URL) async throws {
        _ = try await NSWorkspace.shared.open([url], withApplicationAt: application, configuration: .init())
    }

    private func didStartRecording() {
        phase = .recording
        recordingStarted = .now
        showRecordingControls()
    }

    private func showRecordingControls() {
        if let controls {
            controls.orderFrontRegardless()
            return
        }
        let panel = SnipperPanel("Simple Video Snipper", size: CGSize(width: 268, height: 66), floating: true) {
            RecordingControls(model: self)
        }
        if let frame = NSScreen.screens.first(where: { $0.frame.contains(NSEvent.mouseLocation) })?.visibleFrame
            ?? NSScreen.main?.visibleFrame {
            panel.setFrameTopLeftPoint(CGPoint(x: frame.maxX - panel.frame.width - 18, y: frame.maxY - 14))
        }
        controls = panel
        panel.orderFrontRegardless()
    }

    private func resetCapture() {
        phase = .idle
        native = nil
        captureID = nil
        recordingStarted = nil
        controls?.orderOut(nil)
        controls = nil
    }

    private func ensureStorage() -> Bool {
        if storage.root != nil { return true }
        showSetup()
        return false
    }

    private func showSetup() {
        let panel = SnipperPanel("Set up Simple Video Snipper", size: CGSize(width: 510, height: 166)) {
            StorageSetupView(choose: { [weak self] in self?.chooseStagingFolder() },
                             useDefault: { [weak self] in self?.setStagingFolder(RecordingStorage.defaultStaging) })
        }
        showInteraction(panel)
    }

    private func setStagingFolder(_ folder: URL) {
        let setup = storage.root == nil
        do {
            try storage.updateRoot(folder)
            updateStoragePaths()
            if setup { closeInteraction() }
        } catch {
            alert("Could not use this folder", error.localizedDescription)
        }
    }

    private func updateStoragePaths() {
        stagingPath = (storage.root ?? RecordingStorage.defaultStaging).path
        exportPath = exportFolder.path
    }

    private func chooseFolder(title: String, initial: URL?) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = initial
        NSApp.activate(ignoringOtherApps: true)
        return panel.runModal() == .OK ? panel.url : nil
    }

    private func showInteraction(_ panel: SnipperPanel) {
        closeInteraction()
        interaction = panel
        panel.present()
    }

    private func closeInteraction() {
        interaction?.onClose = nil
        interaction?.close()
        interaction = nil
    }

    private func save(_ clip: PendingClip, ask: Bool) {
        guard phase == .idle else { return }
        phase = .saving
        Task {
            defer { phase = .idle }
            let requiresAsk = ask || !defaults.bool(forKey: "quickSave")
            var request: ExportRequest?
            if requiresAsk {
                request = showSavePanel(for: clip, format: preferredFormat)
            } else {
                request = ExportRequest(format: preferredFormat, destination: .quick(folder: exportFolder))
            }
            while let destination = request {
                do {
                    try checkDestination(destination)
                    let saved = try await RecordingExporter.save(clip, request: destination)
                    showSaved(saved)
                    return
                } catch {
                    request = retrySave(clip, request: destination, message: error.localizedDescription)
                }
            }
        }
    }

    private func showSavePanel(for clip: PendingClip, format: ClipFormat) -> ExportRequest? {
        let panel = NSSavePanel()
        panel.title = "Save Recording"
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        panel.nameFieldStringValue = clip.title
        panel.directoryURL = FileManager.default.fileExists(atPath: exportFolder.path)
            ? exportFolder : exportFolder.deletingLastPathComponent()
        let formats = SavePanelFormat(panel: panel, format: format)
        NSApp.activate(ignoringOtherApps: true)
        return withExtendedLifetime(formats) {
            guard panel.runModal() == .OK, let url = panel.url else { return nil }
            let format = formats.format
            let destination = url.pathExtension.lowercased() == format.rawValue
                ? url : url.appendingPathExtension(format.rawValue)
            let replace = destination == url && FileManager.default.fileExists(atPath: url.path)
            return ExportRequest(format: format, destination: .chosen(file: destination, replace: replace))
        }
    }

    private func checkDestination(_ request: ExportRequest) throws {
        let url: URL
        switch request.destination {
        case .chosen(let file, _): url = file
        case .quick(let folder): url = folder.appendingPathComponent("capture.mp4")
        }
        guard !storage.containsStaging(url) else {
            throw SnipperError.problem("Choose a save location outside the temporary recording files.")
        }
    }

    private func retrySave(_ clip: PendingClip, request: ExportRequest, message: String) -> ExportRequest? {
        let dialog = NSAlert()
        dialog.alertStyle = .warning
        dialog.messageText = "Could not save the recording"
        dialog.informativeText = "\(message) The temporary recording is still available."
        dialog.addButton(withTitle: "Retry")
        dialog.addButton(withTitle: "Save As…")
        dialog.addButton(withTitle: "Keep Unsaved").keyEquivalent = "\u{1b}"
        dialog.addButton(withTitle: "Discard…").hasDestructiveAction = true
        NSApp.activate(ignoringOtherApps: true)
        switch dialog.runModal() {
        case .alertFirstButtonReturn: return request
        case .alertSecondButtonReturn: return showSavePanel(for: clip, format: request.format)
        case NSApplication.ModalResponse(rawValue: NSApplication.ModalResponse.alertThirdButtonReturn.rawValue + 1):
            confirmDiscard(clip, refresh: false)
            return nil
        default: return nil
        }
    }

    private func confirmDiscard(_ clip: PendingClip, refresh: Bool) {
        let dialog = NSAlert()
        dialog.alertStyle = .warning
        dialog.messageText = "Discard this recording?"
        dialog.informativeText = clip.title
        dialog.addButton(withTitle: "Keep Recording").keyEquivalent = "\r"
        dialog.addButton(withTitle: "Discard").hasDestructiveAction = true
        if dialog.runModal() == .alertSecondButtonReturn {
            do {
                try FileManager.default.removeItem(at: clip.url)
                if refresh { showPending() }
            } catch {
                alert("Could not discard the recording", error.localizedDescription)
            }
        }
    }

    private func showSaved(_ url: URL) {
        let autoCopy = defaults.bool(forKey: "autoCopy")
        let copied = autoCopy && SavedClipView.copy(url)
        savedNotice?.close()
        let panel = SnipperPanel("Recording saved", size: CGSize(width: 485, height: 125)) {
            SavedClipView(url: url, initiallyCopied: copied)
        }
        panel.onClose = { [weak self] in self?.savedNotice = nil }
        savedNotice = panel
        panel.present()
    }

    private func alert(_ title: String, _ message: String) {
        let dialog = NSAlert()
        dialog.alertStyle = .warning
        dialog.messageText = title
        dialog.informativeText = message
        dialog.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        dialog.runModal()
    }
}
