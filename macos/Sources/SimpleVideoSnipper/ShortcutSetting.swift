import AppKit
import KeyboardShortcuts
import SwiftUI

struct ShortcutSetting: View {
    @ObservedObject var shortcut: GlobalSnipShortcut
    @State private var keyCode: Int?
    @State private var modifiers: NSEvent.ModifierFlags
    @State private var issue: String?

    private static let keys: [KeyboardShortcuts.Key] = [
        .a, .b, .c, .d, .e, .f, .g, .h, .i, .j, .k, .l, .m, .n, .o, .p, .q, .r, .s, .t, .u, .v, .w, .x, .y, .z,
        .zero, .one, .two, .three, .four, .five, .six, .seven, .eight, .nine,
        .f1, .f2, .f3, .f4, .f5, .f6, .f7, .f8, .f9, .f10, .f11, .f12, .f13, .f14, .f15, .f16, .f17, .f18, .f19, .f20,
        .space, .tab, .escape, .return, .delete, .deleteForward,
        .leftArrow, .rightArrow, .upArrow, .downArrow, .home, .end, .pageUp, .pageDown, .help,
        .backslash, .backtick, .comma, .equal, .minus, .period, .quote, .semicolon, .slash, .leftBracket, .rightBracket,
        .keypad0, .keypad1, .keypad2, .keypad3, .keypad4, .keypad5, .keypad6, .keypad7, .keypad8, .keypad9,
        .keypadClear, .keypadDecimal, .keypadDivide, .keypadEnter, .keypadEquals, .keypadMinus, .keypadMultiply, .keypadPlus,
    ]

    init(shortcut: GlobalSnipShortcut) {
        self.shortcut = shortcut
        _keyCode = State(initialValue: shortcut.current?.carbonKeyCode)
        _modifiers = State(initialValue: shortcut.current?.modifiers ?? GlobalSnipShortcut.defaultShortcut.modifiers)
    }

    private var draft: KeyboardShortcuts.Shortcut? {
        keyCode.map { KeyboardShortcuts.Shortcut(.init(rawValue: $0), modifiers: modifiers) }
    }

    var body: some View {
        LabeledContent("Active shortcut", value: shortcut.current?.description ?? "None")
        HStack {
            Text("Modifiers")
            Spacer()
            HStack(spacing: 12) {
                modifier("Control", .control)
                modifier("Option", .option)
                modifier("Shift", .shift)
                modifier("Command", .command)
            }
            .toggleStyle(.checkbox)
            .disabled(keyCode == nil)
            .accessibilityElement(children: .contain)
        }
        .accessibilityElement(children: .contain)
        Picker("Key", selection: $keyCode) {
            Text("None").tag(nil as Int?)
            if let keyCode, !Self.keys.contains(where: { $0.rawValue == keyCode }) {
                Text(keyName(.init(rawValue: keyCode))).tag(Optional(keyCode))
            }
            ForEach(Self.keys, id: \.rawValue) { key in
                Text(keyName(key)).tag(Optional(key.rawValue))
            }
        }
        .onChange(of: draft) { _, _ in issue = nil }
        HStack {
            Button("Use Default") {
                keyCode = GlobalSnipShortcut.defaultShortcut.carbonKeyCode
                modifiers = GlobalSnipShortcut.defaultShortcut.modifiers
            }
            Spacer()
            Button("Save Shortcut") {
                do {
                    try shortcut.save(draft)
                    issue = nil
                } catch {
                    issue = error.localizedDescription
                }
            }
            .disabled(draft == shortcut.current && shortcut.startupIssue == nil)
        }
        if let issue = issue ?? shortcut.startupIssue {
            Text(issue).font(.callout).foregroundStyle(.red)
        }
    }

    private func modifier(_ name: String, _ flag: NSEvent.ModifierFlags) -> some View {
        Toggle(name, isOn: Binding(get: { modifiers.contains(flag) }, set: { selected in
            if selected { modifiers.insert(flag) } else { modifiers.remove(flag) }
        }))
    }

    private func keyName(_ key: KeyboardShortcuts.Key) -> String {
        let shortcut = KeyboardShortcuts.Shortcut(key)
        return shortcut.description == "�" ? "Key \(key.rawValue)" : shortcut.description
    }
}
