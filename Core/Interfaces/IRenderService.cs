// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Graphics.Canvas;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Owns the shared CanvasDevice (D3D11) used by all capture sessions and render paths.
    /// StartRenderLoop/StopRenderLoop are reserved for the future GPU overlay compositor.
    /// </summary>
    public interface IRenderService : IService
    {
        CanvasDevice Device { get; }
        void StartRenderLoop();
        void StopRenderLoop();
    }
}
