import Darwin
import Foundation
import UniformTypeIdentifiers

enum ClipFormat: String, CaseIterable, Identifiable, Sendable {
    case mp4, webm

    var id: Self { self }
    var label: String {
        switch self {
        case .mp4: "MP4 (H.264 / AAC)"
        case .webm: "WebM (VP9 / Opus)"
        }
    }
    var contentType: UTType {
        switch self {
        case .mp4: .mpeg4Movie
        case .webm: UTType(filenameExtension: "webm") ?? .movie
        }
    }
}

struct PendingClip: Identifiable, Sendable {
    let url: URL
    let format: ClipFormat

    var id: URL { url }
    var title: String { url.deletingPathExtension().lastPathComponent }
}

struct ClipDraft: Sendable {
    let url: URL
    let clip: PendingClip

    func publish() throws -> PendingClip {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard (attributes[.size] as? NSNumber)?.int64Value ?? 0 > 0 else {
            throw SnipperError.problem("The capture contained no data.")
        }
        try FileManager.default.moveItem(at: url, to: clip.url)
        return clip
    }

    func removeIfEmpty() {
        if let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
           let size = attributes[.size] as? NSNumber, size.int64Value == 0 {
            try? FileManager.default.removeItem(at: url)
        }
    }
}

struct RecordingStorage {
    private let defaults = UserDefaults.standard
    private static let stagingDirectoryName = "Simple Video Snipper Pending"

    static var defaultStaging: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Simple Video Snipper/Recordings", isDirectory: true)
    }

    static var defaultExport: URL {
        FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Simple Video Snipper", isDirectory: true)
    }

    var root: URL? { defaults.string(forKey: "recordingsFolder").map { URL(fileURLWithPath: $0, isDirectory: true) } }

    var stagingDirectories: [URL] {
        let previous = (defaults.stringArray(forKey: "recordingFolders") ?? [])
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
        return (previous + (root.map { [$0] } ?? [])).map {
            $0.appendingPathComponent(Self.stagingDirectoryName, isDirectory: true)
        }
    }

    func updateRoot(_ folder: URL) throws {
        let resolved = folder.resolvingSymlinksInPath().standardizedFileURL
        let staging = resolved.appendingPathComponent(Self.stagingDirectoryName, isDirectory: true)
        try Self.probe(staging)
        var previous = Set(defaults.stringArray(forKey: "recordingFolders") ?? [])
        if let root, root.path != resolved.path { previous.insert(root.path) }
        previous.remove(resolved.path)
        defaults.set(previous.sorted(), forKey: "recordingFolders")
        defaults.set(resolved.path, forKey: "recordingsFolder")
    }

    func draft(source: String, format: ClipFormat) throws -> ClipDraft {
        guard let root else { throw SnipperError.problem("Choose a temporary recordings folder first.") }
        let folder = root.appendingPathComponent(Self.stagingDirectoryName, isDirectory: true)
        try Self.probe(folder)
        let id = UUID().uuidString
        let stamp = Date.now.formatted(Date.ISO8601FormatStyle(timeZone: .current).year().month().day().time(includingFractionalSeconds: false)
            .timeSeparator(.colon).dateTimeSeparator(.space))
            .replacingOccurrences(of: ":", with: "-")
        let title = "Snip \(source) \(stamp) \(id.prefix(8))"
        return ClipDraft(
            url: folder.appendingPathComponent(".recording-\(id).\(format.rawValue)"),
            clip: PendingClip(url: folder.appendingPathComponent("\(title).\(format.rawValue)"), format: format)
        )
    }

    func pending() -> [PendingClip] {
        let clips: [PendingClip] = stagingDirectories.flatMap { directory -> [PendingClip] in
            let urls = (try? FileManager.default.contentsOfDirectory(
                at: directory, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey],
                options: [.skipsHiddenFiles]
            )) ?? []
            return urls.compactMap { url -> PendingClip? in
                guard url.lastPathComponent.hasPrefix("Snip "),
                      let format = ClipFormat(rawValue: url.pathExtension.lowercased()),
                      let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey]),
                      values.isRegularFile == true, values.isSymbolicLink != true else { return nil }
                return PendingClip(url: url, format: format)
            }
        }
        return clips.sorted { $0.title.localizedStandardCompare($1.title) == .orderedDescending }
    }

    func containsStaging(_ url: URL) -> Bool {
        let path = url.deletingLastPathComponent().resolvingSymlinksInPath().standardizedFileURL.path
        return stagingDirectories.contains { directory in
            let stagingPath = directory.resolvingSymlinksInPath().standardizedFileURL.path
            return path == stagingPath || path.hasPrefix(stagingPath + "/")
        }
    }

    static func probe(_ folder: URL) throws {
        let files = FileManager.default
        try files.createDirectory(at: folder, withIntermediateDirectories: true)
        let volume = try folder.resourceValues(forKeys: [.volumeIsLocalKey, .volumeAvailableCapacityKey])
        guard volume.volumeIsLocal != false else {
            throw SnipperError.problem("Choose a folder on a local drive.")
        }
        if let available = volume.volumeAvailableCapacity, available < 10 * 1024 * 1024 {
            throw SnipperError.problem("Free at least 10 MB in the recordings folder before recording.")
        }
        let probe = folder.appendingPathComponent(".snipper-probe-\(UUID().uuidString)")
        do {
            try Data([0]).write(to: probe, options: .withoutOverwriting)
            try files.removeItem(at: probe)
        } catch {
            try? files.removeItem(at: probe)
            throw SnipperError.problem("Simple Video Snipper could not write to \(folder.path). \(error.localizedDescription)")
        }
    }
}

