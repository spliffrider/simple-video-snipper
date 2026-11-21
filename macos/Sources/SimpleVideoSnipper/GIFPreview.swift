import AppKit
import ImageIO
import SwiftUI

enum GIFPreset: String, CaseIterable, Identifiable {
    case small = "Small"
    case clear = "Clear"

    var id: Self { self }
    var longestSide: Int { self == .small ? 480 : 720 }
    var frameRate: Int { self == .small ? 10 : 12 }
}

@MainActor
final class GIFPreviewModel: ObservableObject {
    @Published var preset = GIFPreset.small { didSet { if preset != oldValue { render() } } }
    @Published var holdLastFrame = false { didSet { if holdLastFrame != oldValue { render() } } }
    @Published var caption: GIFCaption {
        didSet {
            if caption != oldValue { render(afterTyping: caption.text != oldValue.text) }
        }
    }
    @Published private(set) var image: CGImage?
    @Published private(set) var output: URL?
    @Published private(set) var size = CGSize.zero
    @Published private(set) var bytes = 0
    @Published private(set) var isRendering = false
    @Published private(set) var isPlaying = false
    @Published private(set) var isSaving = false
    @Published private(set) var error: String?

    private let media: ClipEditMedia
    private let edits: ClipEdits
    private var renderTask: Task<Void, Never>?
    private var animationTask: Task<Void, Never>?
    private var imageSource: CGImageSource?
    private var frameIndex = 0

    init(media: ClipEditMedia, edits: ClipEdits, caption: GIFCaption) {
        self.media = media
        self.edits = edits
        self.caption = caption
    }

    func render(afterTyping: Bool = false) {
        guard !isSaving else { return }
        cancelRender()
        clearPreview()
        error = nil
        isRendering = true
        let preset = self.preset
        let hold = holdLastFrame
        let caption = self.caption
        renderTask = Task { [weak self, media, edits] in
            do {
                if afterTyping { try await Task.sleep(for: .milliseconds(400)) }
                let url = try await media.renderGIF(edits, preset: preset, holdLastFrame: hold, caption: caption)
                guard let self, !Task.isCancelled else {
                    try? FileManager.default.removeItem(at: url)
                    return
                }
                self.output = url
                try open(url)
                isRendering = false
                renderTask = nil
                play()
            } catch is CancellationError {
                return
            } catch {
                guard let self, !Task.isCancelled else { return }
                clearPreview()
                isRendering = false
                renderTask = nil
                self.error = error.localizedDescription
            }
        }
    }

    func cancelRender() {
        renderTask?.cancel()
        renderTask = nil
        isRendering = false
    }

    func togglePlayback() { if isPlaying { pause() } else { play() } }

    func beginSave() -> URL? {
        guard !isSaving, !isRendering, let output else { return nil }
        pause()
        isSaving = true
        return output
    }

    func finishSave() { isSaving = false }

    func close() {
        cancelRender()
        clearPreview()
    }

    private func open(_ url: URL) throws {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let first = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw SnipperError.problem("Could not decode the GIF preview.")
        }
        bytes = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        size = CGSize(width: first.width, height: first.height)
        imageSource = source
        image = first
        frameIndex = 0
    }

    private func clearPreview() {
        pause()
        imageSource = nil
        image = nil
        frameIndex = 0
        if let output { try? FileManager.default.removeItem(at: output) }
        output = nil
        size = .zero
        bytes = 0
    }

    private func pause() {
        animationTask?.cancel()
        animationTask = nil
        isPlaying = false
    }

    private func play() {
        guard !isPlaying, let imageSource, CGImageSourceGetCount(imageSource) > 1 else { return }
        isPlaying = true
        animationTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let delay = self?.frameDelay else { break }
                do { try await Task.sleep(for: .seconds(delay)) } catch { break }
                self?.advanceFrame()
            }
        }
    }

    private var frameDelay: Double {
        guard let imageSource,
              let metadata = CGImageSourceCopyPropertiesAtIndex(imageSource, frameIndex, nil) as? [CFString: Any],
              let gif = metadata[kCGImagePropertyGIFDictionary] as? [CFString: Any] else { return 0.1 }
        let seconds = (gif[kCGImagePropertyGIFUnclampedDelayTime] ?? gif[kCGImagePropertyGIFDelayTime]) as? Double
        return max(0.02, seconds ?? 0.1)
    }

    private func advanceFrame() {
        guard let imageSource else { return }
        frameIndex = (frameIndex + 1) % CGImageSourceGetCount(imageSource)
        if let next = CGImageSourceCreateImageAtIndex(imageSource, frameIndex,
                                                      [kCGImageSourceShouldCache: false] as CFDictionary) {
            image = next
        }
    }
}

