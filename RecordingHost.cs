using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SimpleVideoSnipper;

internal sealed class RecordingHost : IDisposable
{
    private readonly WebApplication app;
    private readonly string route = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly object sessionLock = new();
    private RecordingSession? active;

    private RecordingHost(WebApplication app, string folder)
    {
        this.app = app;
        Folder = folder;
        app.UseWebSockets();
        MapAsset("", "index.html", "text/html; charset=utf-8");
        MapAsset("recorder.js", "recorder.js", "text/javascript; charset=utf-8");
        MapAsset("recorder.css", "recorder.css", "text/css; charset=utf-8");
        app.MapGet(route + "/record", HandleRecordingAsync);
    }

    public string Folder { get; }

    private string Origin => app.Urls.Single();

    public string RecorderUrl => Origin + route + "/";

    public bool IsRecording
    {
        get
        {
            lock (sessionLock)
                return active is not null;
        }
    }

    public static RecordingHost Start(string folder)
    {
        Directory.CreateDirectory(folder);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var host = new RecordingHost(builder.Build(), folder);
        host.app.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    public void RequestStop()
    {
        lock (sessionLock)
            active?.RequestStop();
    }

    public RecordingSession? TryStartRecording()
    {
        lock (sessionLock)
        {
            if (active is not null)
                return null;

            return active = new RecordingSession(this);
        }
    }

    private void MapAsset(string path, string resource, string type)
    {
        app.MapGet(route + "/" + path, (HttpContext context) =>
        {
            if (!HasLocalHost(context.Request))
                return Results.NotFound();

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var page = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("SimpleVideoSnipper.Companion." + resource);
            return Results.Stream(page!, type);
        });
    }

    private bool HasLocalHost(HttpRequest request) => Origin == $"http://{request.Host}";

    private async Task HandleRecordingAsync(HttpContext context)
    {
        if (!HasLocalHost(context.Request) ||
            context.Request.Headers.Origin != Origin ||
            !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var session = TryStartRecording();
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await SaveRecordingAsync(socket, session.StopRequested, context.RequestAborted);
    }

    private async Task SaveRecordingAsync(WebSocket socket, Task stopSignal, CancellationToken cancellationToken)
    {
        var name = $"Tab {DateTime.Now:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid():N}.webm";
        var destination = Path.Combine(Folder, name);
        var partial = destination + ".partial";
        var hasData = false;
        var response = "error:No video data was received.";

        try
        {
            bool finished;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await SendAsync(socket, "ready", cancellationToken);
                finished = await ReceiveAsync(socket, file, stopSignal, cancellationToken);
                hasData = file.Position > 0;
            }

            if (finished && hasData)
            {
                File.Move(partial, destination);
                response = "saved:" + name;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            response = "error:Could not save the clip. Check the recordings folder and available disk space.";
        }
        catch (WebSocketException)
        {
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            // Keep written bytes if the connection or final save was interrupted.
            TryRemoveEmptyFile(partial);
        }

        if (socket.State == WebSocketState.Open)
        {
            await SendAsync(socket, response, cancellationToken);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        }
        else if (socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        }
    }

    private static async Task<bool> ReceiveAsync(WebSocket socket, FileStream file, Task stopSignal,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var stopSent = false;
        var chunkComplete = true;

        while (true)
        {
            var receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (!stopSent && await Task.WhenAny(receive, stopSignal) == stopSignal)
            {
                await SendAsync(socket, "stop", cancellationToken);
                stopSent = true;
            }

            var message = await receive;
            if (message.MessageType == WebSocketMessageType.Binary)
            {
                await file.WriteAsync(buffer.AsMemory(0, message.Count), cancellationToken);
                chunkComplete = message.EndOfMessage;
                continue;
            }

            return message.MessageType == WebSocketMessageType.Text &&
                message.EndOfMessage && chunkComplete &&
                buffer.AsSpan(0, message.Count).SequenceEqual("finish"u8);
        }
    }

    private static Task SendAsync(WebSocket socket, string message, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
            WebSocketMessageType.Text, true, cancellationToken);

    private static void TryRemoveEmptyFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length == 0)
                file.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the save result.
        }
    }

    public void Dispose() => app.DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal sealed class RecordingSession(RecordingHost owner) : IDisposable
    {
        private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopRequested => stop.Task;

        public void RequestStop() => stop.TrySetResult();

        public void Dispose()
        {
            lock (owner.sessionLock)
            {
                if (owner.active == this)
                    owner.active = null;
            }
        }
    }
}