struct ExportRequest: Sendable {
    enum Destination: Sendable {
        case quick(folder: URL)
        case chosen(file: URL, replace: Bool)
    }

    let format: ClipFormat
    let destination: Destination
}

enum RecordingExporter {
    static func save(_ clip: PendingClip, request: ExportRequest) async throws -> URL {
        try await Task.detached(priority: .userInitiated) { try export(clip, request: request) }.value
    }

    private static func export(_ clip: PendingClip, request: ExportRequest) throws -> URL {
        let files = FileManager.default
        let folder: URL
        switch request.destination {
        case .quick(let target):
            folder = target
            try files.createDirectory(at: folder, withIntermediateDirectories: true)
        case .chosen(let file, _):
            folder = file.deletingLastPathComponent()
        }

        let temporary = folder.appendingPathComponent(".snipper-export-\(UUID().uuidString).\(request.format.rawValue)")
        defer { try? files.removeItem(at: temporary) }
        if clip.format == .mp4, request.format == .mp4 {
            try files.copyItem(at: clip.url, to: temporary)
        } else {
            try transcode(clip.url, to: temporary, format: request.format)
        }

        let saved: URL
        switch request.destination {
        case .chosen(let file, let replace):
            try publish(temporary, to: file, replace: replace)
            saved = file
        case .quick:
            saved = try publishUnique(temporary, to: folder, title: clip.title, format: request.format)
        }
        // A saved clip is valid even if a disconnected or unwritable disk prevents staging cleanup.
        try? files.removeItem(at: clip.url)
        return saved
    }

    private static func publishUnique(_ source: URL, to folder: URL, title: String, format: ClipFormat) throws -> URL {
        for attempt in 1...100 {
            let stem = attempt == 1 ? title : "\(title) \(attempt)"
            let candidate = folder.appendingPathComponent("\(stem).\(format.rawValue)")
            do {
                try publish(source, to: candidate, replace: false)
                return candidate
            } catch let error as POSIXError where error.code == .EEXIST {
                continue
            }
        }
        throw SnipperError.problem("There is no available filename in \(folder.path).")
    }

    private static func publish(_ source: URL, to destination: URL, replace: Bool) throws {
        let error: Int32? = source.withUnsafeFileSystemRepresentation { sourcePath in
            destination.withUnsafeFileSystemRepresentation { destinationPath in
                guard let sourcePath, let destinationPath else { return EINVAL }
                if replace {
                    return Darwin.rename(sourcePath, destinationPath) == 0 ? nil : errno
                }
                if renamex_np(sourcePath, destinationPath, UInt32(RENAME_EXCL)) == 0 { return nil }
                let error = errno
                guard error == ENOTSUP || error == EINVAL else { return error }
                return publishOnNonExclusiveVolume(sourcePath, to: destinationPath)
            }
        }
        if let error { throw POSIXError(POSIXErrorCode(rawValue: error) ?? .EIO) }
    }

    private static func publishOnNonExclusiveVolume(
        _ source: UnsafePointer<CChar>, to destination: UnsafePointer<CChar>
    ) -> Int32? {
        // exFAT does not implement RENAME_EXCL. Reserve the filename only after the export is complete.
        let descriptor = open(destination, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, mode_t(0o600))
        guard descriptor >= 0 else { return errno }
        defer { close(descriptor) }
        if Darwin.rename(source, destination) == 0 { return nil }

        let error = errno
        var reserved = stat()
        var current = stat()
        if fstat(descriptor, &reserved) == 0, lstat(destination, &current) == 0,
           reserved.st_dev == current.st_dev, reserved.st_ino == current.st_ino {
            unlink(destination)
        }
        return error
    }

    static var ffmpegURL: URL? {
        let candidates = [
            Bundle.main.url(forAuxiliaryExecutable: "ffmpeg"),
            URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg"),
            URL(fileURLWithPath: "/usr/local/bin/ffmpeg"),
        ].compactMap { $0 }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    private static func transcode(_ source: URL, to destination: URL, format: ClipFormat) throws {
        guard let ffmpegURL else {
            throw SnipperError.problem("Install FFmpeg and retry the export. It is required for WebM and Chrome tab exports.")
        }
        var arguments = ["-nostdin", "-v", "error", "-n", "-i", source.path, "-map", "0:v:0", "-map", "0:a:0?"]
        switch format {
        case .mp4:
            arguments += ["-c:v", "libx264", "-preset", "fast", "-pix_fmt", "yuv420p",
                          "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:a", "aac", "-movflags", "+faststart"]
        case .webm:
            arguments += ["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-crf", "31", "-b:v", "0",
                          "-cpu-used", "4", "-c:a", "libopus"]
        }
        arguments.append(destination.path)
        let task = Process()
        let errors = Pipe()
        task.executableURL = ffmpegURL
        task.arguments = arguments
        task.standardError = errors
        task.standardOutput = FileHandle.nullDevice
        try task.run()
        let output = errors.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        guard task.terminationStatus == 0 else {
            let message = String(decoding: output.suffix(1500), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            throw SnipperError.problem(message.isEmpty ? "FFmpeg could not export this recording." : message)
        }
    }
}
