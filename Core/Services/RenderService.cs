using System;
using Microsoft.Graphics.Canvas;
using UIXtend.Core.Interfaces;

namespace UIXtend.Core.Services
{
    public class RenderService : IRenderService
    {
        private CanvasDevice? _device;

        /// <summary>
        /// The shared hardware D3D11 device. All capture sessions and lens windows borrow
        /// this reference — never dispose it from outside this service.
        /// </summary>
        public CanvasDevice Device =>
            _device ?? throw new InvalidOperationException("RenderService has not been initialized.");

        public void Initialize()
        {
            _device = new CanvasDevice();
        }

        // Reserved for the future GPU overlay compositor (144 FPS render loop).
        public void StartRenderLoop() { }
        public void StopRenderLoop() { }

        public void Dispose()
        {
            StopRenderLoop();
            _device?.Dispose();
            _device = null;
            GC.SuppressFinalize(this);
        }
    }
}
