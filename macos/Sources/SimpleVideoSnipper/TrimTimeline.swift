import SwiftUI

struct TrimTimeline: View {
    @ObservedObject var model: ClipEditorModel
    @FocusState private var focused: Bool
    @State private var drag: DragTarget?

    private enum DragTarget {
        case start(offset: CGFloat)
        case end(offset: CGFloat)
        case playhead
    }

    private var step: Double { max(1.0 / 30, model.media.info.duration / 300) }

    var body: some View {
        GeometryReader { geometry in
            let track = Track(width: geometry.size.width, height: geometry.size.height,
                              duration: model.media.info.duration)
            let start = track.x(at: model.edits.start)
            let end = track.x(at: model.edits.end)
            ZStack(alignment: .topLeading) {
                Capsule()
                    .fill(.secondary.opacity(0.28))
                    .frame(width: track.width, height: 6)
                    .position(x: track.inset + track.width / 2, y: track.center)
                Capsule()
                    .fill(Color.accentColor.opacity(0.85))
                    .frame(width: max(1, end - start), height: 6)
                    .position(x: (start + end) / 2, y: track.center)
                RoundedRectangle(cornerRadius: 1)
                    .fill(.primary)
                    .frame(width: 2, height: 25)
                    .position(x: track.x(at: model.position), y: track.center)
                handle(in: track.handle(at: start, isStart: true))
                handle(in: track.handle(at: end, isStart: false))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { value in
                    if drag == nil {
                        focused = true
                        let origin = value.startLocation
                        if track.handle(at: start, isStart: true).contains(origin) {
                            drag = .start(offset: origin.x - start)
                        } else if track.handle(at: end, isStart: false).contains(origin) {
                            drag = .end(offset: origin.x - end)
                        } else {
                            drag = .playhead
                        }
                    }
                    move(to: value.location.x, on: track)
                }
                .onEnded { value in
                    move(to: value.location.x, on: track)
                    drag = nil
                })
        }
        .frame(height: 34)
        .focusable()
        .focused($focused)
        .onMoveCommand { direction in
            if direction == .left { scrub(by: -step) }
            if direction == .right { scrub(by: step) }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Scrub recording")
        .accessibilityValue(model.position.formatted(.number.precision(.fractionLength(2))) + " seconds")
        .accessibilityHint("Drag either handle to trim, or use Set start and Set end at the playhead.")
        .accessibilityAdjustableAction { direction in
            switch direction {
            case .increment: scrub(by: step)
            case .decrement: scrub(by: -step)
            @unknown default: break
            }
        }
        .accessibilityAction(named: Text("Set start at playhead")) { model.setStart() }
        .accessibilityAction(named: Text("Set end at playhead")) { model.setEnd() }
        .accessibilityAction(named: Text("Reset trim")) { model.resetTrim() }
        .help("Drag the handles to trim. Click the track to scrub.")
    }

    private func handle(in rect: CGRect) -> some View {
        RoundedRectangle(cornerRadius: 4)
            .fill(Color.accentColor)
            .overlay {
                Capsule().fill(.white.opacity(0.88)).frame(width: 2, height: 11)
            }
            .frame(width: rect.width, height: rect.height)
            .position(x: rect.midX, y: rect.midY)
    }

    private func move(to x: CGFloat, on track: Track) {
        switch drag {
        case .start(let offset): model.moveTrimStart(to: track.time(at: x - offset))
        case .end(let offset): model.moveTrimEnd(to: track.time(at: x - offset))
        case .playhead: model.position = track.time(at: x)
        case nil: break
        }
    }

    private func scrub(by difference: Double) {
        model.position = min(model.media.info.duration, max(0, model.position + difference))
    }

    private struct Track {
        let width: CGFloat
        let center: CGFloat
        let duration: Double
        let inset: CGFloat = 16

        init(width: CGFloat, height: CGFloat, duration: Double) {
            self.width = max(1, width - inset * 2)
            center = height / 2
            self.duration = duration
        }

        func x(at time: Double) -> CGFloat { inset + width * CGFloat(time / duration) }

        func time(at x: CGFloat) -> Double {
            duration * Double(min(1, max(0, (x - inset) / width)))
        }

        func handle(at x: CGFloat, isStart: Bool) -> CGRect {
            let handleWidth: CGFloat = 13
            return CGRect(x: isStart ? x - handleWidth : x, y: center - 14, width: handleWidth, height: 28)
        }
    }
}
