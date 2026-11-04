# Technology Stack and Architecture

## Application

| Area | Technology |
| --- | --- |
| Language | C# |
| SDK and runtime | .NET 10; `global.json` pins SDK 10.0.400 and rolls forward to later patches |
| Desktop UI | Avalonia 12.1.4 with Fluent theme |
| Architecture | MVVM with Avalonia data bindings |
| Video playback | LibVLCSharp 3.10.2, LibVLC 3.0.24 |
| File size formatting | Humanizer.Core 3.0.10 |
| Target | Windows x64 |
| Application name | Dashcam Remote Control (`Dashcam.Remote.Control`) |

The Windows application and window icon is `src/Dashcam.Remote.Control/Assets/Icons/dashcam-remote-control.ico`, derived from the repository icon `.editoricon.png`.

## Architecture

- `Views/` contains Avalonia windows and AXAML.
- `ViewModels/` owns UI state, commands, and coordination.
- `Models/` contains device, file explorer, and GPS data types.
- `Services/` contains device HTTP and playback logic.
- `Controls/` contains custom map and chart controls.

Keep device HTTP behavior in the service layer. Keep view code-behind for lifecycle and native-control integration. Preserve the `Dashcam.Remote` namespace.

## Device API and networking

- Observed device: Pelsee P12 Plus (`P12_Plus`).
- API: Eeasy HTTP JSON endpoints under `/app/*` over Wi-Fi.
- Default address: `192.168.169.1`; the address remains configurable in the connection panel.
- Metadata requests are serialized and spaced. File transfers use a separate queue with at most five concurrent downloads.
- API traffic uses plain HTTP. USB drivers are not used.

## File explorer and downloads

When connected, the file explorer reads the camera's paged SD-card listing and stores a temporary local manifest cache. It supports search, front/rear pairing, folder actions, multi-selection, cancellation, and per-file download progress and status. File sizes are displayed in MB; after a download, the manifest is refreshed with the local file length.

When disconnected, the Files view can open a local folder and browse its files and subfolders. The local folder acts as the SD-card view. Camera download actions remain disabled in offline mode.

## Video and GPS

Video playback uses LibVLCSharp. GPS telemetry is read from LigoGPS records embedded in the trailer after MPEG-TS packets. The app can export tracks as GPX.

## Repository layout

```text
src/Dashcam.Remote.Control/  Application source and project file
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
