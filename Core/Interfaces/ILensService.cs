using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace UIXtend.Core.Interfaces
{
    /// <summary>Lightweight descriptor for an active lens window, used to build UI buttons.</summary>
    public readonly record struct LensInfo(int Id, string Label);

    /// <summary>
    /// Manages the lifecycle of lens windows (live preview overlays).
    /// Each lens owns one IRegionCapture; multiple lenses on the same monitor
    /// share a single underlying WGC session via CaptureService.
    /// </summary>
    public interface ILensService : IService
    {
        void OpenLens(Rect globalRegion);
        void CloseLens(int id);
        void SetOverlayVisible(bool visible);

        IReadOnlyList<LensInfo> ActiveLenses { get; }

        /// <summary>Fired (on the UI thread) whenever a lens is opened or closed.</summary>
        event EventHandler? LensesChanged;
    }
}
