import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers

struct GIFCaption: Equatable, Sendable {
    enum Position: String, CaseIterable, Identifiable, Sendable {
        case top = "Top", center = "Center", bottom = "Bottom"

        var id: Self { self }
    }

    var text = ""
    var position = Position.bottom

    var hasText: Bool { !trimmedText.isEmpty }

    private var trimmedText: String { text.trimmingCharacters(in: .whitespacesAndNewlines) }

    func writeOverlay(to url: URL, width: Int, height: Int, longestSide: Int) throws {
        let scale = min(1, Double(longestSide) / Double(max(width, height)))
        let width = max(1, Int((Double(width) * scale).rounded()))
        let height = max(1, Int((Double(height) * scale).rounded()))
        let shortSide = min(width, height)
        let margin = CGFloat(max(1, shortSide / 24))
        let padding = CGFloat(min(10, max(2, shortSide / 35)))
        let inset = margin + padding
        let textWidth = CGFloat(width) - 2 * inset
        let textHeight = CGFloat(height) - 2 * inset
        guard textWidth > 0, textHeight > 0 else {
            throw SnipperError.problem("Choose a larger crop to fit the GIF caption.")
        }
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let canvas = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                     space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw SnipperError.problem("Could not create the GIF caption overlay.")
        }

        let content = trimmedText.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        let length = CFRange(location: 0, length: content.utf16.count)
        let paragraph = Self.centeredParagraph()
        for fontSize in stride(from: min(46, max(8, shortSide / 10)), through: 8, by: -1) {
            try Task.checkCancellation()
            let font = CTFontCreateWithName("HelveticaNeue-Bold" as CFString, CGFloat(fontSize), nil)
            let text = NSAttributedString(string: content, attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 1, alpha: 1),
                NSAttributedString.Key(kCTParagraphStyleAttributeName as String): paragraph,
            ])
            let framesetter = CTFramesetterCreateWithAttributedString(text)
            var fitted = CFRange()
            let measured = CTFramesetterSuggestFrameSizeWithConstraints(
                framesetter, length, nil, CGSize(width: textWidth, height: textHeight), &fitted
            )
            guard fitted.length == length.length else { continue }

            let lineHeight = ceil(measured.height)
            let boxWidth = min(textWidth, ceil(measured.width)) + 2 * padding
            let boxHeight = lineHeight + 2 * padding
            let y: CGFloat
            switch position {
            case .top: y = CGFloat(height) - margin - boxHeight
            case .center: y = floor((CGFloat(height) - boxHeight) / 2)
            case .bottom: y = margin
            }
            let bounds = CGRect(x: inset, y: y + padding, width: textWidth, height: lineHeight)
            let frame = CTFramesetterCreateFrame(framesetter, length, CGPath(rect: bounds, transform: nil), nil)
            guard CTFrameGetVisibleStringRange(frame).length == length.length else { continue }

            canvas.setFillColor(CGColor(gray: 0, alpha: 196.0 / 255))
            canvas.fill(CGRect(x: (CGFloat(width) - boxWidth) / 2, y: y, width: boxWidth, height: boxHeight))
            CTFrameDraw(frame, canvas)
            try Task.checkCancellation()
            guard let image = canvas.makeImage(), let destination = CGImageDestinationCreateWithURL(
                url as CFURL, UTType.png.identifier as CFString, 1, nil
            ) else {
                throw SnipperError.problem("Could not create the GIF caption overlay.")
            }
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else {
                throw SnipperError.problem("Could not write the GIF caption overlay.")
            }
            return
        }
        throw SnipperError.problem("The caption is too long for this GIF. Shorten the text or choose a larger crop.")
    }

    private static func centeredParagraph() -> CTParagraphStyle {
        var alignment = CTTextAlignment.center
        return withUnsafePointer(to: &alignment) { value in
            var setting = CTParagraphStyleSetting(spec: .alignment, valueSize: MemoryLayout<CTTextAlignment>.size,
                                                  value: value)
            return CTParagraphStyleCreate(&setting, 1)
        }
    }
}
