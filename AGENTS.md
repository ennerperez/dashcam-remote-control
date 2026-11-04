# Project instructions

## Overview

Read [README.md](README.md) and [docs/STACK.md](docs/STACK.md) before changing project structure or device behavior.

## Architecture

- Keep the Avalonia UI MVVM-based. Put view state and actions in view models and bind them from AXAML.
- Keep code-behind for view lifecycle and native-control integration only.
- Keep device HTTP behavior in the device API/service layer, not in views.
- Preserve the `Pelsee.Remote` namespace unless a change explicitly requires a namespace migration.
- Keep app startup files and the project file at `src/Dashcam.Remote.Control/` root.
- Put AXAML windows in `Views/`, UI state in `ViewModels/`, data types in `Models/`, custom controls in `Controls/`, and API/playback logic in `Services/`.
- Keep verification projects under `tests/`.
- Keep the repository icon at `.editoricon.png` and use the matching multi-size ICO for Windows app and window icons.

## Build and verification

- Restore local tools with `dotnet tool restore` when the tool manifest changes.
- Build the full solution with `dotnet build dashcam-remote-control.slnx`.
- The xUnit project at `tests/UnitTests/` is included in the solution; follow the parent `AGENTS.md` test-approval rule before running tests.
- Follow the parent `AGENTS.md` instructions for test approval, commits, and branches.

## Conventions

- Use the repository `.editorconfig` and `.gitattributes`.
- Use English for source, comments, commits, and documentation.
- Keep device addresses configurable; `192.168.169.1` is only the default observed during development.
