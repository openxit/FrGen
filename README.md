# FrGen

Universal open-source frame generation & upscaling for Windows (plus a Vulkan Implicit Layer for Windows and Linux/Steam Deck) — built for **all GPUs** (NVIDIA GTX/RTX, AMD Radeon, Intel HD/Arc) and **all games** without requiring game-side integration.

## Features
- **Lightweight Windows App (`FrGen.exe`)**: Clean .NET 9 WPF UI (~1.38 MB single-file executable) with zero bloat.
- **Universal GPU Optical-Flow Engine**: Direct3D 11 Compute Shader (`CS_OpticalFlow` + `CS_Interpolate` + optional `Sharp CAS` sharpening) — compatible with DirectX 11 Feature Level `11_0` and newer.
- **Click-Through In-Game Overlay**: Displays real-time metrics directly over your game:
  - **Base FPS (No FG / Real)**
  - **Output FPS (With FG)**
  - **Real vs. Generated Frame Split** (e.g., `60 Real · 60 Gen (2x)`)
  - **Frame Time & Optical-Flow Compute Time (`ms`)**
- **On-Demand GitHub Driver Layer**: Keeps `FrGen.exe` small by downloading the native C++ Vulkan Implicit Layer (`VK_LAYER_FRGEN.dll`) directly from GitHub Releases and registering it per-user (`HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers`).

## Hotkeys
- `Ctrl + Alt + F` — Toggle Frame Generation
- `Ctrl + Alt + O` — Toggle In-Game Overlay

## Build & Test
```powershell
dotnet test OpenFrameGen.sln -c Release
dotnet publish src/OpenFrameGen.App/OpenFrameGen.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
