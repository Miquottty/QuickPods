# Privacy

QuickPods is a local Windows desktop application. It does not include telemetry, analytics, advertising, cloud synchronization, or a network service. A manual update check—and an optional startup check when explicitly enabled—makes an HTTPS request to GitHub's public Releases API for `Miquottty/QuickPods`. The request includes the QuickPods version in its standard HTTP user-agent and does not include Bluetooth, audio, account, or settings data. Automatic checks are disabled by default and limited to once every 24 hours.

## Local data

QuickPods stores data below `%LocalAppData%\QuickPods`:

- `settings.json` — user preferences, an opaque selected-device key, and the time, version, and official page URL from the last successful update check;
- `logs\quickpods-YYYYMMDD.jsonl` — structured operational diagnostics;
- `settings.corrupt-*.json` — a quarantined settings file only when malformed JSON is detected.

The Windows sign-in option stores one current-user Run value that launches the installed `QuickPods.exe --background`. Helper processes do not register themselves for startup.

## Diagnostic minimization

Normal logs and copied diagnostics are designed not to include:

- Bluetooth MAC addresses;
- raw Container IDs;
- full Endpoint or PnP IDs;
- Windows account names or SIDs;
- unrelated notification content;
- signing secrets or certificate material.

Friendly device names, state classifications, counts, versions, and sanitized failure categories may appear because they are needed to diagnose user-visible behavior.

## Sharing diagnostics

Always inspect logs, copied diagnostics, screenshots, MSI logs, and registry excerpts before attaching them to a public issue. Screen captures can reveal paired device names, wallpaper, notifications, account details, and other applications even when QuickPods logs are sanitized.

Never publish `.pfx`, `.p12`, `.pem`, `.key`, `.env`, memory dump, or complete registry export files.

## Removal

Uninstalling QuickPods preserves settings and logs so that reinstall and upgrade remain non-destructive. To remove them manually, exit QuickPods, uninstall the app, and delete `%LocalAppData%\QuickPods`. This action cannot be undone.
