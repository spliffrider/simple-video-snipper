import AppKit
import ScreenCaptureKit

// SCStreamConfiguration holds an unretained CGColor until SCStream copies the configuration.
private let recordingBackground = CGColor(gray: 0, alpha: 1)

enum SnipperError: LocalizedError {
    case problem(String)

    var errorDescription: String? {
        switch self {
        case .problem(let message): message
        }
    }
}

struct AreaCrop {
    let sourceRect: CGRect
    let width: Int
    let height: Int

    init?(selection: CGRect, displaySize: CGSize, scale: CGFloat) {
        guard scale.isFinite, scale > 0, !selection.isEmpty else { return nil }
        let local = selection.standardized.intersection(CGRect(origin: .zero, size: displaySize))
        guard !local.isNull else { return nil }
        let left = Int((local.minX * scale).rounded())
        let top = Int(((displaySize.height - local.maxY) * scale).rounded())
        let right = Int((local.maxX * scale).rounded())
        let bottom = Int(((displaySize.height - local.minY) * scale).rounded())
        width = (right - left) / 2 * 2
        height = (bottom - top) / 2 * 2
        guard width >= 2, height >= 2 else { return nil }

        sourceRect = CGRect(
            x: CGFloat(left) / scale, y: CGFloat(top) / scale,
            width: CGFloat(width) / scale, height: CGFloat(height) / scale
        )
    }

    func selection(displayHeight: CGFloat) -> CGRect {
        CGRect(x: sourceRect.minX, y: displayHeight - sourceRect.maxY,
               width: sourceRect.width, height: sourceRect.height)
    }
}

extension NSScreen {
    var displayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
    }
}

enum CaptureSource {
    case area(display: SCDisplay, crop: AreaCrop)
    case window(SCWindow)

    var title: String {
        switch self {
        case .area: "Area"
        case .window: "Window"
        }
    }

    var windowID: CGWindowID? {
        if case .window(let window) = self { window.windowID } else { nil }
    }

    func streamSetup() throws -> (SCContentFilter, SCStreamConfiguration) {
        let configuration = SCStreamConfiguration()
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 30)
        configuration.queueDepth = 5
        configuration.showsCursor = true
        configuration.showMouseClicks = false
        configuration.scalesToFit = true
        configuration.preservesAspectRatio = true
        configuration.backgroundColor = recordingBackground
        let filter: SCContentFilter

        switch self {
        case .area(let display, let crop):
            filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
            configuration.sourceRect = crop.sourceRect
            configuration.width = crop.width
            configuration.height = crop.height
        case .window(let window):
            filter = SCContentFilter(desktopIndependentWindow: window)
            let size = filter.contentRect.size
            let scale = CGFloat(filter.pointPixelScale)
            configuration.width = Int((size.width * scale).rounded(.down)) / 2 * 2
            configuration.height = Int((size.height * scale).rounded(.down)) / 2 * 2
            configuration.ignoreShadowsSingleWindow = true
        }
        guard configuration.width >= 2, configuration.height >= 2 else {
            throw SnipperError.problem("The selected source is too small to record.")
        }
        return (filter, configuration)
    }
}
