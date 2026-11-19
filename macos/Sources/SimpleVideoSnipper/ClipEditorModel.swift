import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class ClipEditorModel: ObservableObject {
    let media: ClipEditMedia
    @Published private(set) var edits: ClipEdits
    @Published var position: Double = 0
    @Published private(set) var frame: NSImage?
    @Published private(set) var player: AVPlayer?
    @Published private(set) var isPreparing = false
    @Published private(set) var isPlaying = false
    @Published private(set) var playbackPosition: Double = 0
    @Published var previewVolume: Double = 0.8 {
        didSet { player?.volume = Float(previewVolume) }
    }
    @Published private(set) var error: String?

    private var previewFile: URL?
    private var previewTask: Task<Void, Never>?
    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var failureObserver: NSObjectProtocol?
    private var seekGeneration = 0

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

    func setStart() { if canSetStart { edits.start = position } }
    func setEnd() { if canSetEnd { edits.end = position } }

    func moveTrimStart(to time: Double) {
        let start = min(max(0, time), max(0, edits.end - minimumLength))
        if start != edits.start { edits.start = start }
        position = start
    }

    func moveTrimEnd(to time: Double) {
        let end = max(min(media.info.duration, time), min(media.info.duration, edits.start + minimumLength))
        if end != edits.end { edits.end = end }
        position = end
    }

    func resetTrim() {
        edits.start = 0
        edits.end = media.info.duration
    }

    func setCrop(_ crop: PixelCrop?) { edits.crop = crop }
    func setMuted(_ muted: Bool) { edits.muteOriginal = muted }
    func setMusicVolume(_ volume: Double) { edits.music?.volume = volume }
    func removeMusic() { edits.music = nil }

    func chooseMusic() {
        let panel = NSOpenPanel()
        panel.title = "Choose Music"
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        edits.music = ClipMusic(url: url, volume: edits.music?.volume ?? 0.5)
        error = nil
    }

    func readFrame(at time: Double) async {
        do {
            try await Task.sleep(for: .milliseconds(75))
            let data = try await media.frame(at: time)
            try Task.checkCancellation()
            guard position == time else { return }
            guard let image = NSImage(data: data) else {
                throw SnipperError.problem("No frame is available at this position.")
            }
            frame = image
            error = nil
        } catch is CancellationError {
            return
        } catch {
            if position == time { self.error = error.localizedDescription }
        }
    }

    func playPreview() {
        guard !isPreparing, player == nil else { return }
        error = nil
        isPreparing = true
        let edits = self.edits
        previewTask = Task { [weak self, media] in
            do {
                let url = try await media.preview(edits)
                guard let self, !Task.isCancelled else {
                    try? FileManager.default.removeItem(at: url)
                    return
                }
                previewFile = url
                startPlayback(url)
                isPreparing = false
                previewTask = nil
            } catch is CancellationError {
                return
            } catch {
                guard let self, !Task.isCancelled else { return }
                self.error = error.localizedDescription
                isPreparing = false
                previewTask = nil
            }
        }
    }

    private func startPlayback(_ url: URL) {
        let item = AVPlayerItem(url: url)
        let player = AVPlayer(playerItem: item)
        player.volume = Float(previewVolume)
        player.actionAtItemEnd = .pause
        self.player = player
        playbackPosition = 0
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(value: 1, timescale: 20), queue: .main
        ) { [weak self, weak player] time in
            Task { @MainActor in
                guard let self, let player, self.player === player, self.seekGeneration == 0 else { return }
                self.playbackPosition = min(self.edits.duration, max(0, time.seconds))
            }
        }
        endObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main
        ) { [weak self, weak player] _ in
            Task { @MainActor in
                guard let self, let player, self.player === player else { return }
                self.playbackPosition = self.edits.duration
                self.isPlaying = false
            }
        }
        failureObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemFailedToPlayToEndTime, object: item, queue: .main
        ) { [weak self, weak player] notification in
            let message = (notification.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error)?.localizedDescription
            Task { @MainActor in
                guard let self, let player, self.player === player else { return }
                self.isPlaying = false
                self.error = message ?? "The preview stopped unexpectedly."
            }
        }
        isPlaying = true
        player.play()
    }

    func togglePlayback() {
        guard let player else { return }
        if isPlaying {
            player.pause()
            isPlaying = false
        } else {
            if playbackPosition >= edits.duration - 0.01 { seek(to: 0) }
            isPlaying = true
            player.play()
        }
    }

    func restart() {
        guard let player else { return }
        seek(to: 0)
        isPlaying = true
        player.play()
    }

    private var nextSeekID = 0

    func seek(to time: Double) {
        guard let player else { return }
        let target = min(max(time, 0), edits.duration)
        playbackPosition = target
        nextSeekID += 1
        let id = nextSeekID
        seekGeneration = id
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600),
                    toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            Task { @MainActor in
                guard self?.seekGeneration == id else { return }
                self?.seekGeneration = 0
            }
        }
    }

    func close() {
        previewTask?.cancel()
        previewTask = nil
        isPreparing = false
        player?.pause()
        if let timeObserver { player?.removeTimeObserver(timeObserver) }
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        if let failureObserver { NotificationCenter.default.removeObserver(failureObserver) }
        timeObserver = nil
        endObserver = nil
        failureObserver = nil
        player?.replaceCurrentItem(with: nil)
        player = nil
        isPlaying = false
        seekGeneration = 0
        if let previewFile { try? FileManager.default.removeItem(at: previewFile) }
        previewFile = nil
    }
}
