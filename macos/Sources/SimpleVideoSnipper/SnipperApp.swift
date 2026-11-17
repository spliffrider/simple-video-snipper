import AppKit
import SwiftUI

@main
struct SnipperApp: App {
    @NSApplicationDelegateAdaptor private var delegate: SnipperDelegate

    var body: some Scene {
        MenuBarExtra {
            SnipperMenu(model: delegate.model)
        } label: {
            MenuBarIcon(model: delegate.model)
        }
        .menuBarExtraStyle(.menu)
    }
}

private struct MenuBarIcon: View {
    @ObservedObject var model: SnipperModel

    var body: some View {
        Image(systemName: model.isCapturing ? "record.circle.fill" : "record.circle")
            .accessibilityLabel("Simple Video Snipper")
    }
}

private struct SnipperMenu: View {
    @ObservedObject var model: SnipperModel

    var body: some View {
        if model.phase != .idle {
            Text(model.phase.label)
            if let notice = model.captureNotice { Text(notice) }
            Divider()
        }
        Button("Record Area…") { model.recordArea() }.disabled(!model.canStart)
        Button("Record Window…") { model.recordWindow() }.disabled(!model.canStart)
        Button("Record Chrome Tab…") { model.recordChromeTab() }.disabled(!model.canStart)
        Button(model.phase == .browser ? "Cancel Chrome Recorder" : "Stop Recording") { model.stop() }
            .disabled(!model.canStop)
        Divider()
        Button("Unsaved Recordings…") { model.showPending() }.disabled(!model.canStart)
        Button("Settings…") { model.showSettings() }.disabled(!model.canStart)
        Divider()
        Button("Quit Simple Video Snipper") { model.quit() }
            .disabled(!model.canStart && model.phase != .browser)
    }
}

@MainActor
final class SnipperDelegate: NSObject, NSApplicationDelegate {
    let model = SnipperModel()

    func applicationDidFinishLaunching(_ notification: Notification) {
        let args = CommandLine.arguments
        guard args.count == 3, [
            "--verify-crop", "--verify-window", "--verify-system-audio",
            "--verify-companion", "--verify-companion-stop",
        ].contains(args[1]) else {
            model.launch()
            return
        }
        Task {
            do {
                let destination = URL(fileURLWithPath: args[2])
                switch args[1] {
                case "--verify-window":
                    try await CropVerification.verifyWindow(to: destination)
                case "--verify-companion", "--verify-companion-stop":
                    try await CompanionVerification.run(in: destination, stopFromDesktop: args[1] == "--verify-companion-stop")
                default:
                    try await CropVerification.run(to: destination, systemAudio: args[1] == "--verify-system-audio")
                }
                exit(0)
            } catch {
                fputs("\(error.localizedDescription)\n", stderr)
                exit(1)
            }
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        model.isCapturing || model.phase == .saving ? .terminateCancel : .terminateNow
    }
}
