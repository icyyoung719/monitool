# monitool

A lightweight Windows 11 desktop overlay that displays real-time system metrics with a Fluent Design UI.

## Features

| Feature | Details |
|---|---|
| **Metrics** | CPU usage, RAM usage, GPU 3-D engine usage, thermal temperature |
| **Update rate** | Every 1 second (async, thread-pool) |
| **UI** | WinUI 3 (Windows App SDK), Mica backdrop, rounded corners, system font |
| **Window** | Always-on-top, borderless, draggable by clicking anywhere |
| **Global hotkey** | **Ctrl + Shift + M** — toggle overlay visibility |
| **Resource cost** | Negligible (PerformanceCounter + WMI, polled at 1 Hz) |

## Screenshots

The overlay appears in the top-right corner of the primary monitor:

```
┌──────────────────────────────────┐
│ System Monitor      Ctrl+Shift+M │
│ ─────────────────────────────── │
│ CPU  ██████░░░░░░░░░  42.3%     │
│ MEM  █████████░░░░░░   7.2G     │
│ GPU  ██░░░░░░░░░░░░░   8.0%     │
│ TEMP ████░░░░░░░░░░░   51°C     │
│ ─────────────────────────────── │
│          Updated 09:41:05        │
└──────────────────────────────────┘
```

## Requirements

* Windows 10 version 1903 (build 18362) or later — Windows 11 recommended for Mica.
* [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Windows App SDK 1.5 runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
* x64 or ARM64 CPU (x86 also supported)

> **Note:** Temperature readings require WMI access to `root\wmi\MSAcpi_ThermalZoneTemperature`, which may need administrator privileges on some OEM systems.
> GPU utilisation uses the WDDM 2.x performance counter (`Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine`), available on Windows 10 1703+ with a WDDM 2.0 driver.

## Build

```powershell
# Restore and build (x64 Release)
dotnet build MonitorTool/MonitorTool.csproj -c Release -r win-x64

# Run directly
dotnet run --project MonitorTool/MonitorTool.csproj -r win-x64
```

Or open `MonitorTool.sln` in Visual Studio 2022 (version 17.8+) and press **F5**.

## Project structure

```
monitool/
├── MonitorTool.sln
└── MonitorTool/
    ├── MonitorTool.csproj          # WinUI 3 / Windows App SDK project
    ├── app.manifest                # DPI awareness, Windows 10/11 compat
    ├── App.xaml / App.xaml.cs      # Application entry point
    ├── MainWindow.xaml             # Overlay UI (XAML)
    ├── MainWindow.xaml.cs          # Window logic: topmost, drag, hotkey, timer
    ├── Models/
    │   └── SystemMetrics.cs        # Metrics data model
    └── Services/
        ├── SystemMetricsService.cs # CPU / RAM / GPU / temperature collection
        └── GlobalHotkeyService.cs  # Ctrl+Shift+M global hotkey registration
```

## Architecture

```
DispatcherTimer (1 s, UI thread)
    └─► SystemMetricsService.GetMetricsAsync()
            └─► Task.Run (thread pool)
                    ├── PerformanceCounter  → CPU %
                    ├── GlobalMemoryStatusEx → RAM
                    ├── WMI GPUEngine       → GPU %
                    └── WMI ThermalZone     → °C
    └─► MainWindow.UpdateUi()  (back on UI thread via await)

GlobalHotkeyService
    RegisterHotKey(Ctrl+Shift+M) → WndProc subclass → AppWindow.Show/Hide
```
