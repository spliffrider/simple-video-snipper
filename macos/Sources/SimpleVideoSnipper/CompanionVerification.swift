import Foundation

@MainActor
enum CompanionVerification {
    static func run(in folder: URL, stopFromDesktop: Bool) async throws {
        try RecordingStorage.probe(folder)
        let id = UUID().uuidString
        let draft = ClipDraft(
            url: folder.appendingPathComponent(".verification-\(id).webm"),
            clip: PendingClip(url: folder.appendingPathComponent("Chrome-\(id).webm"), format: .webm)
        )
        let (events, feed) = AsyncStream<CompanionServer.Event>.makeStream()
        let server = try CompanionServer(draft: draft) { feed.yield($0) }
        let url = try await server.start()
        print("Recorder: \(url.absoluteString)")
        fflush(stdout)
        let timeout = Task {
            try? await Task.sleep(for: .seconds(45))
            if !Task.isCancelled { feed.finish() }
        }
        defer { timeout.cancel() }
        var stopTask: Task<Void, Never>?
        var finished: Result<PendingClip, Error>?
        for await event in events {
            switch event {
            case .recording:
                if stopFromDesktop {
                    stopTask = Task {
                        try? await Task.sleep(for: .seconds(3))
                        if !Task.isCancelled { server.stopRecording() }
                    }
                }
            case .finished(let result):
                finished = result
                feed.finish()
            }
        }
        stopTask?.cancel()
        await server.close()
        guard let finished else {
            throw SnipperError.problem("The Chrome recording verification timed out.")
        }
        let clip = try finished.get()
        print("Staged: \(clip.url.path)")
    }
}
