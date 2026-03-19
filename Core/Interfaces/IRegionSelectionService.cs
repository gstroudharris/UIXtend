// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Tasks;

namespace UIXtend.Core.Interfaces
{
    public interface IRegionSelectionService : IService
    {
        Task<Windows.Foundation.Rect?> StartSelectionAsync();
    }
}
