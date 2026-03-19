// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Base interface for all feature modules in the application.
    /// Modules must be self-contained and disposable.
    /// </summary>
    public interface IModule : IDisposable
    {
        void Initialize();
    }
}
