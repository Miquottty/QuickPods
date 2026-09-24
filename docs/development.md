# Development

## Prerequisites

- Windows 11 x64
- .NET SDK 10.0.302 or a compatible patch in the feature band selected by [`global.json`](../global.json)
- Git
- Optional: Visual Studio 2022 with the .NET desktop development workload
- Optional for MSI packaging: the WiX SDK is restored through the installer project

QuickPods is Windows-specific. WPF, Win32, shell hooks and window event hooks, Core Audio, Configuration Manager, UI Automation, registry, and MSI behavior cannot be validated on Linux or macOS.

## Clone and restore

```powershell
git clone https://github.com/Miquottty/QuickPods.git
Set-Location QuickPods
dotnet restore QuickPods.sln --locked-mode
dotnet restore installer/QuickPods.Setup/QuickPods.Setup.wixproj --locked-mode
```

Committed NuGet lock files are authoritative. Update packages intentionally and commit every affected lock file.

## Build and run

```powershell
dotnet build QuickPods.sln -c Release --no-restore
./src/QuickPods.App/bin/Release/net10.0-windows10.0.26100.0/QuickPods.exe
```

Only one QuickPods instance is allowed per user session. Exit the notification-area instance before launching another build from a different path.

## Validate a change

```powershell
./build/Test-DependencyVulnerabilities.ps1
dotnet format QuickPods.sln --verify-no-changes --no-restore --severity warn
dotnet build QuickPods.sln -c Release --no-restore
dotnet test QuickPods.sln -c Release --no-build --no-restore `
  --logger "trx;LogFilePrefix=quickpods" `
  --results-directory TestResults `
  -- RunConfiguration.TreatNoTestsAsError=true
./build/Test-RepositoryPublicReadiness.ps1
```

CI runs the same locked restore, dependency audit, formatting, Release build, tests, and repository-publication checks on Windows.

## Hardware- and shell-dependent validation

Automated tests cannot prove every Windows integration behavior. Changes in these areas require focused local-console validation:

- Bluetooth enumeration, connect, disconnect, and default-output changes;
- taskbar placement, Start/Search interaction, DPI, monitor, and Explorer recovery;
- notification-area lifetime and explicit exit;
- Windows sign-in startup;
- MSI install, upgrade, and uninstall.

Do not infer local Bluetooth or taskbar behavior from Remote Desktop. Record the commit, Windows build, relevant driver versions, exact steps, and sanitized result in the pull request. Do not commit raw diagnostic logs merely to preserve a one-time test run.

## Packaging

Create a self-contained portable package:

```powershell
./build/Publish-ReleaseCandidate.ps1 -Version 0.1.0-rc.1
```

Create the per-user MSI:

```powershell
./build/Publish-Installer.ps1 -Version 0.1.0-rc.1
```

Outputs are written under ignored `artifacts/` directories. See the [release guide](release/README.md) and [installer guide](../installer/README.md) before distributing them.

## Repository conventions

- Production projects live in `src/`; experiments remain under `spikes/`.
- Cross-process contracts live in `QuickPods.Contracts` and are versioned explicitly.
- Core logic should remain independent of Windows adapters when practical.
- Treat mutation boundaries as fail-closed and confirm resulting OS state.
- Never commit generated outputs, raw logs, dumps, certificates, keys, or machine-specific configuration.
