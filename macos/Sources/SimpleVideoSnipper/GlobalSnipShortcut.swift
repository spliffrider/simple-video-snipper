import AppKit
import Carbon.HIToolbox
import KeyboardShortcuts

@MainActor
final class GlobalSnipShortcut: ObservableObject {
    typealias Shortcut = KeyboardShortcuts.Shortcut
    static let defaultShortcut = Shortcut(.r, modifiers: [.control, .option, .shift])

    @Published private(set) var current: Shortcut? = defaultShortcut
    @Published private(set) var startupIssue: String?

    private struct Stored: Codable {
        let shortcut: Shortcut?
    }

    private struct Registration {
        let id: UInt32
        let reference: EventHotKeyRef
    }

    private static let signature: UInt32 = 0x53565350
    private let preferences: URL
    private var registration: Registration?
    private var eventHandler: EventHandlerRef?
    private var nextID: UInt32 = 0
    private var onRelease: (() -> Void)?

    init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let identity = Bundle.main.bundleIdentifier ?? "io.github.spliffrider.SimpleVideoSnipper"
        preferences = support.appendingPathComponent("Simple Video Snipper/Shortcuts/\(identity).json")
    }

    deinit {
        if let registration { UnregisterEventHotKey(registration.reference) }
        if let eventHandler { RemoveEventHandler(eventHandler) }
    }

    func start(onRelease: @escaping () -> Void) {
        self.onRelease = onRelease
        do {
            current = try JSONDecoder().decode(Stored.self, from: Data(contentsOf: preferences)).shortcut
        } catch {
            current = legacyShortcut()
            if (error as? CocoaError)?.code != .fileReadNoSuchFile {
                startupIssue = "Could not read the saved shortcut. Save a shortcut to repair the preference."
            }
        }

        if let current {
            do {
                registration = try register(current)
            } catch {
                startupIssue = error.localizedDescription
            }
        }
    }

    func save(_ shortcut: Shortcut?) throws {
        // Keep the old hotkey registered while testing the replacement and writing preferences.
        let reuseRegistration = shortcut == current && registration != nil
        let replacement = reuseRegistration ? registration : try shortcut.map(register)
        do {
            try FileManager.default.createDirectory(at: preferences.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(Stored(shortcut: shortcut))
            try data.write(to: preferences, options: .atomic)
        } catch {
            if !reuseRegistration, let replacement { UnregisterEventHotKey(replacement.reference) }
            let recovery = registration == nil ? "" : " The previous shortcut is still active."
            throw SnipperError.problem("Could not save the shortcut.\(recovery) \(error.localizedDescription)")
        }

        if !reuseRegistration, let registration { UnregisterEventHotKey(registration.reference) }
        registration = replacement
        current = shortcut
        startupIssue = nil
    }

    private func legacyShortcut() -> Shortcut? {
        let key = "KeyboardShortcuts_recordArea"
        let defaults = UserDefaults.standard
        if let data = defaults.string(forKey: key)?.data(using: .utf8),
           let shortcut = try? JSONDecoder().decode(Shortcut.self, from: data) {
            return shortcut
        }
        if (defaults.object(forKey: key) as? Bool) == false { return nil }
        return Self.defaultShortcut
    }

    private func register(_ shortcut: Shortcut) throws -> Registration {
        guard !shortcut.modifiers.isEmpty, let keyCode = UInt32(exactly: shortcut.carbonKeyCode),
              let modifiers = UInt32(exactly: shortcut.carbonModifiers) else {
            throw SnipperError.problem("Choose at least one modifier for the global shortcut.")
        }
        guard !Self.isSystemShortcut(shortcut) else {
            throw SnipperError.problem("\(shortcut) is already used by macOS. Choose another shortcut.")
        }
        try installHandler()
        nextID &+= 1
        let identifier = EventHotKeyID(signature: Self.signature, id: nextID)
        var reference: EventHotKeyRef?
        let status = RegisterEventHotKey(keyCode, modifiers, identifier, GetEventDispatcherTarget(),
                                         OptionBits(kEventHotKeyExclusive), &reference)
        guard status == noErr, let reference else {
            throw SnipperError.problem("macOS could not register \(shortcut). Choose another shortcut.")
        }
        return Registration(id: identifier.id, reference: reference)
    }

    private func installHandler() throws {
        guard eventHandler == nil else { return }
        let eventTypes = [
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased)),
        ]
        let context = Unmanaged.passUnretained(self).toOpaque()
        let status = InstallEventHandler(GetEventDispatcherTarget(), { _, event, context in
            guard let event, let context else { return OSStatus(eventNotHandledErr) }
            let shortcut = Unmanaged<GlobalSnipShortcut>.fromOpaque(context).takeUnretainedValue()
            return MainActor.assumeIsolated { shortcut.receive(event) }
        }, eventTypes.count, eventTypes, context, &eventHandler)
        guard status == noErr else {
            throw SnipperError.problem("Could not start the global shortcut handler.")
        }
    }

    private func receive(_ event: EventRef) -> OSStatus {
        var identifier = EventHotKeyID()
        let status = GetEventParameter(event, UInt32(kEventParamDirectObject), UInt32(typeEventHotKeyID),
                                       nil, MemoryLayout<EventHotKeyID>.size, nil, &identifier)
        guard status == noErr, identifier.signature == Self.signature, identifier.id == registration?.id else {
            return OSStatus(eventNotHandledErr)
        }
        if GetEventKind(event) == UInt32(kEventHotKeyReleased) { onRelease?() }
        return noErr
    }

    private static func isSystemShortcut(_ candidate: Shortcut) -> Bool {
        var result: Unmanaged<CFArray>?
        guard CopySymbolicHotKeys(&result) == noErr,
              let entries = result?.takeRetainedValue() as? [[String: Any]] else { return false }
        return entries.contains {
            guard $0[kHISymbolicHotKeyEnabled] as? Bool == true,
                  let keyCode = $0[kHISymbolicHotKeyCode] as? Int,
                  let modifiers = $0[kHISymbolicHotKeyModifiers] as? Int else { return false }
            return candidate == Shortcut(carbonKeyCode: keyCode, carbonModifiers: modifiers)
        }
    }
}
