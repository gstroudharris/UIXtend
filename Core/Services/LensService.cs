using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UIXtend.Core;
using UIXtend.Core.Interfaces;
using UIXtend.Core.UI;
using Windows.Foundation;

namespace UIXtend.Core.Services
{
    public class LensService : ILensService
    {
        private readonly ICaptureService _captureService;
        private readonly IRenderService _renderService;

        // All access is on the UI thread; no locking needed for _active itself.
        private readonly List<(IRegionCapture Capture, LensWindow Window)> _active = new();
        private bool _disposed;

        public IReadOnlyList<LensInfo> ActiveLenses =>
            _active.Select(a => new LensInfo(a.Capture.Id, $"Region {a.Capture.Id}")).ToList();

        public event EventHandler? LensesChanged;

        public LensService(ICaptureService captureService, IRenderService renderService)
        {
            _captureService = captureService;
            _renderService = renderService;
        }

        public void Initialize() { }

        public void OpenLens(Rect globalRegion)
        {
            var sw = Stopwatch.StartNew();
            AppLogger.Log($"OpenLens region=({globalRegion.X},{globalRegion.Y} {globalRegion.Width}x{globalRegion.Height})");

            var capture = _captureService.StartCapture(globalRegion);
            AppLogger.Log($"  StartCapture took {sw.ElapsedMilliseconds} ms");

            var color = LensColorPalette.ForIndex(capture.Id - 1);
            var window = new LensWindow(capture, _renderService.Device, color);
            AppLogger.Log($"  LensWindow ctor took {sw.ElapsedMilliseconds} ms");

            window.LensClosed += OnLensWindowClosed;
            window.Activate();
            AppLogger.Log($"  Activate() took {sw.ElapsedMilliseconds} ms");

            _active.Add((capture, window));
            AppLogger.Log($"  Lens {capture.Id} opened — total {sw.ElapsedMilliseconds} ms (first frame logged separately)");
            LensesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetOverlayVisible(bool visible)
        {
            foreach (var (_, window) in _active)
                window.ShowOverlay = visible;
        }

        public void CloseLens(int id)
        {
            var entry = _active.FirstOrDefault(a => a.Capture.Id == id);
            if (entry == default) return;

            AppLogger.Log($"CloseLens id={id}");
            // Close() triggers AppWindow.Closing → LensWindow cleanup → LensClosed event
            entry.Window.Close();
        }

        private void OnLensWindowClosed(int id)
        {
            _active.RemoveAll(a => a.Capture.Id == id);
            AppLogger.Log($"  Lens {id} closed");
            LensesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (_, window) in _active.ToArray())
                window.Close();
            _active.Clear();
            GC.SuppressFinalize(this);
        }
    }
}
