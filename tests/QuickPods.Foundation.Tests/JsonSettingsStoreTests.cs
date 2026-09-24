using System.IO;
using QuickPods.Core.Models;
using QuickPods.Infrastructure.Settings;
using Xunit;

namespace QuickPods.Foundation.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task CorruptSettingsAreQuarantinedAndSafeDefaultsAreReturned()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "QuickPods.Foundation.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{ not-valid-json");
            var json = new JsonSettingsStore<QuickPodsSettings>(path);
            using var settings = new JsonBluetoothSelectionStore(json);

            QuickPodsSettings loaded = await settings.LoadSettingsAsync();

            Assert.Equal(QuickPodsSettings.Default, loaded);
            SettingsRecoveryInfo recovery = Assert.IsType<SettingsRecoveryInfo>(json.LastRecovery);
            Assert.Equal(Path.GetFullPath(path), recovery.OriginalPath);
            Assert.True(File.Exists(recovery.QuarantinedPath));
            Assert.Equal("{ not-valid-json", await File.ReadAllTextAsync(recovery.QuarantinedPath));
            Assert.True(File.Exists(path));
            Assert.Equal(QuickPodsSettings.Default, await json.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsRoundTripPreservesSelectionAndOptions()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "QuickPods.Foundation.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new JsonSettingsStore<QuickPodsSettings>(path);
            var expected = new QuickPodsSettings(
                new BluetoothDeviceKey("opaque-device-key"),
                StartWithWindows: true,
                PreferNativeTaskbarSurface: false,
                DisplayMode: QuickPodsDisplayMode.TrayOnly,
                SetConnectedDeviceAsDefault: false,
                MouseWheelStepPercent: 5,
                Theme: QuickPodsThemeMode.Light,
                Language: QuickPodsLanguageMode.Japanese,
                ConfirmBluetoothDisconnect: true,
                TaskbarPlacement: QuickPodsTaskbarPlacement.NotificationAreaLeft,
                CheckForUpdatesAtStartup: true,
                LastUpdateCheckUtc: new DateTimeOffset(2026, 8, 8, 6, 30, 0, TimeSpan.Zero),
                LastKnownLatestVersion: "0.2.0",
                LastKnownReleasePage: "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0");

            await store.SaveAsync(expected);
            QuickPodsSettings? actual = await store.LoadAsync();

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LegacySettingsReceiveSafePhaseFiveDefaults()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "QuickPods.Foundation.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SelectedDevice": null,
                  "StartWithWindows": false,
                  "PreferNativeTaskbarSurface": true
                }
                """);
            using var settings = new JsonBluetoothSelectionStore(
                new JsonSettingsStore<QuickPodsSettings>(path));

            QuickPodsSettings migrated = await settings.LoadSettingsAsync();

            Assert.Equal(2, migrated.SchemaVersion);
            Assert.Equal(QuickPodsDisplayMode.Auto, migrated.DisplayMode);
            Assert.True(migrated.SetConnectedDeviceAsDefault);
            Assert.Equal(2, migrated.MouseWheelStepPercent);
            Assert.Equal(QuickPodsThemeMode.System, migrated.Theme);
            Assert.Equal(QuickPodsLanguageMode.System, migrated.Language);
            Assert.False(migrated.ConfirmBluetoothDisconnect);
            Assert.Equal(
                QuickPodsTaskbarPlacement.NotificationAreaLeft,
                migrated.TaskbarPlacement);
            Assert.False(migrated.CheckForUpdatesAtStartup);
            Assert.Null(migrated.LastUpdateCheckUtc);
            Assert.Null(migrated.LastKnownLatestVersion);
            Assert.Null(migrated.LastKnownReleasePage);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BluetoothSelectionStorePreservesUnrelatedOptions()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "QuickPods.Foundation.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");

        try
        {
            var settings = new JsonSettingsStore<QuickPodsSettings>(path);
            await settings.SaveAsync(new QuickPodsSettings(
                null,
                StartWithWindows: true,
                PreferNativeTaskbarSurface: false,
                DisplayMode: QuickPodsDisplayMode.TrayOnly,
                SetConnectedDeviceAsDefault: false,
                MouseWheelStepPercent: 10,
                Theme: QuickPodsThemeMode.Dark,
                Language: QuickPodsLanguageMode.English,
                ConfirmBluetoothDisconnect: true,
                TaskbarPlacement: QuickPodsTaskbarPlacement.NotificationAreaLeft,
                CheckForUpdatesAtStartup: true,
                LastUpdateCheckUtc: new DateTimeOffset(2026, 8, 8, 6, 30, 0, TimeSpan.Zero),
                LastKnownLatestVersion: "0.2.0",
                LastKnownReleasePage: "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0"));
            using var selection = new JsonBluetoothSelectionStore(settings);

            await selection.SaveAsync(new BluetoothDeviceKey("bt-device"));
            _ = await selection.UpdateSettingsAsync(
                current => current with { StartWithWindows = false });
            QuickPodsSettings actual = Assert.IsType<QuickPodsSettings>(await settings.LoadAsync());

            Assert.Equal(new BluetoothDeviceKey("bt-device"), actual.SelectedDevice);
            Assert.False(actual.StartWithWindows);
            Assert.False(actual.PreferNativeTaskbarSurface);
            Assert.Equal(QuickPodsDisplayMode.TrayOnly, actual.DisplayMode);
            Assert.False(actual.SetConnectedDeviceAsDefault);
            Assert.Equal(10, actual.MouseWheelStepPercent);
            Assert.Equal(QuickPodsThemeMode.Dark, actual.Theme);
            Assert.Equal(QuickPodsLanguageMode.English, actual.Language);
            Assert.True(actual.ConfirmBluetoothDisconnect);
            Assert.Equal(
                QuickPodsTaskbarPlacement.NotificationAreaLeft,
                actual.TaskbarPlacement);
            Assert.True(actual.CheckForUpdatesAtStartup);
            Assert.Equal(
                new DateTimeOffset(2026, 8, 8, 6, 30, 0, TimeSpan.Zero),
                actual.LastUpdateCheckUtc);
            Assert.Equal("0.2.0", actual.LastKnownLatestVersion);
            Assert.Equal(
                "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0",
                actual.LastKnownReleasePage);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
