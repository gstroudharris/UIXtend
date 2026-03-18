UIXtend Project Instructions
1. Core Architecture Tenets
Strict Modularity: All features must be self-contained modules.

Unidirectional Dependency: Dependencies must flow from Core Services -> Feature Modules -> UI. Modules must never depend on each other directly.

Interface-Based Design: Always define behaviors in Interfaces (e.g., ICaptureService) before implementing them in concrete classes.

Service Provider Pattern: Use a central Service Provider/Host for dependency injection. Modules should "request" services from the host.

2. Technical Stack & Constraints
Framework: .NET 10 with WinUI 3 (Windows App SDK).

Language: C# 14.

Primary APIs: Windows Graphics Capture (WGC) for screen data, Win32 via Microsoft.Windows.CsWin32.

Performance: Target 144 FPS for overlays with near-zero CPU impact. Use GPU-based composition/shaders for all visual transformations.

Low-Level Windowing: Use SetWindowLongPtr for styles like WS_EX_TRANSPARENT and WS_EX_TOOLWINDOW.

3. Design & UX Guidelines
Material: Use Mica Alt for primary windows and Acrylic for transient/contextual UI.

Visuals: Follow Windows 11 design language (8px rounded corners, Segoe Fluent icons, Segoe UI Variable font).

Accessibility: * Maintain a 4.5:1 contrast ratio.

Every interactive element must have a clear focus ring.

Support keyboard-only navigation for all menus.

4. Coding Standards
Naming: Use the "I" prefix for all interfaces (e.g., IService, IModule).

Resource Management: Every service/module must implement IDisposable to clean up Win32 handles and GPU resources.

No "God Objects": Keep the App.xaml.cs and MainWindow.xaml.cs lightweight; move logic into dedicated Services.

Always use a "Balanced" (Medium) reasoning approach. Avoid over-explaining simple C# syntax, but provide deep chain-of-thought reasoning for Win32 API interactions and memory management.