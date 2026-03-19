// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// A handle to an active capture of a specific screen region.
    /// One IRegionCapture exists per lens window; multiple captures on the same physical
    /// monitor share a single underlying WGC session (managed by CaptureService).
    /// FrameArrived delivers the full monitor bitmap — use CropRect as the source rect
    /// when drawing to avoid an extra GPU copy.
    /// </summary>
    public interface IRegionCapture : IDisposable
    {
        /// <summary>Unique ID for this capture, used to identify the associated lens window.</summary>
        int Id { get; }

        /// <summary>The user's original selection in global virtual-screen physical pixels.</summary>
        Rect Region { get; }

        /// <summary>
        /// The equivalent rect in monitor-local physical pixels.
        /// Use this as the source rect when drawing the full-monitor CanvasBitmap.
        /// </summary>
        Rect CropRect { get; }

        /// <summary>
        /// Fires on the WGC capture thread at the monitor's native refresh rate.
        /// The CanvasBitmap covers the FULL monitor — use CropRect to crop.
        /// The bitmap is only valid for the duration of the event handler; do not retain it.
        /// </summary>
        event EventHandler<CanvasBitmap>? FrameArrived;
    }
}
