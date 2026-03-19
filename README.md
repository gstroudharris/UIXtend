# UIXtend

A high-performance Windows screen-magnification overlay designed for gamers and users with visual or motor impairments. Capture any region of your screen into a floating, resizable lens window that updates in real time at up to 144 FPS with near-zero CPU impact.

## Install
- **Unzip** the release folder.
- **Run** Install.bat.
- **Test** the program and let me know how it goes!

## Features

- **Region capture** — drag to select any area of any monitor and open it as a floating lens
- **Live & static modes** — toggle between a live feed and a frozen snapshot (useful for maps, reference images, etc.)
- **Input forwarding** — optionally forward mouse clicks, scroll, and drag events through the lens to the source region
- **Aspect-ratio-locked resizing** — hold Shift while resizing to lock to the original capture ratio
- **Multi-monitor aware** — works across all connected displays
- **System tray integration** — runs quietly in the tray; open the menu any time from the tray icon

## Requirements

| Requirement | Version |
|---|---|
| Windows | 11 (build 22000 or later) |
| .NET | 10 |
| Windows App SDK | 1.6 |
| Architecture | x64 |

## Building

```powershell
# Clone the repo
git clone https://github.com/gstroudharris/UIXtend.git
cd UIXtend/UIXtend

# Run (debug)
dotnet run --project UIXtend.csproj

# Run with logging enabled
dotnet run --project UIXtend.csproj -- --loggingEnabled

# Build release
dotnet build -c Release UIXtend.csproj
```

> **Note:** The Windows App SDK requires Visual Studio 2022 workloads or the standalone [Windows App SDK runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) to be installed.

## Project Structure

```
UIXtend/
├── Program.cs                      Entry point
├── App.cs                          WinUI Application host
├── Core/
│   ├── ServiceHost.cs              DI container and startup
│   ├── AppLogger.cs                File-based logger (opt-in via --loggingEnabled)
│   ├── Interfaces/                 IService, IModule, ICaptureService, etc.
│   ├── Services/
│   │   ├── WindowService.cs        Main menu window lifecycle
│   │   ├── ShellService.cs         System tray icon
│   │   ├── CaptureService.cs       Windows Graphics Capture integration
│   │   ├── LensService.cs          Lens window orchestration
│   │   ├── RegionSelectionService.cs  Monitor-aware drag-to-select overlay
│   │   └── RenderService.cs        GPU render loop (144 FPS target)
│   └── UI/
│       ├── MainMenuWindow.cs        Acrylic control panel
│       ├── LensWindow.cs            Per-capture floating overlay
│       └── RegionSelectionOverlay.cs  Full-screen transparent selection UI
└── assets/                         Icons and images
```

## License

Copyright (C) 2026 Grant Harris

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the [LICENSE](LICENSE) file for details.

## Attribution

Icons from [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) © Microsoft Corporation, MIT License.

Vibe coding assistance from [Claude Sonnet 4.6](https://claude.ai) by Anthropic.
