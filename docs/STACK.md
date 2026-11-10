# Technology Stack and Architecture

## Application

| Area | Technology |
| --- | --- |
| Language | C# |
| SDK and runtime | .NET 10; `global.json` pins SDK 10.0.400 and rolls forward to later patches |
| Desktop UI | Avalonia 12.1.4 with Fluent theme |
| Architecture | MVVM with Avalonia data bindings |
| Video playback | LibVLCSharp 3.10.2, LibVLC 3.0.24 |
| Player project | `Dashcam.Remote.Player`, an Avalonia class library referenced by the desktop app |
| Shared UI project | `Dashcam.Remote.Shared`, common Avalonia icons, palette, and styles |
| File size formatting | Humanizer.Core 3.0.10 |
| Mock dashcam API | .NET 10 console app with ASP.NET Core minimal APIs |
| Target | Windows x64 |
| Application name | Dashcam Remote Control (`Dashcam.Remote.Control`) |

The Windows application and window icon is `src/Dashcam.Remote.Shared/Assets/Icons/dashcam-remote-control.ico`, derived from the repository icon `.editoricon.png`.

## Architecture

- `Dashcam.Remote.Player` contains the playback window, playback view model, GPS track parsing, and player controls.
- `Dashcam.Remote.Shared` owns icons, palette colors, and common Avalonia styles used by the app and player.
- `Dashcam.Remote.Control/Views/` contains the main Avalonia windows and AXAML.
- `Dashcam.Remote.Control/ViewModels/` owns app UI state, commands, and coordination.
- `Dashcam.Remote.Control/Models/` contains device and file explorer data types.
- `Dashcam.Remote.Control/Services/` contains device HTTP and playback media cache logic.

Keep device HTTP behavior in the service layer. Keep view code-behind for lifecycle and native-control integration. Preserve the `Dashcam.Remote` namespace.

## Device API and networking

- Observed device: Pelsee P12 Plus (`P12_Plus`).
- API: Eeasy HTTP JSON endpoints under `/app/*` over Wi-Fi.
- Default address: `192.168.169.1`; the address remains configurable in the connection panel.
- Metadata requests are serialized and spaced. File transfers use a separate queue with at most five concurrent downloads.
- API traffic uses plain HTTP. USB drivers are not used.
- `src/Dashcam.Remote.Control.MockDevice/` simulates the device API for local app development. It listens on HTTP port 80, reads `.ts` and `.jpg` files recursively from the `MockDevice:MediaFolder` setting in `appsettings.json`, and serves their actual bytes. It does not provide an RTSP stream.

## File explorer and downloads

When connected, the file explorer reads the camera's paged SD-card listing and stores a temporary local manifest cache. It supports search, front/rear pairing, folder actions, multi-selection, cancellation, and per-file download progress and status. File sizes are displayed in MB; after a download, the manifest is refreshed with the local file length.

When disconnected, the Files view can open a local folder and browse its files and subfolders. The local folder acts as the SD-card view. Camera download actions remain disabled in offline mode.

## Video and GPS

Video playback uses LibVLCSharp. Optional object detection uses ML.NET with the Tiny YOLOv2 ONNX model. The player samples frames at a bounded rate and draws labeled boxes over each visible camera view. GPS telemetry is read from LigoGPS records embedded in the trailer after MPEG-TS packets. The app can export tracks as GPX.

## Repository layout

```text
src/Dashcam.Remote.Control/  Desktop application source and project file
src/Dashcam.Remote.Player/  Reusable Avalonia playback library
src/Dashcam.Remote.Shared/  Shared UI resources and theme
src/Dashcam.Remote.Control.MockDevice/  Local HTTP device simulator
tests/UnitTests/             xUnit tests
docs/                        Project documentation
dashcam-remote-control.slnx  Solution
.config/                     Local .NET tool manifest
```

## Build and verification

Restore local tools and build the solution from the repository root:

```powershell
dotnet tool restore
dotnet build dashcam-remote-control.slnx
```

Run unit tests with:

```powershell
dotnet test dashcam-remote-control.slnx
```
