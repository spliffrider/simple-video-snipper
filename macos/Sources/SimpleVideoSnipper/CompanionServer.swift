import Foundation
import NIOCore
import NIOHTTP1
import NIOPosix
import NIOWebSocket

final class CompanionServer: @unchecked Sendable {
    enum Event {
        case recording
        case finished(Result<PendingClip, Error>)
    }

    private let group: MultiThreadedEventLoopGroup
    private let sessionPath = "/session/\(UUID().uuidString)/"
    private let draft: ClipDraft
    private let assets: [String: Asset]
    private let onEvent: (Event) -> Void

    // After bind, these are accessed only on the single server event loop.
    private var claimed = false
    private var socket: Channel?
    private var handler: RecordingSocket?
    @MainActor private var listener: Channel?
    @MainActor private var closed = false

    private struct Asset {
        let type: String
        let data: Data
    }

    init(draft: ClipDraft, onEvent: @escaping (Event) -> Void) throws {
        guard let resources = Bundle.main.resourceURL else {
            throw SnipperError.problem("The Chrome recorder resources are missing from the app.")
        }
        let folder = resources.appendingPathComponent("Companion", isDirectory: true)
        var assets: [String: Asset] = [:]
        for (route, file, type) in [
            ("", "index.html", "text/html; charset=utf-8"),
            ("recorder.css", "recorder.css", "text/css; charset=utf-8"),
            ("recorder.js", "recorder.js", "text/javascript; charset=utf-8"),
        ] {
            let data = try Data(contentsOf: folder.appendingPathComponent(file))
            assets[route] = Asset(type: type, data: data)
        }
        self.draft = draft
        self.onEvent = onEvent
        self.assets = assets
        group = MultiThreadedEventLoopGroup(numberOfThreads: 1)
    }

    @MainActor
    func start() async throws -> URL {
        do {
            let listener = try await ServerBootstrap(group: group)
                .serverChannelOption(ChannelOptions.backlog, value: 16)
                .childChannelInitializer { [self] channel in configure(channel) }
                .bind(host: "127.0.0.1", port: 0).get()
            self.listener = listener
            guard let port = listener.localAddress?.port else {
                throw SnipperError.problem("The Chrome recorder could not get a local port.")
            }
            return URL(string: "http://127.0.0.1:\(port)\(sessionPath)")!
        } catch {
            await close()
            throw error
        }
    }

    @MainActor
    func stopRecording() {
        guard let loop = listener?.eventLoop else { return }
        loop.execute { [weak self] in
            guard let socket = self?.socket, let handler = self?.handler else { return }
            handler.requestStop(on: socket)
        }
    }

    @MainActor
    func close() async {
        guard !closed else { return }
        closed = true
        if let listener {
            self.listener = nil
            try? await listener.close().get()
        }
        try? await group.shutdownGracefully()
    }

    private func configure(_ channel: Channel) -> EventLoopFuture<Void> {
        let http = CompanionHTTP { [self] request in
            guard request.method == .GET, matchesHost(request, channel: channel),
                  request.uri.hasPrefix(sessionPath) else { return nil }
            let route = String(request.uri.dropFirst(sessionPath.count))
            return assets[route].map { ($0.type, $0.data) }
        }
        let upgrade = NIOWebSocketServerUpgrader(
            maxFrameSize: 32 * 1024 * 1024,
            shouldUpgrade: { [self] channel, request in
                let host = request.headers.first(name: "Host")
                let origins = request.headers["Origin"]
                let accepted = !claimed && matchesHost(request, channel: channel)
                    && request.method == .GET && request.uri == sessionPath + "record"
                    && origins.count == 1 && origins.first == host.map { "http://\($0)" }
                return channel.eventLoop.makeSucceededFuture(accepted ? HTTPHeaders() : nil)
            },
            upgradePipelineHandler: { [self] channel, _ in
                guard !claimed else {
                    return channel.close()
                }
                claimed = true
                let recorder = RecordingSocket(draft: draft, onEvent: onEvent)
                socket = channel
                handler = recorder
                return channel.eventLoop.makeCompletedFuture {
                    try channel.pipeline.syncOperations.addHandlers([
                        NIOWebSocketFrameAggregator(
                            minNonFinalFragmentSize: 0, maxAccumulatedFrameCount: 128,
                            maxAccumulatedFrameSize: 32 * 1024 * 1024
                        ),
                        recorder,
                    ])
                }
            }
        )
        return channel.pipeline.configureHTTPServerPipeline(withServerUpgrade: (
            upgraders: [upgrade], completionHandler: { context in
                context.pipeline.removeHandler(http, promise: nil)
            }
        )).flatMap { channel.pipeline.addHandler(http) }
    }

    private func matchesHost(_ request: HTTPRequestHead, channel: Channel) -> Bool {
        guard let port = channel.localAddress?.port else { return false }
        return request.headers["Host"] == ["127.0.0.1:\(port)"]
    }
}

private final class CompanionHTTP: ChannelInboundHandler, RemovableChannelHandler {
    typealias InboundIn = HTTPServerRequestPart
    typealias OutboundOut = HTTPServerResponsePart

