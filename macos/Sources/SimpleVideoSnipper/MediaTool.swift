import Foundation

enum MediaTool: String {
    case ffmpeg, ffprobe

    private var executable: URL? {
        [
            Bundle.main.url(forAuxiliaryExecutable: rawValue),
            URL(fileURLWithPath: "/opt/homebrew/bin/\(rawValue)"),
            URL(fileURLWithPath: "/usr/local/bin/\(rawValue)"),
        ].compactMap { $0 }.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    var available: Bool { executable != nil }

    func run(_ arguments: [String], context: String) async throws -> Data {
        guard let executable else {
            throw SnipperError.problem("Install FFmpeg and FFprobe with Homebrew to use the clip editor and convert recordings.")
        }
        let subprocess = MediaProcess(executable, arguments: arguments, context: context)
        let worker = Task.detached(priority: .userInitiated) { try await subprocess.run() }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            subprocess.cancel()
        }
    }

    static func seconds(_ seconds: Double) -> String {
        String(format: "%.6f", locale: Locale(identifier: "en_US_POSIX"), seconds)
    }
}

private final class MediaProcess: @unchecked Sendable {
    private let process = Process()
    private let output = Pipe()
    private let errors = Pipe()
    private let lock = NSLock()
    private let context: String
    private var cancelled = false

    init(_ executable: URL, arguments: [String], context: String) {
        self.context = context
        process.executableURL = executable
        process.arguments = arguments
        process.standardOutput = output
        process.standardError = errors
        process.standardInput = FileHandle.nullDevice
    }

    func run() async throws -> Data {
        try lock.withLock {
            if cancelled { throw CancellationError() }
            try process.run()
        }

        // Drain both pipes so a long error message cannot block the decoder.
        let errorRead = Task.detached { [errors] in
            errors.fileHandleForReading.readDataToEndOfFile()
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let diagnostics = await errorRead.value
        if lock.withLock({ cancelled }) { throw CancellationError() }
        guard process.terminationStatus == 0 else {
            let detail = String(decoding: diagnostics.suffix(1500), as: UTF8.self)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            throw SnipperError.problem(detail.isEmpty ? context : "\(context) \(detail)")
        }
        return data
    }

    func cancel() {
        lock.withLock {
            cancelled = true
            if process.isRunning { process.terminate() }
        }
    }
}
