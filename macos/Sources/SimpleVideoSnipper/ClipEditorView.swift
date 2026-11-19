import AppKit
import AVFoundation
import SwiftUI

struct ClipEditorView: View {
    @ObservedObject var model: ClipEditorModel
    let save: () -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            edits

            if let error = model.error {
                Text(error).foregroundStyle(.red).font(.callout).lineLimit(3)
            }
            Divider()
            HStack {
                Button("Cancel", action: cancel).keyboardShortcut(.cancelAction)
                Spacer()
                Button("Save", action: save).keyboardShortcut(.defaultAction)
            }
        }
        .padding(18)
    }

    private var edits: some View {
        VStack(alignment: .leading, spacing: 10) {
            canvas.layoutPriority(1)
            HStack {
                Text(timestamp(model.position)).monospacedDigit()
                TrimTimeline(model: model)
                Text(timestamp(model.media.info.duration)).monospacedDigit()
            }
            playbackControls
            HStack {
                Button("Set start") { model.setStart() }
                    .keyboardShortcut(.leftArrow, modifiers: .option)
                    .disabled(!model.canSetStart)
                Button("Set end") { model.setEnd() }
                    .keyboardShortcut(.rightArrow, modifiers: .option)
                    .disabled(!model.canSetEnd)
                Text("\(timestamp(model.edits.start)) – \(timestamp(model.edits.end))")
                    .monospacedDigit().foregroundStyle(.secondary)
                    .accessibilityLabel("Trim range")
                    .accessibilityValue("\(timestamp(model.edits.start)) to \(timestamp(model.edits.end))")
                if model.edits.start > 0 || model.edits.end < model.media.info.duration {
                    Button("Reset trim") { model.resetTrim() }
                }
                Spacer()
                if let crop = model.edits.crop {
                    Text("\(crop.width) × \(crop.height)").monospacedDigit().foregroundStyle(.secondary)
                    Button("Clear crop") { model.setCrop(nil) }
                } else {
                    Text(model.isPlaying ? "Pause to crop" : "Drag on the frame to crop").foregroundStyle(.secondary)
                }
            }
            Divider()
            HStack(spacing: 14) {
                Toggle("Mute original audio", isOn: Binding(
                    get: { model.edits.muteOriginal }, set: model.setMuted
                ))
                .disabled(!model.media.info.hasAudio)
                Button(model.edits.music == nil ? "Add music…" : "Change music…") { model.chooseMusic() }
                if let music = model.edits.music {
                    Text(music.url.lastPathComponent).lineLimit(1).help(music.url.path)
                    Slider(value: Binding(get: { model.edits.music?.volume ?? 0 }, set: model.setMusicVolume),
                           in: 0...1) { Text("Music volume") }
                        .accessibilityLabel("Music volume")
                        .frame(maxWidth: 185)
                    Text("\(Int(music.volume * 100))%").monospacedDigit()
                    Button("Remove music") { model.removeMusic() }
                }
                Spacer(minLength: 0)
            }
        }
    }

    private var canvas: some View {
        ZStack {
            if let player = model.player {
                PlayerSurface(player: player)
                    .background(.black)
                    .accessibilityLabel("Video preview")
            } else {
                CropCanvas(frameImage: model.frame, info: model.media.info,
                           crop: model.edits.crop, onCrop: model.setCrop)
                    .allowsHitTesting(!model.isPlaying)
                    .task(id: model.position) { await model.readFrame(at: model.position) }
            }
            if model.isPreparing {
                ProgressView("Preparing playback…")
                    .padding(12)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8))
            }
        }
        .frame(minHeight: 100, maxHeight: .infinity)
    }

    private var playbackControls: some View {
        HStack {
            Button(model.isPlaying ? "Pause" : "Play") {
                if let event = NSApp.currentEvent, event.type == .keyDown, event.isARepeat { return }
                model.togglePlayback()
            }
            .keyboardShortcut(.space, modifiers: [])
            Button("Back 5s") { model.skip(by: -5) }
            Button("Forward 5s") { model.skip(by: 5) }
            Spacer()
            Slider(value: $model.previewVolume, in: 0...1) { Text("Preview volume") }
                .accessibilityLabel("Preview volume")
                .frame(maxWidth: 220)
            Text("\(Int(model.previewVolume * 100))%").monospacedDigit()
        }
    }

    private func timestamp(_ seconds: Double) -> String {
        let ticks = max(0, Int(seconds * 100))
        return String(format: "%d:%02d.%02d", ticks / 6000, ticks / 100 % 60, ticks % 100)
    }
}

private struct CropCanvas: View {
    let frameImage: NSImage?
    let info: ClipMediaInfo
    let crop: PixelCrop?
    let onCrop: (PixelCrop?) -> Void
    @State private var dragCrop: CGRect?

    var body: some View {
        GeometryReader { geometry in
            let scale = min(geometry.size.width / CGFloat(info.width), geometry.size.height / CGFloat(info.height))
            let size = CGSize(width: CGFloat(info.width) * scale, height: CGFloat(info.height) * scale)
            ZStack {
                Color.black
                if let frameImage {
                    ZStack {
                        Image(nsImage: frameImage).resizable().accessibilityLabel("Recording frame")
                        if let cropRect = dragCrop ?? crop.map({ displayRect($0, scale: scale) }) {
                            Path { path in
                                path.addRect(CGRect(origin: .zero, size: size))
                                path.addRect(cropRect)
                            }
                            .fill(Color.black.opacity(0.5), style: FillStyle(eoFill: true))
                            Path { $0.addRect(cropRect) }
                                .stroke(Color.accentColor, lineWidth: 2)
                        }
                    }
                    .frame(width: size.width, height: size.height)
                    .clipped()
                    .contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 3)
                        .onChanged { dragCrop = selection($0, in: size) }
                        .onEnded {
                            let rect = selection($0, in: size)
                            dragCrop = nil
                            if scale > 0, let crop = pixelCrop(rect, scale: scale) { onCrop(crop) }
                        })
                } else {
                    ProgressView("Reading frame…").tint(.white)
                }
            }
        }
        .frame(minHeight: 100, maxHeight: .infinity)
    }

    private func selection(_ value: DragGesture.Value, in size: CGSize) -> CGRect {
        func clamped(_ point: CGPoint) -> CGPoint {
            CGPoint(x: min(max(point.x, 0), size.width), y: min(max(point.y, 0), size.height))
        }
        let a = clamped(value.startLocation)
        let b = clamped(value.location)
        return CGRect(x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y).standardized
    }

    private func displayRect(_ crop: PixelCrop, scale: CGFloat) -> CGRect {
        CGRect(x: CGFloat(crop.x) * scale, y: CGFloat(crop.y) * scale,
               width: CGFloat(crop.width) * scale, height: CGFloat(crop.height) * scale)
    }

    private func pixelCrop(_ rect: CGRect, scale: CGFloat) -> PixelCrop? {
        let x = max(0, Int((rect.minX / scale).rounded()))
        let y = max(0, Int((rect.minY / scale).rounded()))
        let right = min(info.width, Int((rect.maxX / scale).rounded()))
        let bottom = min(info.height, Int((rect.maxY / scale).rounded()))
        let width = (right - x) / 2 * 2
        let height = (bottom - y) / 2 * 2
        guard width >= 2, height >= 2 else { return nil }
        return PixelCrop(x: x, y: y, width: width, height: height)
    }
}

private struct PlayerSurface: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        let layer = AVPlayerLayer(player: player)
        layer.videoGravity = .resizeAspect
        view.layer = layer
        view.wantsLayer = true
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        (view.layer as? AVPlayerLayer)?.player = player
    }
}
