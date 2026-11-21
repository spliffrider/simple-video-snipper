import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class ClipEditorModel: ObservableObject {
    let media: ClipEditMedia
    var gifCaption = GIFCaption()
    @Published private(set) var edits: ClipEdits
    // The editor playhead is always in source time, including while a trimmed preview plays.
    @Published private(set) var position: Double = 0
    @Published private(set) var frame: NSImage?
    @Published private(set) var player: AVPlayer?
    @Published private(set) var isPreparing = false
    @Published private(set) var isPlaying = false
    @Published var previewVolume: Double = 0.8 {
        didSet { player?.volume = Float(previewVolume) }
    }
    @Published private(set) var error: String?

    private var renderedPreview: URL?
    private var previewTask: Task<Void, Never>?
    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var failureObserver: NSObjectProtocol?
    private var seekID: Int?
    private var nextSeekID = 0

    init(media: ClipEditMedia) {
        self.media = media
        edits = ClipEdits(start: 0, end: media.info.duration, source: media.info)
    }

    var exportEdits: ClipEdits? {
        let unedited = edits.start == 0 && edits.end == media.info.duration
            && edits.crop == nil && !edits.muteOriginal && edits.music == nil
        return unedited ? nil : edits
    }

    private var minimumLength: Double { min(media.info.duration, 1.0 / 30) }
    var canSetStart: Bool { position <= edits.end - minimumLength }
    var canSetEnd: Bool { position >= edits.start + minimumLength }

    func setStart() { if canSetStart { moveTrimStart(to: position) } }
    func setEnd() { if canSetEnd { moveTrimEnd(to: position) } }

    func moveTrimStart(to time: Double) {
        let start = min(max(0, time), max(0, edits.end - minimumLength))
        changeEdits(at: start) { $0.start = start }
    }

    func moveTrimEnd(to time: Double) {
        let end = max(min(media.info.duration, time), min(media.info.duration, edits.start + minimumLength))
        changeEdits(at: end) { $0.end = end }
    }

    func resetTrim() {
        changeEdits {
            $0.start = 0
            $0.end = media.info.duration
        }
    }

    func setCrop(_ crop: PixelCrop?) { changeEdits { $0.crop = crop } }
    func setMuted(_ muted: Bool) { changeEdits { $0.muteOriginal = muted } }
    func setMusicVolume(_ volume: Double) { changeEdits { $0.music?.volume = volume } }
    func removeMusic() { changeEdits { $0.music = nil } }

    private func changeEdits(at time: Double? = nil, _ change: (inout ClipEdits) -> Void) {
        var next = edits
        change(&next)
        guard next != edits else {
            if let time { scrub(to: time) }
            return
        }
        if time == nil { syncPlayhead() }
        stopPlayback()
        if let time { position = time }
        discardPreview()
        edits = next
        error = nil
    }

    func chooseMusic() {
        if isPlaying { pause() }
        let panel = NSOpenPanel()
        panel.title = "Choose Music"
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        changeEdits { $0.music = ClipMusic(url: url, volume: $0.music?.volume ?? 0.5) }
    }

    func readFrame(at time: Double) async {
        do {
            try await Task.sleep(for: .milliseconds(75))
            let data = try await media.frame(at: time)
            try Task.checkCancellation()
            guard position == time, player == nil else { return }
            guard let image = NSImage(data: data) else {
                throw SnipperError.problem("No frame is available at this position.")
            }
            frame = image
        } catch is CancellationError {
            return
        } catch {
            if position == time, player == nil { self.error = error.localizedDescription }
        }
    }

    func togglePlayback() {
        if isPlaying {
            pause()
            return
        }
        if position < edits.start || position >= edits.end { position = edits.start }
        error = nil
        isPlaying = true
        if let renderedPreview {
            openPlayer(renderedPreview)
            return
        }
        if exportEdits == nil, media.clip.format == .mp4 {
            openPlayer(media.clip.url)
            return
        }
        isPreparing = true
        let edits = self.edits
        previewTask = Task { [weak self, media] in
            do {
                let file = try await media.preview(edits)
                guard let self, !Task.isCancelled else {
                    try? FileManager.default.removeItem(at: file)
                    return
                }
                renderedPreview = file
                previewTask = nil
                isPreparing = false
                openPlayer(file)
            } catch is CancellationError {
                return
            } catch {
                guard let self, !Task.isCancelled else { return }
                stopPlayback()
                self.error = error.localizedDescription
            }
        }
    }

    func scrub(to time: Double) {
        position = min(max(0, time), media.info.duration)
        guard isPlaying else { return }
        if position >= edits.start && position < edits.end {
            seekPlayer()
        } else {
            stopPlayback()
        }
    }

    func skip(by seconds: Double) {
        syncPlayhead()
        scrub(to: min(max(edits.start, position + seconds), edits.end))
    }

    private func pause() {
        syncPlayhead()
        stopPlayback()
    }

    private func syncPlayhead() {
        guard let player, seekID == nil else { return }
        let elapsed = player.currentTime().seconds
        guard elapsed.isFinite else { return }
        position = min(edits.end, max(edits.start, edits.start + elapsed))
    }

    private func openPlayer(_ url: URL) {
        let item = AVPlayerItem(url: url)
        let player = AVPlayer(playerItem: item)
        player.volume = Float(previewVolume)
        player.actionAtItemEnd = .pause
        self.player = player
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(value: 1, timescale: 20), queue: .main
        ) { [weak self, weak player] _ in
            Task { @MainActor in
                guard let self, let player, self.player === player else { return }
                self.syncPlayhead()
            }
        }
        endObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main
        ) { [weak self, weak player] _ in
            Task { @MainActor in
                guard let self, let player, self.player === player, self.seekID == nil else { return }
                self.position = self.edits.end
                self.stopPlayback()
            }
        }
        failureObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemFailedToPlayToEndTime, object: item, queue: .main
        ) { [weak self, weak player] notification in
            let message = (notification.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error)?.localizedDescription
            Task { @MainActor in
                guard let self, let player, self.player === player else { return }
                self.stopPlayback()
                self.discardPreview()
                self.error = message ?? "The preview stopped unexpectedly."
            }
        }
        seekPlayer()
    }

    private func seekPlayer() {
        guard let player else { return }
        let offset = position - edits.start
        nextSeekID += 1
        let id = nextSeekID
        seekID = id
        player.seek(to: CMTime(seconds: offset, preferredTimescale: 600),
                    toleranceBefore: .zero, toleranceAfter: .zero) { [weak self, weak player] finished in
            Task { @MainActor in
                guard let self, let player, self.player === player, self.seekID == id else { return }
                self.seekID = nil
                if finished {
                    player.play()
                } else {
                    self.stopPlayback()
                    self.discardPreview()
                    self.error = "Could not seek to this position in the preview."
                }
            }
        }
    }

    private func stopPlayback() {
        previewTask?.cancel()
        previewTask = nil
        isPreparing = false
        isPlaying = false
        if let timeObserver { player?.removeTimeObserver(timeObserver) }
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        if let failureObserver { NotificationCenter.default.removeObserver(failureObserver) }
        timeObserver = nil
        endObserver = nil
        failureObserver = nil
        player?.pause()
        player?.replaceCurrentItem(with: nil)
        player = nil
        seekID = nil
    }

    private func discardPreview() {
        if let renderedPreview { try? FileManager.default.removeItem(at: renderedPreview) }
        renderedPreview = nil
    }

    func close() {
        stopPlayback()
        discardPreview()
    }
}
