# Open-FrameGen (`FrGen`)

Universelle Open-Source Frame-Generation & Upscaling für Windows (und Linux/Steam Deck Layer) — für **alle GPUs** (NVIDIA GTX 900/1000+, RTX 2000/3000+, AMD, Intel) und **alle Spiele** ohne Game-Support.

## Features
- **Schlanke Windows-App (`OpenFrameGen.exe`)**: .NET 9 WPF-UI ohne Bloat.
- **Universelle GPU Optical-Flow Engine**: Direct3D 11 Compute Shader (`CS_OpticalFlow` + `CS_Interpolate` + optionales `Sharp CAS` Upscaling) — funktioniert ab DirectX 11 Feature Level 11_0.
- **Klick-durchlässiges In-Game Overlay**: Zeigt in Echtzeit:
  - **Base FPS (ohne Frame-Gen / Real)**
  - **Output FPS (mit Frame-Gen)**
  - **Real vs. Generierte Frames** (z. B. `60 Real · 60 Gen (2x)`)
  - **Frame-Time & Optical-Flow Compute-Zeit (`ms`)**
- **Nachladbarer Treiber-Layer über GitHub Releases**: Die `.exe` bleibt klein und lädt den nativen C++ Vulkan Implicit Layer (`VK_LAYER_OPEN_FRAMEGEN.dll`) direkt von GitHub Releases herunter und registriert ihn benutzerlokal (`HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers`).

## Hotkeys
- `Ctrl + Alt + F` — Frame-Generation starten / stoppen
- `Ctrl + Alt + O` — In-Game Overlay ein- / ausblenden

## Build & Tests
```powershell
dotnet test OpenFrameGen.sln -c Release
dotnet publish src/OpenFrameGen.App/OpenFrameGen.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
