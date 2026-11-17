using System.Runtime.InteropServices;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;
using Colors = Vortice.Mathematics.Colors;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SimpleVideoSnipper;

internal readonly record struct CaptureTarget(nint Handle, Rectangle Bounds, Rectangle? Region)
{
    public bool IsWindow => Region is null;
    public Size DisplaySize { get; init; }

    public static CaptureTarget FromArea(SelectedArea area) =>
        new(area.Monitor, area.DesktopRegion, area.Region) { DisplaySize = area.Display.Size };
}

internal sealed class CaptureFrames : IDisposable
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
    private readonly ID2D1DeviceContext? compositor;
    private readonly ID2D1Bitmap1? canvas;
    private readonly Rectangle? region;
    private SizeInt32 poolSize;
    private bool hasFrame;

    public CaptureFrames(CaptureTarget target)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows Graphics Capture is unavailable on this device.");

        region = target.Region;
        item = CreateItem(target.Handle, target.IsWindow);
        poolSize = item.Size;
        if (region is { } area &&
            (target.DisplaySize != new Size(poolSize.Width, poolSize.Height) ||
             !new Rectangle(0, 0, poolSize.Width, poolSize.Height).Contains(area)))
            throw new InvalidOperationException("The display changed. Select the area again.");
        OutputSize = region?.Size ?? new Size(poolSize.Width & ~1, poolSize.Height & ~1);
        if (OutputSize.Width < 16 || OutputSize.Height < 16)
            throw new InvalidOperationException("The capture source is too small. Choose a larger window or area.");

        device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            Vortice.Direct3D.FeatureLevel.Level_11_0);
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
            if (target.IsWindow)
            {
                using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
                using var direct2D = factory.CreateDevice(dxgi);
                compositor = direct2D.CreateDeviceContext(DeviceContextOptions.None);
                using var surface = crop.QueryInterface<IDXGISurface>();
                canvas = compositor.CreateBitmapFromDxgiSurface(surface,
                    BitmapProperties(BitmapOptions.Target | BitmapOptions.CannotDraw));
            }
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
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
    public Size OutputSize { get; }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args) => ready.TrySetResult();
    private void OnClosed(GraphicsCaptureItem sender, object args) => closed.TrySetResult();

    public bool UpdateFrame()
    {
        // A static source need not deliver a new frame. Retain the last image so the clip keeps time.
        SizeInt32? newPoolSize = null;
        for (var pending = 0; pending < 2; pending++)
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
                break;
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0)
                break;
            if (size.Width != poolSize.Width || size.Height != poolSize.Height)
            {
                if (region is not null)
                    throw new InvalidOperationException("The display size changed during recording.");
                newPoolSize = size;
                break;
            }

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(ID3D11Texture2D).GUID;
            using var source = new ID3D11Texture2D(access.GetInterface(in iid));
            if (region is { } area)
            {
                var sourceBox = new Box(area.Left, area.Top, 0, area.Right, area.Bottom, 1);
                context.CopySubresourceRegion(crop, 0, 0, 0, 0, source, 0, sourceBox);
            }
            else
            {
                FitWindow(source, size);
            }
            hasFrame = true;
        }

        if (newPoolSize is { } updated)
        {
            pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, updated);
            poolSize = updated;
        }

        return hasFrame;
    }

    public IDirect3DSurface GetSurface()
    {
        UpdateFrame();
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
        Format.B8G8R8A8_UNorm, (uint)OutputSize.Width, (uint)OutputSize.Height,
        bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource);

    private void FitWindow(ID3D11Texture2D source, SizeInt32 size)
    {
        if (size.Width == OutputSize.Width && size.Height == OutputSize.Height)
        {
            context.CopyResource(crop, source);
            return;
        }

        var scale = Math.Min(OutputSize.Width / (float)size.Width, OutputSize.Height / (float)size.Height);
        var width = size.Width * scale;
        var height = size.Height * scale;
        var x = (OutputSize.Width - width) / 2;
        var y = (OutputSize.Height - height) / 2;
        using var surface = source.QueryInterface<IDXGISurface>();
        using var bitmap = compositor!.CreateBitmapFromDxgiSurface(surface, BitmapProperties(BitmapOptions.None));
        compositor.Target = canvas;
        compositor.BeginDraw();
        compositor.Clear(Colors.Black);
        compositor.DrawBitmap(bitmap, new RawRectF(x, y, x + width, y + height), 1,
            Vortice.Direct2D1.InterpolationMode.Linear, new RawRectF(0, 0, size.Width, size.Height), null);
        compositor.EndDraw().CheckError();
        compositor.Target = null;
    }

    private static BitmapProperties1 BitmapProperties(BitmapOptions options) =>
        new(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, options);

    private static GraphicsCaptureItem CreateItem(nint handle, bool window)
    {
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        nint raw;
        if (window)
            factory.CreateForWindow(handle, in iid, out raw);
        else
            factory.CreateForMonitor(handle, in iid, out raw);
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
        canvas?.Dispose();
        compositor?.Dispose();
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
