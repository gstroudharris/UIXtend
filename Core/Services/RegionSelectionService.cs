using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using UIXtend.Core.Interfaces;
using UIXtend.Core.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace UIXtend.Core.Services
{
    public class RegionSelectionService : IRegionSelectionService
    {
        private RegionSelectionOverlay? _activeOverlay;
        private DispatcherQueueTimer? _trackerTimer;
        private TaskCompletionSource<Windows.Foundation.Rect?>? _tcs;

        public void Initialize()
        {
        }

        public unsafe Task<Windows.Foundation.Rect?> StartSelectionAsync()
        {
            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                return _tcs.Task;
            }
            _tcs = new TaskCompletionSource<Windows.Foundation.Rect?>();

            if (_activeOverlay != null)
            {
                _activeOverlay.Close();
                _activeOverlay = null;
            }

            // Snapshot the exact cursor position at exactly the moment "Select Region" is clicked
            PInvoke.GetCursorPos(out var pt);

            // Determine the single monitor under the cursor
            var hMonitor = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

            var monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (PInvoke.GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                var bounds = monitorInfo.rcMonitor;
                
                // Spawn a single overlay on that screen constraint only
                _activeOverlay = new RegionSelectionOverlay(bounds);
                
                _activeOverlay.OnRegionSelected += (rect) => CompleteSelection(rect);
                _activeOverlay.OnSelectionCancelled += () => CompleteSelection(null);

                // Fallback close handling if the window dies
                _activeOverlay.Closed += (s, e) =>
                {
                    if (_tcs != null && !_tcs.Task.IsCompleted)
                    {
                        CompleteSelection(null);
                    }
                };
                // Start global keyboard tracking loop to securely catch ESC
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                _trackerTimer = dispatcher.CreateTimer();
                _trackerTimer.Interval = TimeSpan.FromMilliseconds(16);
                _trackerTimer.Tick += (s, e) => 
                {
                    if ((PInvoke.GetAsyncKeyState(0x1B) & 0x8000) != 0)
                    {
                        CompleteSelection(null);
                    }
                };
                _trackerTimer.Start();
            }
            else
            {
                _tcs.TrySetResult(null);
            }

            return _tcs.Task;
        }

        private void CompleteSelection(Windows.Foundation.Rect? rect)
        {
            if (_trackerTimer != null)
            {
                _trackerTimer.Stop();
                _trackerTimer = null;
            }

            if (_activeOverlay != null)
            {
                var overlay = _activeOverlay;
                _activeOverlay = null;

                // Tell the window to trigger its graceful 0.5s fade out animation
                overlay.FadeOutAndClose(() => 
                {
                    _tcs?.TrySetResult(rect);
                });
            }
            else
            {
                _tcs?.TrySetResult(rect);
            }
        }

        public void Dispose()
        {
            CompleteSelection(null);
            GC.SuppressFinalize(this);
        }
    }
}
