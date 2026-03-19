using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using UIXtend.Core.Interfaces;
using UIXtend.Core.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UIXtend.Core.Services
{
    public class RegionSelectionService : IRegionSelectionService
    {
        private RegionSelectionOverlay? _activeOverlay;
        private DispatcherQueueTimer? _escTimer;
        private TaskCompletionSource<Windows.Foundation.Rect?>? _tcs;

        public void Initialize() { }

        public Task<Windows.Foundation.Rect?> StartSelectionAsync()
        {
            if (_tcs != null && !_tcs.Task.IsCompleted)
                return _tcs.Task;

            _tcs = new TaskCompletionSource<Windows.Foundation.Rect?>();

            _activeOverlay?.Close();
            _activeOverlay = null;

            var bounds = GetVirtualDesktopBounds();
            _activeOverlay = new RegionSelectionOverlay(bounds);

            _activeOverlay.OnRegionSelected += (rect) => CompleteSelection(rect);
            _activeOverlay.OnSelectionCancelled += () => CompleteSelection(null);

            _activeOverlay.Closed += (s, e) =>
            {
                if (_tcs != null && !_tcs.Task.IsCompleted)
                    CompleteSelection(null);
            };

            // Poll for ESC key at ~60 Hz — right-click cancel is handled directly by the overlay
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _escTimer = dispatcher.CreateTimer();
            _escTimer.Interval = TimeSpan.FromMilliseconds(16);
            _escTimer.Tick += (s, e) =>
            {
                if ((PInvoke.GetAsyncKeyState(0x1B) & 0x8000) != 0)
                    CompleteSelection(null);
            };
            _escTimer.Start();

            return _tcs.Task;
        }

        private static unsafe RECT GetVirtualDesktopBounds()
        {
            var x = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
            var y = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
            var w = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
            var h = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
            return new RECT { left = x, top = y, right = x + w, bottom = y + h };
        }

        private void CompleteSelection(Windows.Foundation.Rect? rect)
        {
            _escTimer?.Stop();
            _escTimer = null;

            // Resolve immediately so callers don't wait for the fade animation.
            // The overlay fades out and closes in the background independently.
            _tcs?.TrySetResult(rect);

            if (_activeOverlay != null)
            {
                var overlay = _activeOverlay;
                _activeOverlay = null;
                overlay.FadeOutAndClose();
            }
        }

        public void Dispose()
        {
            CompleteSelection(null);
            GC.SuppressFinalize(this);
        }
    }
}
