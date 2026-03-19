// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using UIXtend.Core;
using UIXtend.Core.Interfaces;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using WinRT;

namespace UIXtend.Core.Services
{
    public class CaptureService : ICaptureService
    {
        private readonly IRenderService _renderService;

        // Keyed by HMONITOR's raw pointer value (extracted once in unsafe context)
        private readonly Dictionary<nint, MonitorCapture> _monitorCaptures = new();
        private readonly object _lock = new();
        private int _nextId = 1;

        public CaptureService(IRenderService renderService)
        {
            _renderService = renderService;
        }

        public void Initialize() { }

        public unsafe IRegionCapture StartCapture(Rect globalRegion)
        {
            var hMonitor = GetMonitorForRegion(globalRegion);

            // Extract the opaque HMONITOR pointer (void* in CsWin32) as a safe nint key
            var monitorKey = (nint)hMonitor.Value;

            // Compute the crop rect in monitor-local physical pixels
            var monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            PInvoke.GetMonitorInfo(hMonitor, ref monitorInfo);
            var mb = monitorInfo.rcMonitor;

            var cropRect = new Rect(
                globalRegion.X - mb.left,
                globalRegion.Y - mb.top,
                globalRegion.Width,
                globalRegion.Height);

            lock (_lock)
            {
                // Reuse the existing WGC session for this monitor if one is already running
                if (!_monitorCaptures.TryGetValue(monitorKey, out var monitorCapture))
                {
                    AppLogger.Log($"StartCapture: creating new MonitorCapture for monitor 0x{monitorKey:X}");
                    var item = CreateItemForMonitor(hMonitor);
                    monitorCapture = new MonitorCapture(monitorKey, item, _renderService.Device);
                    _monitorCaptures[monitorKey] = monitorCapture;
                }
                else
                {
                    AppLogger.Log($"StartCapture: reusing MonitorCapture for monitor 0x{monitorKey:X}");
                }

                var id = _nextId++;
                AppLogger.Log($"  RegionView id={id} crop=({cropRect.X},{cropRect.Y} {cropRect.Width}x{cropRect.Height})");
                var view = new RegionView(id, globalRegion, cropRect, monitorKey);
                monitorCapture.AddView(view);
                view.ViewDisposed += OnViewDisposed;
                return view;
            }
        }

        private void OnViewDisposed(RegionView view)
        {
            lock (_lock)
            {
                if (!_monitorCaptures.TryGetValue(view.MonitorKey, out var capture)) return;

                capture.RemoveView(view);

                // Tear down the WGC session when the last lens for this monitor is closed
                if (capture.ViewCount == 0)
                {
                    capture.Dispose();
                    _monitorCaptures.Remove(view.MonitorKey);
                }
            }
        }

        private static HMONITOR GetMonitorForRegion(Rect rect)
        {
            var r = new RECT
            {
                left   = (int)rect.X,
                top    = (int)rect.Y,
                right  = (int)(rect.X + rect.Width),
                bottom = (int)(rect.Y + rect.Height)
            };
            return PInvoke.MonitorFromRect(r, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }

        // ── WinRT / COM interop for GraphicsCaptureItem ────────────────────────────
        // IGraphicsCaptureItemInterop (GUID 3628E81B-...) is implemented by the WinRT
        // activation factory for GraphicsCaptureItem. CreateForMonitor returns an
        // IGraphicsCaptureItem (GUID 79C3F95B-...) for the given HMONITOR.

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig] int CreateForWindow(IntPtr hwnd, ref Guid iid, out IntPtr ppv);
            [PreserveSig] int CreateForMonitor(IntPtr hmonitor, ref Guid iid, out IntPtr ppv);
        }