struct GIFPreviewView: View {
    @ObservedObject var model: GIFPreviewModel
    let save: () -> Void
    let close: () -> Void

    @Environment(\.displayScale) private var displayScale
    @State private var actualSize = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Picker("Preset", selection: $model.preset) {
                    ForEach(GIFPreset.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(width: 250)
                Spacer()
                Toggle("Hold last frame for 1 second", isOn: $model.holdLastFrame)
            }
            .disabled(model.isSaving)

            canvas
                .layoutPriority(1)
                .background(.black)

            if model.output != nil {
                Text("\(Int(model.size.width)) × \(Int(model.size.height)) px · \(model.preset.frameRate) fps · \(ByteCountFormatter.string(fromByteCount: Int64(model.bytes), countStyle: .file))")
                    .monospacedDigit().foregroundStyle(.secondary)
            } else if let error = model.error {
                Text(error).foregroundStyle(.red).font(.callout).lineLimit(3)
            }

            HStack(alignment: .top, spacing: 10) {
                Text("Caption").padding(.top, 5)
                TextEditor(text: $model.caption.text)
                    .font(.body)
                    .frame(height: 54)
                    .border(Color.secondary.opacity(0.35))
                    .accessibilityLabel("GIF caption")
                Picker("Position", selection: $model.caption.position) {
                    ForEach(GIFCaption.Position.allCases) { Text($0.rawValue).tag($0) }
                }
                .frame(width: 146)
                .accessibilityLabel("Caption position")
                .disabled(!model.caption.hasText)
            }
            .disabled(model.isSaving)

            Divider()
            HStack {
                Button(model.isPlaying ? "Pause" : "Play") { model.togglePlayback() }
                    .disabled(model.output == nil || model.isSaving)
                Picker("Zoom", selection: $actualSize) {
                    Text("Fit").tag(false)
                    Text("Actual size").tag(true)
                }
                .labelsHidden().accessibilityLabel("Preview zoom").frame(width: 110)
                if model.isRendering {
                    Button("Cancel render") { model.cancelRender() }
                } else if model.output == nil {
                    Button("Render") { model.render() }
                }
                Spacer()
                Button("Back", action: close).keyboardShortcut(.cancelAction)
                    .disabled(model.isSaving)
                Button("Save GIF…", action: save).keyboardShortcut(.defaultAction)
                    .disabled(model.output == nil || model.isSaving)
            }
        }
        .padding(18)
    }

    private var canvas: some View {
        GeometryReader { geometry in
            if let image = model.image {
                let picture = Image(decorative: image, scale: displayScale).resizable()
                    .accessibilityLabel("Encoded GIF preview")
                if actualSize {
                    ScrollView([.horizontal, .vertical]) {
                        picture.frame(width: model.size.width / displayScale, height: model.size.height / displayScale)
                            .frame(minWidth: geometry.size.width, minHeight: geometry.size.height)
                    }
                } else {
                    picture.scaledToFit().frame(width: geometry.size.width, height: geometry.size.height)
                }
            } else if model.isRendering {
                ProgressView("Rendering \(model.preset.rawValue) preview…")
                    .tint(.white).foregroundStyle(.white)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(minHeight: 160)
    }
}
