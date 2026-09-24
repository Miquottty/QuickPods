# Contributing to QuickPods

Thank you for helping improve QuickPods. Contributions are welcome when they keep the application safe, focused, and understandable for Windows users.

## Before you start

- Search existing [issues](https://github.com/Miquottty/QuickPods/issues) and pull requests.
- Open a focused issue before a substantial behavior, architecture, dependency, installer, or Windows-interop change.
- Use a short-lived branch created from the latest `main`.
- Do not attach credentials, raw Bluetooth addresses, full Container/PnP/Endpoint IDs, account names, or unsanitized logs and screenshots.

## Development environment

QuickPods requires Windows 11 x64 and the SDK feature band pinned by [`global.json`](global.json). Visual Studio 2022 with the .NET desktop development workload is optional.

```powershell
git clone https://github.com/Miquottty/QuickPods.git
Set-Location QuickPods
dotnet restore QuickPods.sln --locked-mode
dotnet build QuickPods.sln -c Release --no-restore
dotnet test QuickPods.sln -c Release --no-build --no-restore -- RunConfiguration.TreatNoTestsAsError=true
```

Read [`docs/development.md`](docs/development.md) for the complete workflow.

## Pull requests

Keep each pull request reviewable and include:

1. The problem and user impact.
2. The chosen behavior and important tradeoffs.
3. Automated validation commands and results.
4. Manual Windows or Bluetooth validation when the change depends on hardware, shell state, DPI, theme, login, or Explorer lifecycle.
5. Updated public documentation for user-visible or operational changes.

Before requesting review, run:

```powershell
dotnet format QuickPods.sln --verify-no-changes --no-restore --severity warn
dotnet build QuickPods.sln -c Release --no-restore
dotnet test QuickPods.sln -c Release --no-build --no-restore -- RunConfiguration.TreatNoTestsAsError=true
./build/Test-RepositoryPublicReadiness.ps1
```

## Windows and Bluetooth safety

- Treat read-only discovery and OS mutation as separate operations.
- Never report connection, disconnection, or default-device success from a request return value alone; confirm the resulting Windows state.
- Preserve fail-closed behavior when device identity, ownership, capability, taskbar geometry, DPI, or Explorer generation is uncertain.
- Keep long-running Windows provider subscriptions and potentially blocking Bluetooth calls inside their existing process-lifetime boundaries.
- Do not add application-wide Bluetooth toggles or mutations that can affect unrelated devices.

## Coding conventions

- Follow `.editorconfig`; warnings and analyzer findings are build errors.
- Keep nullable annotations enabled and avoid suppressions without a narrow explanation.
- Add tests for new pure logic, contracts, parsers, state transitions, and safety boundaries.
- Keep public documentation in English. A Japanese translation is welcome when user-facing instructions change.
- Do not commit generated `bin/`, `obj/`, `artifacts/`, `TestResults/`, logs, dumps, or signing material.

## License

By contributing, you agree that your contribution is licensed under the repository's [MIT License](LICENSE).