        private static readonly Guid IID_IGraphicsCaptureItem =
            new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll",
            CallingConvention = CallingConvention.StdCall)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            uint length, out IntPtr hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll",
            CallingConvention = CallingConvention.StdCall)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        private static unsafe GraphicsCaptureItem CreateItemForMonitor(HMONITOR hMonitor)
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

            var hr = WindowsCreateString(className, (uint)className.Length, out var classHStr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
                hr = RoGetActivationFactory(classHStr, ref interopGuid, out var factoryPtr);
                Marshal.ThrowExceptionForHR(hr);

                // The activation factory also implements IGraphicsCaptureItemInterop
                var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                Marshal.Release(factoryPtr);

                // HMONITOR.Value is void* in CsWin32 — cast through nint for IntPtr
                var hMonitorPtr = (IntPtr)(nint)hMonitor.Value;
                var itemGuid = IID_IGraphicsCaptureItem;
                hr = factory.CreateForMonitor(hMonitorPtr, ref itemGuid, out var itemPtr);
                Marshal.ThrowExceptionForHR(hr);

                // Must use FromAbi (C#/WinRT projection API) — Marshal.GetObjectForIUnknown
                // returns System.__ComObject (generic COM RCW), not the projected type,
                // causing an InvalidCastException at runtime.
                var item = GraphicsCaptureItem.FromAbi(itemPtr);
                Marshal.Release(itemPtr); // FromAbi AddRefs internally; release our original ref
                return item;
            }
            finally
            {
                WindowsDeleteString(classHStr);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var capture in _monitorCaptures.Values)
                    capture.Dispose();
                _monitorCaptures.Clear();
            }
            GC.SuppressFinalize(this);
        }
    }

    // ── MonitorCapture ────────────────────────────────────────────────────────────
    // One instance per physical monitor. Owns the WGC session and distributes
    // full-monitor bitmaps to all RegionViews (lens windows) on that monitor.

    internal sealed class MonitorCapture : IDisposable
    {
        private readonly CanvasDevice _device;
        private readonly IDirect3DDevice _d3dDevice; // cached QI result
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private readonly List<RegionView> _views = new();
        private readonly object _viewLock = new();
        private SizeInt32 _captureSize;
        private bool _disposed;

        // Dispatch timing — logged every 5 s to track frame budget consumption.
        private int _dispatchCount;
        private long _totalDispatchTicks;
        private long _maxDispatchTicks;
        private long _lastDispatchStatsTicks = Stopwatch.GetTimestamp();
        private int _totalDrainCount; // stale frames discarded by queue drain

        // Exclusive lock guarding all Win2D/D2D device operations on the shared CanvasDevice.
        //
        // Win2D's CanvasBitmap.CreateFromDirect3D11Surface calls ID2D1DeviceContext::
        // CreateBitmapFromDxgiSurface on a shared ID2D1DeviceContext owned by the CanvasDevice.
        // ID2D1DeviceContext is NOT thread-safe; concurrent calls from multiple WGC threads
        // corrupt its internal state, leaving one thread permanently blocked on a GPU sync
        // event that never fires ("black screen" bug confirmed by diagnostic logs).
        //
        // new CanvasSwapChain likewise allocates D3D resources on the same device.
        //
        // Solution: serialize all three operations (CreateFromDirect3D11Surface, new
        // CanvasSwapChain, ResizeBuffers) under a single exclusive lock.  Each call takes
        // ≤ 1 ms, so even with two monitors at 60 fps the serialization overhead is < 0.1 ms/s.
        //
        // Present() is intentionally excluded — it routes through the DWM compositor and must
        // NOT hold this lock; doing so causes a deadlock with the DWM ACK path.
        internal static readonly object DeviceCreationLock = new();

        public nint MonitorKey { get; }
        public int ViewCount { get { lock (_viewLock) return _views.Count; } }

        public MonitorCapture(nint monitorKey, GraphicsCaptureItem item, CanvasDevice device)
        {
            MonitorKey = monitorKey;
            _device = device;
            _d3dDevice = device.As<IDirect3DDevice>(); // QI once, reuse for recreates
            _captureSize = item.Size;

            // Free-threaded, single-buffered frame pool at the monitor's native resolution.
            //
            // CreateFreeThreaded (vs Create): eliminates the DispatcherQueue hop. Create()
            // ties FrameArrived to the calling thread's DispatcherQueue, so every frame must
            // wait to be scheduled on that thread — behind XAML layout, input, etc. —
            // before the callback fires. CreateFreeThreaded() calls us directly on WGC's
            // internal thread the instant the frame is ready; no dispatcher overhead.
            //
            // bufferCount=1 (vs 2): WGC can only queue one frame ahead of delivery. With
            // draining (TryGetNextFrame loop) we always get the latest, so extra buffers
            // only add pipeline depth without benefit. One buffer = minimum WGC latency.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _d3dDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                _captureSize);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(item);
            // IsCursorCaptureEnabled requires 19041+; our target framework guarantees this
#pragma warning disable CA1416
            _session.IsCursorCaptureEnabled = false;
#pragma warning restore CA1416

            // IsBorderRequired requires Windows 11 22H2 (SDK 22621); TFM now targets 22621.
            // TargetPlatformMinVersion is still 17763, so CA1416 must be suppressed.
#pragma warning disable CA1416
            _session.IsBorderRequired = false;
#pragma warning restore CA1416
            AppLogger.Log("  IsBorderRequired = false (yellow border suppressed)");

            _session.StartCapture();
        }

        public void AddView(RegionView view) { lock (_viewLock) _views.Add(view); }
        public void RemoveView(RegionView view) { lock (_viewLock) _views.Remove(view); }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;

            // Any unhandled exception here escapes into the WGC system, which silently
            // detaches the FrameArrived handler and stops all frame delivery permanently.
            // Catch everything, log it, and keep the loop alive.
            try
            {
                OnFrameArrivedCore(sender);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ERROR] MonitorCapture 0x{MonitorKey:X}: OnFrameArrived unhandled exception: {ex.GetType().Name} hr=0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        private void OnFrameArrivedCore(Direct3D11CaptureFramePool sender)
        {
            // Drain the frame queue to get the most recent frame, discarding any stale
            // intermediates. TryGetNextFrame() returns null immediately when the queue is
            // empty, so this is at most bufferCount extra COM calls — zero GPU cost.
            // Without draining, processing hiccups cause the queue to fill and the display
            // to fall behind real-time by up to bufferCount frames.
            var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            int drainCount = 0;
            while (true)
            {
                var newer = sender.TryGetNextFrame();
                if (newer == null) break;
                frame.Dispose();
                frame = newer;
                drainCount++;
            }

            using (frame)
            {
            // Recreate the frame pool when the monitor resolution changes
            if (frame.ContentSize.Width != _captureSize.Width ||
                frame.ContentSize.Height != _captureSize.Height)
            {
                AppLogger.Log($"MonitorCapture 0x{MonitorKey:X}: size changed {_captureSize.Width}x{_captureSize.Height} -> {frame.ContentSize.Width}x{frame.ContentSize.Height}, recreating pool");
                _captureSize = frame.ContentSize;
                sender.Recreate(
                    _d3dDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    1,
                    _captureSize);
                return; // Skip this frame; next one will be the correct size
            }

            // Log the thread ID on the very first dispatch so we know which thread owns the frame loop.
            if (_dispatchCount == 0)
                AppLogger.Log($"MonitorCapture 0x{MonitorKey:X}: first dispatch on thread={Environment.CurrentManagedThreadId}");

            var dispatchStart = Stopwatch.GetTimestamp();

            // Serialize all Win2D device operations under DeviceCreationLock.
            // ID2D1DeviceContext (shared per CanvasDevice) is not thread-safe; concurrent
            // CreateFromDirect3D11Surface calls from two WGC threads corrupt its state and
            // cause a permanent GPU-sync hang.  Present() runs outside this lock (see above).
            CanvasBitmap bitmap;
            lock (DeviceCreationLock)
            {
                bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
            }

            RegionView[] snapshot;
            lock (_viewLock) snapshot = _views.ToArray();

            // DeliverFrame (and the draw/present inside it) run outside the lock.
            using (bitmap)
                foreach (var view in snapshot)
                    view.DeliverFrame(bitmap);

            // Accumulate dispatch timing (includes all view draw+present work).
            var dispatchTicks = Stopwatch.GetTimestamp() - dispatchStart;
            _dispatchCount++;
            _totalDispatchTicks += dispatchTicks;
            _totalDrainCount += drainCount;
            if (dispatchTicks > _maxDispatchTicks) _maxDispatchTicks = dispatchTicks;

            var now = Stopwatch.GetTimestamp();
            if (now - _lastDispatchStatsTicks >= Stopwatch.Frequency * 5)
            {
                var avgMs     = _totalDispatchTicks * 1000.0 / Stopwatch.Frequency / _dispatchCount;
                var maxMs     = _maxDispatchTicks  * 1000.0 / Stopwatch.Frequency;
                var fps       = _dispatchCount / 5.0;
                var views     = snapshot.Length;
                var avgDrain  = (double)_totalDrainCount / _dispatchCount;
                AppLogger.Log($"[perf] MonitorCapture 0x{MonitorKey:X}: {fps:F1} fps, {views} views, dispatch avg={avgMs:F2}ms max={maxMs:F2}ms drain avg={avgDrain:F2} thread={Environment.CurrentManagedThreadId}");
                _dispatchCount = 0;
                _totalDispatchTicks = 0;
                _maxDispatchTicks = 0;
                _totalDrainCount = 0;
                _lastDispatchStatsTicks = now;
            }
            } // end using (frame)
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_framePool != null)
                _framePool.FrameArrived -= OnFrameArrived;

            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
        }
    }

    // ── RegionView ────────────────────────────────────────────────────────────────
    // One instance per active lens window. Implements IRegionCapture and acts as
    // the per-region subscriber to its parent MonitorCapture's frame stream.

    internal sealed class RegionView : IRegionCapture
    {
        public int Id { get; }
        public Rect Region { get; }
        public Rect CropRect { get; }

        /// <summary>The HMONITOR's raw value (void* cast to nint), used as a dictionary key.</summary>
        public nint MonitorKey { get; }

        private bool _disposed;

        /// <summary>Fired on the WGC thread for every new frame from the parent monitor capture.</summary>
        public event EventHandler<CanvasBitmap>? FrameArrived;

        /// <summary>Notifies CaptureService so it can release the MonitorCapture when empty.</summary>
        internal event Action<RegionView>? ViewDisposed;

        public RegionView(int id, Rect region, Rect cropRect, nint monitorKey)
        {
            Id = id;
            Region = region;
            CropRect = cropRect;
            MonitorKey = monitorKey;
        }

        /// <summary>Called by MonitorCapture on every frame. Bitmap lifetime = caller's frame.</summary>
        internal void DeliverFrame(CanvasBitmap fullMonitorBitmap)
        {
            if (!_disposed)
                FrameArrived?.Invoke(this, fullMonitorBitmap);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ViewDisposed?.Invoke(this);
        }
    }
}
