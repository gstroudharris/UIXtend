using Windows.Foundation;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Manages Windows Graphics Capture sessions.
    /// Internally shares one WGC session per physical monitor across all active lenses.
    /// </summary>
    public interface ICaptureService : IService
    {
        /// <summary>
        /// Starts a capture for the given region (in global virtual-screen physical pixels).
        /// Returns an IRegionCapture handle whose FrameArrived event delivers full-monitor
        /// bitmaps at the display's native refresh rate. Dispose the handle to release resources.
        /// </summary>
        IRegionCapture StartCapture(Rect globalRegion);
    }
}
