import AppKit
import KeyboardShortcuts
import SwiftUI

extension KeyboardShortcuts.Name {
    static let recordArea = Self("recordArea", default: .init(.r, modifiers: [.control, .option, .shift]))
}

struct StorageSetupView: View {
    let choose: () -> Void
    let useDefault: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Temporary recordings folder").font(.title2.weight(.semibold))
            Text("This is where recordings are held until you save or discard them.")
            Text(RecordingStorage.defaultStaging.path(percentEncoded: false))
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            Spacer()
            HStack {
                Button("Choose Folder…", action: choose)
                Spacer()
                Button("Use This Folder", action: useDefault)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
    }
}

struct SnipperSettingsView: View {
    @ObservedObject var model: SnipperModel
    @AppStorage("quickSave") private var quickSave = false
    @AppStorage("preferredFormat") private var format = ClipFormat.mp4
    @AppStorage("autoCopy") private var autoCopy = false

    var body: some View {
        Form {
            Section("Storage") {
                LabeledContent("Temporary folder") {
                    Button("Choose…") { model.chooseStagingFolder() }
                }
                Text(model.stagingPath)
                    .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                LabeledContent("Save folder") {
                    Button("Choose…") { model.chooseExportFolder() }
                }
                Text(model.exportPath)
                    .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
            Section("Saving") {
                Picker("After recording", selection: $quickSave) {
                    Text("Ask every time").tag(false)
                    Text("Quick Save").tag(true)
                }
                Picker("Format", selection: $format) {
                    ForEach(ClipFormat.allCases) { Text($0.label).tag($0) }
                }
                Toggle("Copy the saved file to the clipboard", isOn: $autoCopy)
            }
            Section("Shortcut") {
                ShortcutSetting()
            }
        }
        .formStyle(.grouped)
        .padding(.vertical, 8)
    }
}

struct PendingClipsView: View {
    let clips: [PendingClip]
    let open: (PendingClip) -> Void
    let discard: (PendingClip) -> Void

    @State private var choice: URL?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if clips.isEmpty {
                ContentUnavailableView("No unsaved recordings", systemImage: "film")
            } else {
                List(clips, selection: $choice) { clip in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(clip.title)
                        Text(clip.format.label).font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 2)
                    .tag(clip.id)
                }
            }
            HStack {
                Button("Discard…", role: .destructive) {
                    if let selected { discard(selected) }
                }
                .disabled(selected == nil)
                Spacer()
                Button("Continue") {
                    if let selected { open(selected) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selected == nil)
            }
        }
        .padding(16)
        .onAppear { choice = clips.first?.id }
    }

    private var selected: PendingClip? { clips.first { $0.id == choice } }
}

struct SavedClipView: View {
    let url: URL
    let initiallyCopied: Bool

    @State private var copyStatus: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(url.lastPathComponent)
                .font(.headline)
                .lineLimit(2)
                .help(url.path)
            if let copyStatus {
                Text(copyStatus)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            HStack {
                Button("Open") { NSWorkspace.shared.open(url) }
                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                Spacer()
                Button("Copy video file") {
                    copyStatus = Self.copy(url) ? "Copied" : "Could not copy the file. Try again."
                }
            }
        }
        .padding(18)
        .onAppear { if initiallyCopied { copyStatus = "Copied" } }
    }

    static func copy(_ url: URL) -> Bool {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        return pasteboard.writeObjects([url as NSURL])
    }
}

@MainActor
final class SavePanelFormat {
    private let formatPicker = NSPopUpButton()
    private weak var panel: NSSavePanel?
    var format: ClipFormat { ClipFormat.allCases[max(0, formatPicker.indexOfSelectedItem)] }

    init(panel: NSSavePanel, format: ClipFormat) {
        self.panel = panel
        formatPicker.addItems(withTitles: ClipFormat.allCases.map(\.label))
        formatPicker.selectItem(at: ClipFormat.allCases.firstIndex(of: format) ?? 0)
        formatPicker.target = self
        formatPicker.action = #selector(changeFormat)
        let stack = NSStackView(views: [NSTextField(labelWithString: "Format"), formatPicker])
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 8, left: 12, bottom: 8, right: 12)
        stack.frame = NSRect(x: 0, y: 0, width: 270, height: 38)
        panel.accessoryView = stack
        changeFormat()
    }

    @objc private func changeFormat() {
        guard let panel else { return }
        panel.allowedContentTypes = [format.contentType]
        if let ext = ClipFormat(rawValue: (panel.nameFieldStringValue as NSString).pathExtension.lowercased()) {
            if ext != format {
                panel.nameFieldStringValue = (panel.nameFieldStringValue as NSString).deletingPathExtension
                    + "." + format.rawValue
            }
        }
    }
}

struct RecordingReadyView: View {
    let title: String
    let save: () -> Void
    let edit: () -> Void
    let later: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(title)
                .font(.headline)
                .lineLimit(2)
            Spacer(minLength: 0)
            HStack {
                Button("Keep for later", action: later).keyboardShortcut(.cancelAction)
                Spacer()
                Button("Edit", action: edit)
                Button("Save", action: save).keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
    }
}
