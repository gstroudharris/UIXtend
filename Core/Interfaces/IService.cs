// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Base interface for all core services in the application.
    /// Every service must be disposable to ensure proper cleanup of Win32/GPU resources.
    /// </summary>
    public interface IService : IDisposable
    {
        void Initialize();
    }
}
