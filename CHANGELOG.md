# Changelog

All notable changes to QuickPods will be documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and released versions use [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.2] - 2026-09-23

### Fixed

- Connecting AirPods and other multi-profile headsets now succeeds on the first click. QuickPods requests reconnection on every audio profile of the device (stereo and Hands-Free), as Windows Settings does, and re-issues the request if nothing has come up after 4 and 8 seconds.

## [1.0.1] - 2026-09-07

### Changed

- The taskbar observer now uses Win32 shell hooks, window event hooks, and registry notifications instead of a UI Automation event subscription, so browsers no longer detect QuickPods as an assistive-technology client or enable accessibility mode because of it.
- The taskbar observer no longer depends on the Windows Desktop framework.

### Fixed

- Restored locked-mode restore on the current .NET SDK patch by updating the launcher lock file.

## [1.0.0] - 2026-08-08

### Added

- Added manual latest-release checks from the notification-area menu and Settings.
- Added an opt-in startup update check limited to once every 24 hours.

### Changed

- Added a taskbar-placement setting. The verified notification-area-adjacent position remains the default, with the previous taskbar-left position available as an alternative.
- Reorganized the portable package around an obvious root `QuickPods.exe` launcher.
- Moved the application runtime and helper executables into an internal `app` directory.
- Collected license and third-party notice files in a dedicated `licenses` directory.
- Added a bilingual package README with startup, security, update, and internal-file guidance.
- Clarified the existing tray refresh action as a device-status refresh.

## [0.1.1] - 2026-08-08

### Changed

- Made the self-contained Windows x64 ZIP the official public distribution format.
- Stopped publishing unsigned MSI artifacts while code signing is unavailable.
- Added portable installation, update, removal, checksum, and unsigned-app guidance.

## [0.1.0] - 2026-08-08

### Added

- English and Japanese localization with an in-app language selector.
- Paired Bluetooth audio selection, guarded connect/disconnect operations, and default-output switching.
- Taskbar volume controls, notification-area fallback, settings, startup registration, diagnostics, and per-user MSI packaging.

### Changed

- Prepared documentation, contribution guidance, CI templates, and packaging metadata for a public open-source repository.

### Security

- Kept device mutation fail-closed and excluded raw device and account identifiers from normal diagnostics.

[Unreleased]: https://github.com/rimtty/QuickPods/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/rimtty/QuickPods/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/rimtty/QuickPods/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/rimtty/QuickPods/compare/v0.1.1...v1.0.0
[0.1.1]: https://github.com/rimtty/QuickPods/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/rimtty/QuickPods/releases/tag/v0.1.0
