import AppKit
import AVFoundation
import ScreenCaptureKit

@MainActor
enum CropVerification {
    static func run(to destination: URL, systemAudio: Bool = false) async throws {
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw SnipperError.problem("Use a new path for the verification clip.")
        }
        guard CGPreflightScreenCaptureAccess() else {
            _ = CGRequestScreenCaptureAccess()
            throw SnipperError.problem("Allow screen recording in System Settings and relaunch the verification.")
        }
        guard let screen = NSScreen.main, let displayID = screen.displayID else {
            throw SnipperError.problem("Connect and unlock a display to run the crop verification.")
        }
        try checkGeometry()
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw SnipperError.problem("The current display is unavailable for recording.")
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])

        let frame = CGRect(x: floor(screen.frame.midX - 190), y: floor(screen.frame.midY - 140),
                           width: 380, height: 280)
        let window = NSWindow(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.level = .floating
        window.isOpaque = true
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.contentView = VerificationGrid(frame: CGRect(origin: .zero, size: frame.size))
        window.orderFrontRegardless()
        defer { window.orderOut(nil) }

        // Crop is deliberately off-center to catch an inverted Y origin and incorrect display scaling.
        let selection = CGRect(
            x: frame.minX - screen.frame.minX + 52, y: frame.minY - screen.frame.minY + 28,
            width: 252, height: 188
        )
        let scale = CGFloat(filter.pointPixelScale)
        guard let crop = AreaCrop(selection: selection, displaySize: screen.frame.size, scale: scale) else {
            throw SnipperError.problem("The crop fixture did not fit the display.")
        }
        let overlay = NSWindow(
            contentRect: CGRect(x: frame.minX + 47, y: frame.minY + 22, width: 35, height: 35),
            styleMask: .borderless, backing: .buffered, defer: false
        )
        overlay.backgroundColor = .white
        overlay.level = .modalPanel
        overlay.ignoresMouseEvents = true
        overlay.orderFrontRegardless()
        defer { overlay.orderOut(nil) }
        try await Task.sleep(for: .milliseconds(500))
        let windowID = CGWindowID(window.windowNumber)
        try await record(
            .area(display: display, crop: crop), windowID: windowID,
            audio: AudioOptions(system: systemAudio), to: destination
        ) {
            try? await Task.sleep(for: .seconds(systemAudio ? 3 : 1))
        }
        let asset = AVURLAsset(url: destination)
        guard let video = try await asset.loadTracks(withMediaType: .video).first else {
            throw SnipperError.problem("The resulting MP4 has no video track.")
        }
        let size = try await video.load(.naturalSize)
        guard Int(size.width) == crop.width, Int(size.height) == crop.height else {
            throw SnipperError.problem("Expected \(crop.width)×\(crop.height) pixels. Found \(size).")
        }

        let imageGenerator = AVAssetImageGenerator(asset: asset)
        let (image, _) = try await imageGenerator.image(at: .zero)
        try verifyColors(in: image)
        if systemAudio {
            let tracks = try await asset.loadTracks(withMediaType: .audio)
            guard tracks.count == 1 else {
                throw SnipperError.problem("Expected one system audio track. Found \(tracks.count).")
            }
        }
        print("Verified H.264 MP4, Stop, \(crop.width)×\(crop.height) crop at \(scale)×, and all four corner colors.")
    }

    static func verifyWindow(to destination: URL) async throws {
        guard !FileManager.default.fileExists(atPath: destination.path),
              CGPreflightScreenCaptureAccess(), let screen = NSScreen.main else {
            throw SnipperError.problem("Use a new path and allow screen recording before verifying a window.")
        }
        let frame = CGRect(x: floor(screen.frame.midX - 200), y: floor(screen.frame.midY - 130),
                           width: 400, height: 260)
        let window = NSWindow(contentRect: frame, styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.title = "Simple Video Snipper Window Fixture"
        window.isReleasedWhenClosed = false
        window.contentView = AspectGrid(frame: CGRect(origin: .zero, size: frame.size))
        window.orderFrontRegardless()
        defer { window.orderOut(nil) }
        try await Task.sleep(for: .milliseconds(500))
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
        guard let target = content.windows.first(where: { $0.windowID == CGWindowID(window.windowNumber) }) else {
            throw SnipperError.problem("The verification window was not available to ScreenCaptureKit.")
        }
        let source = CaptureSource.window(target)
        let (_, configuration) = try source.streamSetup()
        try await record(source, windowID: nil, audio: .init(), to: destination, stopWhenReady: false) {
            try? await Task.sleep(for: .milliseconds(600))
            window.setContentSize(CGSize(width: 400, height: 123))
            try? await Task.sleep(for: .seconds(1))
            window.close()
        }
        let asset = AVURLAsset(url: destination)
        let expected = CGSize(width: configuration.width, height: configuration.height)
        guard let track = try await asset.loadTracks(withMediaType: .video).first,
              try await track.load(.naturalSize) == expected else {
            throw SnipperError.problem("Window capture did not keep its original dimensions.")
        }
        let generator = AVAssetImageGenerator(asset: asset)
        generator.requestedTimeToleranceAfter = .zero
        generator.requestedTimeToleranceBefore = .zero
        let (first, _) = try await generator.image(at: CMTime(seconds: 0.3, preferredTimescale: 600))
        let (resized, _) = try await generator.image(at: CMTime(seconds: 1.2, preferredTimescale: 600))
        guard first.width == resized.width, first.height == resized.height else {
            throw SnipperError.problem("Resizing the source changed the encoded frame size.")
        }
        try verifyWindowFit(first: first, resized: resized)
        print("Verified window resize and automatic finalization on close at \(Int(expected.width))×\(Int(expected.height)).")
    }

    private static func checkGeometry() throws {
        let display = CGSize(width: 1000, height: 720)
        for scale: CGFloat in [1, 2] {
            let selection = CGRect(x: 310, y: 238, width: -130, height: -104)
            let result = AreaCrop(selection: selection, displaySize: display, scale: scale)
            guard result?.sourceRect == CGRect(x: 180, y: 482, width: 130, height: 104),
                  result?.width == Int(130 * scale), result?.height == Int(104 * scale) else {
                throw SnipperError.problem("Reverse-drag geometry failed at \(scale)×.")
            }
        }
    }

    private static func record(
        _ source: CaptureSource, windowID: CGWindowID?, audio: AudioOptions, to destination: URL,
        stopWhenReady: Bool = true, onStart: @escaping @MainActor () async -> Void
    ) async throws {
        var recorder: NativeRecorder?
        return try await withCheckedThrowingContinuation { continuation in
            let instance = NativeRecorder { event in
                switch event {
                case .started:
                    Task {
                        await onStart()
                        if stopWhenReady { await recorder?.stop() }
                        // If closing the window does not finish on its own, fail the verification.
                        if !stopWhenReady {
                            try? await Task.sleep(for: .seconds(6))
                            if let active = recorder {
                                recorder = nil
                                continuation.resume(throwing: SnipperError.problem("Closing the source did not stop recording."))
                                await active.stop()
                            }
                        }
                    }
                case .finished(let result):
                    guard recorder != nil else { return }
                    recorder = nil
                    continuation.resume(with: result)
                case .notice, .finishing: break
                }
            }
            recorder = instance
            Task {
                do {
                    try await instance.start(source: source, audio: audio, to: destination, includingOwnWindow: windowID)
                } catch {
                    if recorder != nil {
                        recorder = nil
                        continuation.resume(throwing: error)
                    }
                }
            }
        }
    }

    private static func rasterize(_ image: CGImage) throws -> CGContext {
        guard let context = CGContext(
            data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
            bytesPerRow: image.width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ), context.data != nil else {
            throw SnipperError.problem("Could not read encoded pixels.")
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return context
    }

    private static func verifyWindowFit(first: CGImage, resized: CGImage) throws {
        let original = try rasterize(first)
        let changed = try rasterize(resized)
        let x = first.width / 2
        let y = first.height / 4
        let originalPixel = pixel(in: original, x: x, y: y)
        let resizedMiddle = pixel(in: changed, x: x, y: first.height / 2)
        let resizedEdges = [pixel(in: changed, x: x, y: 5), pixel(in: changed, x: x, y: first.height - 6)]
        guard originalPixel.max() ?? 0 > 120, resizedMiddle.max() ?? 0 > 120,
              resizedEdges.contains(where: { ($0.max() ?? 255) < 35 }) else {
            throw SnipperError.problem("The resized window did not fit inside the original frame.")
        }
    }

    private static func pixel(in context: CGContext, x: Int, y: Int) -> [Int] {
        let bytes = context.data!.assumingMemoryBound(to: UInt8.self)
        let pixel = bytes + (y * context.width + x) * 4
        return [Int(pixel[0]), Int(pixel[1]), Int(pixel[2])]
    }

    private static func verifyColors(in image: CGImage) throws {
        let context = try rasterize(image)
        let corners = [
            (12, 12, [255, 0, 0]),
            (image.width - 13, 12, [0, 255, 0]),
            (12, image.height - 13, [0, 0, 255]),
            (image.width - 13, image.height - 13, [255, 255, 0]),
        ]
        for (x, y, expected) in corners {
            let observed = pixel(in: context, x: x, y: y)
            // Display color profiles and H.264 color conversion can move the exact RGB values.
            guard zip(observed, expected).allSatisfy({ $0.1 == 0 ? $0.0 < 100 : $0.0 > 155 }) else {
                throw SnipperError.problem("Incorrect color at (\(x), \(y)). Expected \(expected), found \(observed).")
            }
        }
    }
}

private final class VerificationGrid: NSView {
    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.magenta.setFill()
        bounds.fill()
        let tile: CGFloat = 106
        let left: CGFloat = 40
        let top: CGFloat = 46
        let width: CGFloat = 281
        let height: CGFloat = 218
        for (color, rect) in [
            ((1, 0, 0), CGRect(x: left, y: top, width: tile, height: tile)),
            ((0, 1, 0), CGRect(x: left + width - tile, y: top, width: tile, height: tile)),
            ((0, 0, 1), CGRect(x: left, y: top + height - tile, width: tile, height: tile)),
            ((1, 1, 0), CGRect(x: left + width - tile, y: top + height - tile, width: tile, height: tile)),
        ] {
            NSColor(srgbRed: CGFloat(color.0), green: CGFloat(color.1), blue: CGFloat(color.2), alpha: 1).setFill()
            rect.fill()
        }
    }
}

private final class AspectGrid: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.white.setFill()
        bounds.fill()
        NSColor.systemBlue.setFill()
        bounds.insetBy(dx: bounds.width / 5, dy: bounds.height / 6).fill()
    }
}
