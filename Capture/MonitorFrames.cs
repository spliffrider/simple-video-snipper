using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SimpleVideoSnipper;

internal sealed class MonitorFrames : IDisposable
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GraphicsCaptureItem item;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice winrtDevice;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly ID3D11Texture2D crop;
    private readonly Rectangle region;
    private bool hasFrame;

    public MonitorFrames(SelectedArea area)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows Graphics Capture is unavailable on this device.");

        region = area.Region;
        item = CreateMonitorItem(area.Monitor);
        if (item.Size.Width != area.Display.Width || item.Size.Height != area.Display.Height ||
            !new Rectangle(0, 0, item.Size.Width, item.Size.Height).Contains(region))
            throw new InvalidOperationException("The display changed. Select the area again.");

        device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            FeatureLevel.Level_11_0);
        try
        {
            context = device.ImmediateContext;
            using (var multithread = context.QueryInterface<ID3D11Multithread>())
                multithread.SetMultithreadProtected(true);

            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var raw));
            try
            {
                winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(raw);
            }
            finally
            {
                Marshal.Release(raw);
            }

            crop = CreateTexture();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            item.Closed += OnClosed;
            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Task Ready => ready.Task;
    public Task Closed => closed.Task;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args) => ready.TrySetResult();
    private void OnClosed(GraphicsCaptureItem sender, object args) => closed.TrySetResult();

    public IDirect3DSurface GetSurface()
    {
        // A static desktop need not deliver a new frame. Retain the last crop so the clip keeps time.
        for (var pending = 0; pending < 2; pending++)
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
                break;
            if (frame.ContentSize.Width != item.Size.Width || frame.ContentSize.Height != item.Size.Height)
                throw new InvalidOperationException("The display size changed during recording.");

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(ID3D11Texture2D).GUID;
            using var source = new ID3D11Texture2D(access.GetInterface(in iid));
            var sourceBox = new Box(region.Left, region.Top, 0, region.Right, region.Bottom, 1);
            context.CopySubresourceRegion(crop, 0, 0, 0, 0, source, 0, sourceBox);
            hasFrame = true;
        }

        if (!hasFrame)
            throw new InvalidOperationException("Windows did not deliver a frame from the selected display.");

        // The encoder may still be reading its sample when the next screen frame is captured.
        using var texture = CreateTexture();
        context.CopyResource(texture, crop);
        using var surface = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(surface.NativePointer, out var raw));
        try
        {
            return MarshalInterface<IDirect3DSurface>.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private ID3D11Texture2D CreateTexture() => device.CreateTexture2D(
        Format.B8G8R8A8_UNorm, (uint)region.Width, (uint)region.Height,
        bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource);

    private static GraphicsCaptureItem CreateMonitorItem(nint monitor)
    {
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        factory.CreateForMonitor(monitor, in iid, out var raw);
        try
        {
            return GraphicsCaptureItem.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    public void Dispose()
    {
        item.Closed -= OnClosed;
        if (pool is not null)
            pool.FrameArrived -= OnFrameArrived;
        session?.Dispose();
        pool?.Dispose();
        crop?.Dispose();
        winrtDevice?.Dispose();
        context?.Dispose();
        device.Dispose();
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint window, in Guid iid, out nint result);
        void CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(in Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint surface);
}
