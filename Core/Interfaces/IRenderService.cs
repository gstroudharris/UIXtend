using System;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Manages the high-performance 144 FPS D3D / Composition render loop.
    /// </summary>
    public interface IRenderService : IService
    {
        void StartRenderLoop();
        void StopRenderLoop();
    }
}
