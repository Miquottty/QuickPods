<p align="center">
  <img src="docs/assets/branding/quickpods-icon-v2.png" width="112" alt="QuickPods icon">
</p>

<h1 align="center">QuickPods</h1>

<p align="center">
  Fast access to Windows audio and paired Bluetooth audio devices from the Windows 11 taskbar.
</p>

<p align="center">
  <a href="https://github.com/Miquottty/QuickPods/actions/workflows/ci.yml"><img src="https://github.com/Miquottty/QuickPods/actions/workflows/ci.yml/badge.svg" alt="CI status"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2011-0078D4" alt="Windows 11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10">
</p>

<p align="center">
  <a href="README.ja.md">日本語</a> ·
  <a href="docs/user-guide.md">User guide</a> ·
  <a href="docs/development.md">Development</a> ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

![QuickPods Bluetooth audio flyout, taskbar control, and Settings window](docs/assets/screenshots/quickpods-overview.png)

> [!IMPORTANT]
> The public portable ZIP is currently unsigned and may trigger a Microsoft Defender SmartScreen warning. Download it only from this repository's Releases page and verify its published SHA-256 checksum. An MSI installer is not distributed until code signing is available.

## Features

- Control the current default output volume and mute state from a compact taskbar surface.
- List paired Bluetooth headphones, headsets, earbuds, and speakers in one flyout.
- Connect or disconnect a selected device when its Windows driver exposes a supported operation.
- Make a connected device the Windows Console and Multimedia default output.
- Follow external Windows audio and Bluetooth changes without requiring an app restart.
- Fall back to the notification area when safe taskbar placement cannot be verified.
- Use English by default, Japanese on Japanese Windows, or an explicit language selected in Settings.
- Check the latest stable GitHub Release manually or once per day at startup when explicitly enabled.
- Store settings and diagnostics per user without requiring administrator privileges.

## Requirements

- Windows 11 x64, build 22000 or later
- A horizontal Windows 11 taskbar with enough verified free space left of the notification area for the default placement; the alternative taskbar-left placement requires center alignment
- A compatible Bluetooth audio driver for direct connect and disconnect operations

The verified position immediately left of the notification area remains the default and supports center- and left-aligned taskbars. Settings also provides a **Left side of taskbar** option for the previous placement behavior. QuickPods continues to work from its notification-area icon when safe placement cannot be proven. Shipped packages are self-contained, so end users do not need to install .NET separately.

## Install and run

1. Download `QuickPods-<version>-win-x64.zip` and `SHA256SUMS.txt` from the [latest release](https://github.com/Miquottty/QuickPods/releases/latest).
2. Verify the ZIP's SHA-256 checksum against `SHA256SUMS.txt`.
3. Extract the complete `QuickPods` folder to a permanent location owned by your Windows user.
4. Run the `QuickPods.exe` located directly in that folder.

The package is self-contained and does not require a separate .NET installation. The root `QuickPods.exe` is a lightweight launcher; runtime files and helper processes are kept in the internal `app` folder, while license files are collected under `licenses`. Do not start or move files from `app` manually. Because the package is unsigned, Windows may display a SmartScreen warning on first launch. Confirm the download source and checksum before deciding whether to run it. Keep the extracted files together and do not move the folder after enabling start at sign-in.

For portable updates, removal, source builds, and packaging details, see the [user guide](docs/user-guide.md), [update policy](docs/release/update-policy.md), and [release guide](docs/release/README.md).

## How it works

QuickPods uses public Windows Core Audio APIs for volume, mute, endpoint observation, and most device discovery. Bluetooth mutations are sent only after the selected physical device and driver capability are verified. Taskbar placement is fail-closed: if QuickPods cannot prove that a region is safe, it hides the embedded surface and remains available from the notification area.

Some Windows desktop capabilities used by QuickPods are not stable public extension points. The architecture isolates those boundaries in helper processes and confirms the resulting OS state instead of treating an API return value as proof of success. See the [architecture overview](docs/architecture/README.md).

## Privacy and safety

QuickPods runs locally and does not include telemetry or a network service. Manual update checks—and optional startup checks when enabled—request only the latest public release metadata from GitHub. Logs intentionally omit raw Bluetooth addresses, Container IDs, PnP IDs, endpoint IDs, account names, and other device identifiers. Review the [privacy notes](docs/privacy.md) before attaching diagnostics to an issue.

## Build and test

The repository pins the .NET 10 SDK feature band in [`global.json`](global.json). From a Windows 11 x64 development machine:

```powershell
dotnet restore QuickPods.sln --locked-mode
dotnet format QuickPods.sln --verify-no-changes --no-restore --severity warn
dotnet build QuickPods.sln -c Release --no-restore
dotnet test QuickPods.sln -c Release --no-build --no-restore -- RunConfiguration.TreatNoTestsAsError=true
```

See [Development](docs/development.md) for Visual Studio, hardware-dependent validation, and packaging commands.

## Repository layout

| Path | Purpose |
|---|---|
| `src/` | Production application and helper processes |
| `tests/` | Automated unit, contract, and Windows integration tests |
| `spikes/` | Historical feasibility projects kept separate from production code |
| `build/` | Reproducible validation and packaging scripts |
| `installer/` | WiX per-user MSI project |
| `docs/` | User, architecture, development, privacy, and release documentation |

## Project status and support

QuickPods currently targets Windows 11 x64 and distributes an unsigned portable ZIP while a sustainable code-signing option is evaluated. Review the [known limitations](docs/release/known-limitations.md) before filing a defect.

- Use [GitHub Issues](https://github.com/Miquottty/QuickPods/issues) for reproducible bugs and focused feature requests.
- Read [SUPPORT.md](SUPPORT.md) for support expectations.
- Report security issues privately according to [SECURITY.md](SECURITY.md).
- See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

## License

QuickPods is available under the [MIT License](LICENSE). Third-party attributions are listed in [ThirdPartyNotices.txt](ThirdPartyNotices.txt).
