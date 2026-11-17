import AppKit

@MainActor
final class AreaSelection {
    private var panel: NSPanel?
    private var complete: ((AreaCrop?) -> Void)?

    func show(on screen: NSScreen, scale: CGFloat, complete: @escaping (AreaCrop?) -> Void) {
        self.complete = complete
        let panel = SelectionPanel(
            contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false, screen: screen
        )
        panel.setFrame(screen.frame, display: false)
        panel.isReleasedWhenClosed = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hasShadow = false
        panel.acceptsMouseMovedEvents = true

        let view = SelectionView(frame: CGRect(origin: .zero, size: screen.frame.size), scale: scale)
        view.complete = { [weak self] in self?.finish($0) }
        panel.contentView = view
        self.panel = panel
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(view)
    }

    func cancel() { finish(nil) }

    private func finish(_ crop: AreaCrop?) {
        panel?.orderOut(nil)
        panel = nil
        let callback = complete
        complete = nil
        callback?(crop)
    }
}

private final class SelectionPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class SelectionView: NSView {
    private let scale: CGFloat
    private var anchor: CGPoint?
    private var crop: AreaCrop?
    private var draggingExisting = false
    private let directions = NSTextField(labelWithString: "Drag to select an area")
    private let confirm = NSButton(title: "Continue", target: nil, action: nil)

    var complete: ((AreaCrop?) -> Void)?

    init(frame: CGRect, scale: CGFloat) {
        self.scale = scale
        super.init(frame: frame)
        directions.textColor = .white
        directions.font = .systemFont(ofSize: 15, weight: .semibold)
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancelSelection))
        cancel.keyEquivalent = "\u{1b}"
        confirm.target = self
        confirm.action = #selector(confirmSelection)
        confirm.keyEquivalent = "\r"
        confirm.isEnabled = false
        let controls = NSStackView(views: [directions, cancel, confirm])
        controls.spacing = 12
        controls.translatesAutoresizingMaskIntoConstraints = false
        addSubview(controls)
        NSLayoutConstraint.activate([
            controls.centerXAnchor.constraint(equalTo: centerXAnchor),
            controls.bottomAnchor.constraint(equalTo: bottomAnchor, constant: 38),
        ])
    }

    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
        if let handle {
            addCursorRect(handle.insetBy(dx: -5, dy: -5), cursor: .crosshair)
        }
    }

    private var selection: CGRect? { crop?.selection(displayHeight: bounds.height) }
    private var handle: CGRect? {
        selection.map { CGRect(x: $0.maxX - 5, y: $0.minY - 5, width: 10, height: 10) }
    }

    override func draw(_ dirtyRect: NSRect) {
        let shade = NSBezierPath(rect: bounds)
        if let selection { shade.appendRect(selection) }
        shade.windingRule = .evenOdd
        NSColor.black.withAlphaComponent(0.54).setFill()
        shade.fill()
        if let selection, let handle {
            NSColor.white.setStroke()
            let outline = NSBezierPath(rect: selection)
            outline.lineWidth = 1.5
            outline.stroke()
            NSColor.controlAccentColor.setFill()
            NSBezierPath(roundedRect: handle, xRadius: 2, yRadius: 2).fill()
        }
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if let selection, handle?.insetBy(dx: -6, dy: -6).contains(point) == true {
            draggingExisting = true
            anchor = CGPoint(x: selection.minX, y: selection.maxY)
        } else {
            draggingExisting = false
            anchor = point
            update(nil)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let anchor else { return }
        let point = convert(event.locationInWindow, from: nil)
        let x = draggingExisting ? max(point.x, anchor.x + 2) : point.x
        let y = draggingExisting ? min(point.y, anchor.y - 2) : point.y
        let rect = CGRect(x: anchor.x, y: anchor.y, width: x - anchor.x, height: y - anchor.y).standardized
        update(AreaCrop(selection: rect, displaySize: bounds.size, scale: scale))
    }

    override func mouseUp(with event: NSEvent) { anchor = nil }
    override func cancelOperation(_ sender: Any?) { cancelSelection() }

    private func update(_ crop: AreaCrop?) {
        self.crop = crop
        if let crop {
            directions.stringValue = "\(crop.width) × \(crop.height) px"
            confirm.isEnabled = true
        } else {
            directions.stringValue = "Drag to select an area"
            confirm.isEnabled = false
        }
        needsDisplay = true
    }

    @objc private func cancelSelection() { complete?(nil) }
    @objc private func confirmSelection() {
        if let crop { complete?(crop) }
    }
}
