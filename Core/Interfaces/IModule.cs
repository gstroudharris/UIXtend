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
