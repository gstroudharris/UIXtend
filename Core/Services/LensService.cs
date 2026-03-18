using System;
using System.Collections.Generic;
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
            AppLogger.Log($"OpenLens region=({globalRegion.X},{globalRegion.Y} {globalRegion.Width}x{globalRegion.Height})");
            var capture = _captureService.StartCapture(globalRegion);
            var color = LensColorPalette.ForIndex(capture.Id - 1);
            var window = new LensWindow(capture, _renderService.Device, color);

            window.LensClosed += OnLensWindowClosed;
            window.Activate();

            _active.Add((capture, window));
            AppLogger.Log($"  Lens {capture.Id} opened");
            LensesChanged?.Invoke(this, EventArgs.Empty);
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