    private let asset: (HTTPRequestHead) -> (String, Data)?
    private var requested: HTTPRequestHead?

    init(asset: @escaping (HTTPRequestHead) -> (String, Data)?) {
        self.asset = asset
    }

    func channelRead(context: ChannelHandlerContext, data: NIOAny) {
        switch unwrapInboundIn(data) {
        case .head(let request):
            requested = request
        case .body:
            context.close(promise: nil)
        case .end:
            guard let request = requested else {
                context.close(promise: nil)
                return
            }
            requested = nil
            let resource = asset(request)
            var headers = HTTPHeaders([
                ("Content-Length", String(resource?.1.count ?? 0)),
                ("Cache-Control", "no-store"),
                ("X-Content-Type-Options", "nosniff"),
                ("Referrer-Policy", "no-referrer"),
                ("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' ws://127.0.0.1:*; frame-ancestors 'none'; base-uri 'none'"),
                ("Connection", "close"),
            ])
            if let (type, _) = resource { headers.add(name: "Content-Type", value: type) }
            let head = HTTPResponseHead(version: .http1_1, status: resource == nil ? .notFound : .ok, headers: headers)
            context.write(wrapOutboundOut(.head(head)), promise: nil)
            if let (_, data) = resource {
                var body = context.channel.allocator.buffer(capacity: data.count)
                body.writeBytes(data)
                context.write(wrapOutboundOut(.body(.byteBuffer(body))), promise: nil)
            }
            context.writeAndFlush(wrapOutboundOut(.end(nil))).whenComplete { _ in context.close(promise: nil) }
        }
    }

    func errorCaught(context: ChannelHandlerContext, error: Error) {
        context.close(promise: nil)
    }
}

private final class RecordingSocket: ChannelInboundHandler {
    typealias InboundIn = WebSocketFrame
    typealias OutboundOut = WebSocketFrame

    private let draft: ClipDraft
    private let onEvent: (CompanionServer.Event) -> Void
    private var file: FileHandle?
    private var ended = false

    init(draft: ClipDraft, onEvent: @escaping (CompanionServer.Event) -> Void) {
        self.draft = draft
        self.onEvent = onEvent
    }

    func handlerAdded(context: ChannelHandlerContext) {
        do {
            try Data().write(to: draft.url, options: .withoutOverwriting)
            file = try FileHandle(forWritingTo: draft.url)
            send("ready", on: context.channel)
            onEvent(.recording)
        } catch {
            end(.failure(error), on: context.channel)
        }
    }

    func channelRead(context: ChannelHandlerContext, data: NIOAny) {
        guard !ended else { return }
        let frame = unwrapInboundIn(data)
        let channel = context.channel
        switch frame.opcode {
        case .binary:
            do {
                try file?.write(contentsOf: Data(frame.unmaskedData.readableBytesView))
            } catch {
                end(.failure(error), on: channel)
            }
        case .text:
            let text = String(decoding: frame.unmaskedData.readableBytesView, as: UTF8.self)
            guard text == "finish" else {
                end(.failure(SnipperError.problem("Chrome sent an unexpected recorder message.")), on: channel)
                return
            }
            do {
                try file?.close()
                file = nil
                let clip = try draft.publish()
                end(.success(clip), on: channel)
            } catch {
                end(.failure(error), on: channel)
            }
        case .ping:
            channel.writeAndFlush(WebSocketFrame(fin: true, opcode: .pong, data: frame.unmaskedData), promise: nil)
        case .pong:
            break
        case .connectionClose:
            end(.failure(SnipperError.problem("Chrome closed the connection before finishing.")), on: channel)
        default:
            end(.failure(SnipperError.problem("Chrome sent an unexpected WebSocket frame.")), on: channel)
        }
    }

    func channelInactive(context: ChannelHandlerContext) {
        if !ended {
            end(.failure(SnipperError.problem("Chrome disconnected before finishing.")), on: nil)
        }
        context.fireChannelInactive()
    }

    func errorCaught(context: ChannelHandlerContext, error: Error) {
        end(.failure(error), on: context.channel)
    }

    func requestStop(on channel: Channel) {
        guard !ended else { return }
        send("stop", on: channel)
    }

    private func send(_ message: String, on channel: Channel) {
        var payload = channel.allocator.buffer(capacity: message.utf8.count)
        payload.writeString(message)
        channel.writeAndFlush(WebSocketFrame(fin: true, opcode: .text, data: payload), promise: nil)
    }

    private func end(_ result: Result<PendingClip, Error>, on channel: Channel?) {
        guard !ended else { return }
        ended = true
        try? file?.close()
        file = nil
        draft.removeIfEmpty()
        if let channel {
            switch result {
            case .success(let clip): send("staged:" + clip.title, on: channel)
            case .failure: send("error:Could not finish the clip. Check the desktop app for details.", on: channel)
            }
            var close = channel.allocator.buffer(capacity: 2)
            close.writeInteger(UInt16(1000))
            channel.writeAndFlush(WebSocketFrame(fin: true, opcode: .connectionClose, data: close)).whenComplete {
                [onEvent] _ in
                channel.close(promise: nil)
                onEvent(.finished(result))
            }
        } else {
            onEvent(.finished(result))
        }
    }
}
