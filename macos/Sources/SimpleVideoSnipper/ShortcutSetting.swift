import AppKit
import Carbon.HIToolbox
import KeyboardShortcuts
import SwiftUI

struct ShortcutSetting: View {
    @StateObject private var recorder = ShortcutRecorder()

    var body: some View {
        LabeledContent("Record area") {
            Button(recorder.buttonLabel) { recorder.begin() }
                .disabled(recorder.isRecording)
        }
        if let message = recorder.message {
            Text(message).foregroundStyle(.secondary).font(.caption)
        }
        Color.clear.frame(height: 0)
            .onDisappear { recorder.cancel() }
            .onReceive(NotificationCenter.default.publisher(for: NSWindow.didResignKeyNotification)) { _ in
                recorder.cancel()
            }
    }
}

@MainActor
private final class ShortcutRecorder: ObservableObject {
    @Published private var current = KeyboardShortcuts.getShortcut(for: .recordArea)
    @Published private(set) var isRecording = false
    @Published private(set) var message: String?
    private var monitor: Any?

    var buttonLabel: String {
        if isRecording { return "Press Shortcut…" }
        return current.map(\.description) ?? "Set Shortcut…"
    }

    func begin() {
        guard monitor == nil else { return }
        KeyboardShortcuts.disable(.recordArea)
        isRecording = true
        message = "Press a modified key. Escape cancels and Delete clears the shortcut."
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            receive(event)
            return nil
        }
    }

    func cancel() {
        guard let monitor else { return }
        NSEvent.removeMonitor(monitor)
        self.monitor = nil
        isRecording = false
        message = nil
        KeyboardShortcuts.enable(.recordArea)
    }

    private func receive(_ event: NSEvent) {
        let modifiers = event.modifierFlags.intersection([.control, .option, .command, .shift])
        if modifiers.isEmpty && event.keyCode == kVK_Escape {
            cancel()
            return
        }
        if modifiers.isEmpty && [kVK_Delete, kVK_ForwardDelete].contains(Int(event.keyCode)) {
            store(nil)
            return
        }
        guard !modifiers.intersection([.control, .option, .command]).isEmpty,
              let shortcut = KeyboardShortcuts.Shortcut(event: event) else {
            message = "Include Control, Option, or Command in the shortcut."
            return
        }

        var reference: EventHotKeyRef?
        let identifier = EventHotKeyID(signature: 0x53565350, id: 1)
        let status = RegisterEventHotKey(
            UInt32(shortcut.carbonKeyCode), UInt32(shortcut.carbonModifiers), identifier,
            GetEventDispatcherTarget(), OptionBits(kEventHotKeyExclusive), &reference
        )
        guard status == noErr, let reference else {
            message = "That shortcut is in use. Try another combination."
            return
        }
        UnregisterEventHotKey(reference)
        store(shortcut)
    }

    private func store(_ shortcut: KeyboardShortcuts.Shortcut?) {
        KeyboardShortcuts.setShortcut(shortcut, for: .recordArea)
        current = shortcut
        cancel()
    }
}
