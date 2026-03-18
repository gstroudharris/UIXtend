using System;
using UIXtend.Core.Interfaces;

namespace UIXtend.Core.Services
{
    public class RenderService : IRenderService
    {
        public void Initialize()
        {
            // Initialization for Render Loop
        }

        public void StartRenderLoop()
        {
            // 144 FPS D3D / Composition loop
        }

        public void StopRenderLoop()
        {
            // Stop loop
        }

        public void Dispose()
        {
            StopRenderLoop();
            GC.SuppressFinalize(this);
        }
    }
}
