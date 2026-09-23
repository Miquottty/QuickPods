using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickPods.Contracts;
using QuickPods.Core;
using QuickPods.Core.Models;
using QuickPods.Core.Ports;
using QuickPods.Infrastructure.Logging;
using QuickPods.Infrastructure.Runtime;
using QuickPods.Infrastructure.Settings;
using QuickPods.Infrastructure.Updates;
using QuickPods.Presentation;
using QuickPods.Windows.Audio;
using QuickPods.Windows.Bluetooth;
using QuickPods.Windows.Settings;
using QuickPods.Windows.Startup;
using MediaColor = System.Windows.Media.Color;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfSystemColors = System.Windows.SystemColors;

namespace QuickPods.App;

public partial class App : WpfApplication, IDisposable
{
    private const string SingleInstanceId = "QuickPods.App.v1";

    private AudioController? controller;
    private BluetoothCatalogController? bluetoothCatalog;
    private WindowsBluetoothAudioCatalogPort? bluetoothCatalogPort;
    private BluetoothOperationController? bluetoothOperations;
    private WindowsBluetoothDeviceOperationPort? bluetoothOperationPort;
    private WindowsDefaultOutputOperationPort? defaultOutputOperationPort;
    private JsonBluetoothSelectionStore? bluetoothSelectionStore;
    private CancellationTokenSource? bluetoothLifetime;
    private Task? bluetoothInitialization;
    private TaskbarHostProcessManager? taskbarHost;
    private SingleInstanceLease? singleInstance;
    private TrayIconController? trayIcon;
    private WindowsCoreAudioEndpointPort? windowsAudio;
    private JsonLineLogger? logger;
    private HttpClient? updateHttpClient;
    private IApplicationUpdateChecker? updateChecker;
    private string logsDirectory = string.Empty;
    private string? restartExecutable;
    private readonly object updateCheckSync = new();
    private readonly ProductLocalizer localizer = new(
        ProductLanguageResolver.Resolve(QuickPodsLanguageMode.System));
    private readonly ProductLifetimePolicy lifetimePolicy = new();
    private readonly HashSet<string> pendingLifecycleReasons = new(StringComparer.Ordinal);
    private QuickPodsSettings productSettings = QuickPodsSettings.Default;
    private ApplicationUpdateViewState updateViewState = ApplicationUpdateViewState.NotChecked;
    private Task<ApplicationUpdateViewState>? activeUpdateCheck;
    private TaskbarThemeMode resolvedTaskbarTheme = TaskbarThemeMode.Dark;
    private DispatcherTimer? lifecycleRecoveryTimer;
    private DispatcherTimer? flyoutDismissTimer;
    private DispatcherTimer? bluetoothTopologyRefreshTimer;
    private TaskbarSurfaceAnchor? lastTaskbarAnchor;
    private bool lifecycleRecoveryRunning;
    private bool lifecycleRecoverySuspended;
    private bool lifecycleEventsSubscribed;
    private bool productSettingsInitialized;
    private bool flyoutAutoDismissActive;
    private bool disposed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyLocalizationResources();

        if (e.Args.Length == 1 && string.Equals(
            e.Args[0],
            "--unregister-startup",
            StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(RunUnregisterStartupCommand());
            return;
        }

        singleInstance = SingleInstanceLease.TryAcquire(SingleInstanceId);
        if (!singleInstance.IsPrimary)
        {
            singleInstance.SignalPrimary();
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }

        singleInstance.ActivationRequested += OnActivationRequested;
        singleInstance.StartActivationListener();
        InitializeLogging();
        Log(QuickPodsLogLevel.Information, "ApplicationStarted", "QuickPods started.");

        IAudioEndpointPort audioPort;
        string? startupDiagnostic = null;
        try
        {
            windowsAudio = new WindowsCoreAudioEndpointPort(
                () => localizer["DefaultOutputDevice"]);
            audioPort = windowsAudio;
        }
        catch (Exception exception)
        {
            audioPort = new UnavailableAudioEndpointPort();
            startupDiagnostic = localizer.Format("CoreAudioInitFailed", exception.Message);
        }

        controller = new AudioController(audioPort);
        InitializeSettingsStore();
        updateHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        updateChecker = new GitHubReleaseUpdateChecker(updateHttpClient);
        StartBluetoothCatalog();
        var settingsLauncher = new WindowsSettingsLauncher();
        string applicationExecutable = Path.Combine(AppContext.BaseDirectory, "QuickPods.exe");
        var startupRegistration = new WindowsStartupRegistration(applicationExecutable);
        var window = new MainWindow(
            controller,
            bluetoothCatalog,
            bluetoothOperations,
            settingsLauncher,
            bluetoothSelectionStore!,
            startupRegistration,
            lifetimePolicy,
            RestartApplication,
            () => updateViewState,
            () => CheckForUpdatesAsync(ApplicationUpdateCheckTrigger.Settings),
            OpenReleasePage,
            localizer,
            logsDirectory,
            startupDiagnostic);
        window.SettingsChanged += OnProductSettingsChanged;
        window.FlyoutPointerEntered += OnFlyoutPointerEntered;
        window.FlyoutPointerExited += OnFlyoutPointerExited;
        MainWindow = window;
        flyoutDismissTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(420),
            DispatcherPriority.Background,
            OnFlyoutDismissTick,
            Dispatcher)
        {
            IsEnabled = false,
        };
        bluetoothTopologyRefreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(450),
            DispatcherPriority.Background,
            OnBluetoothTopologyRefreshTick,
            Dispatcher)
        {
            IsEnabled = false,
        };
        if (windowsAudio is not null)
        {
            windowsAudio.TopologyChanged += OnAudioTopologyChanged;
        }

        bool trayIconAvailable = false;
        try
        {
            trayIcon = new TrayIconController(
                ShowMainWindow,
                () => _ = window.RequestRefreshAsync(),
                () => _ = CheckForUpdatesAsync(ApplicationUpdateCheckTrigger.Tray),
                window.ShowSettingsWindow,
                visible => _ = window.SetTaskbarSurfaceVisibleAsync(visible),
                () => settingsLauncher.TryOpenSoundSettings(),
                () => settingsLauncher.TryOpenBluetoothSettings(),
                ExitApplication,
                localizer);
            trayIconAvailable = true;
        }
        catch (Exception exception)
        {
            window.ReportSettingsFailure(
                localizer.Format("TrayInitFailed", exception.Message));
        }

        _ = window.InitializeAsync();
        if (StartupPresentationPolicy.ShouldShowInitialFlyout(trayIconAvailable))
        {
            ShowMainWindow();
        }

        string hostExecutable = Path.Combine(AppContext.BaseDirectory, "QuickPods.TaskbarHost.exe");
        taskbarHost = new TaskbarHostProcessManager(hostExecutable);
        controller.StateChanged += OnAudioStateChanged;
        taskbarHost.InteractionReceived += OnHostInteractionReceived;
        taskbarHost.StateChanged += OnTaskbarHostStateChanged;
        ApplyTheme(productSettings.Theme);
        taskbarHost.Start(CreateTaskbarSnapshot(controller.State));
        InitializeSystemLifecycle();

        if (bluetoothCatalog is not null && bluetoothLifetime is not null)
        {
            bluetoothInitialization = InitializeBluetoothCatalogAsync(bluetoothLifetime.Token);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        string? executable = restartExecutable;
        Dispose();
        if (!string.IsNullOrWhiteSpace(executable))
        {
            try
            {
                // Dispose releases the single-instance lease before the replacement starts.
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception exception)
            {
                _ = WpfMessageBox.Show(
                    localizer.Format("RestartLaunchFailed", exception.Message),
                    "QuickPods",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        lifetimePolicy.RequestExit();
        base.OnSessionEnding(e);
    }

    private static int RunUnregisterStartupCommand()
    {
        try
        {
            string applicationExecutable = Path.Combine(AppContext.BaseDirectory, "QuickPods.exe");
            var startupRegistration = new WindowsStartupRegistration(applicationExecutable);
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickPods",
                "settings.json");
            var cleanup = new UninstallCleanupService(
                startupRegistration,
                new JsonSettingsStore<QuickPodsSettings>(settingsPath));
            cleanup.ExecuteAsync(File.Exists(settingsPath)).AsTask().GetAwaiter().GetResult();

            return 0;
        }
        catch
        {
            return 1;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetimePolicy.RequestExit();
        Log(QuickPodsLogLevel.Information, "ApplicationStopping", "QuickPods is stopping.");
        UnsubscribeSystemLifecycle();
        bluetoothLifetime?.Cancel();
        trayIcon?.Dispose();
        trayIcon = null;
        if (controller is not null)
        {
            controller.StateChanged -= OnAudioStateChanged;
        }

        if (MainWindow is MainWindow window)
        {
            window.SettingsChanged -= OnProductSettingsChanged;
            window.FlyoutPointerEntered -= OnFlyoutPointerEntered;
            window.FlyoutPointerExited -= OnFlyoutPointerExited;
        }

        flyoutDismissTimer?.Stop();
        flyoutDismissTimer = null;
        bluetoothTopologyRefreshTimer?.Stop();
        bluetoothTopologyRefreshTimer = null;
        if (windowsAudio is not null)
        {
            windowsAudio.TopologyChanged -= OnAudioTopologyChanged;
        }

        if (taskbarHost is not null)
        {
            taskbarHost.InteractionReceived -= OnHostInteractionReceived;
            taskbarHost.StateChanged -= OnTaskbarHostStateChanged;
            taskbarHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (bluetoothCatalog is not null)
        {
            bluetoothCatalog.StateChanged -= OnBluetoothCatalogStateChanged;
        }

        if (bluetoothOperations is not null)
        {
            bluetoothOperations.StateChanged -= OnBluetoothOperationStateChanged;
        }

        try
        {
            bluetoothInitialization?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        bluetoothOperations?.Dispose();
        defaultOutputOperationPort?.Dispose();
        bluetoothOperationPort?.Dispose();
        bluetoothCatalog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        bluetoothSelectionStore?.Dispose();
        bluetoothCatalogPort?.Dispose();
        bluetoothLifetime?.Dispose();
        windowsAudio?.Dispose();
        updateHttpClient?.Dispose();
        updateHttpClient = null;
        updateChecker = null;
        logger?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        logger = null;
        if (singleInstance is not null)
        {
            singleInstance.ActivationRequested -= OnActivationRequested;
            singleInstance.Dispose();
            singleInstance = null;
        }

        GC.SuppressFinalize(this);
    }

    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs eventArgs) =>
        taskbarHost?.Publish(CreateTaskbarSnapshot(eventArgs.State));

    private void OnBluetoothCatalogStateChanged(
        object? sender,
        BluetoothAudioCatalogSnapshot catalog)
    {
        Log(
            QuickPodsLogLevel.Information,
            "BluetoothCatalogUpdated",
            "Bluetooth audio catalog updated.",
            new Dictionary<string, object?>
            {
                ["DeviceCount"] = catalog.Devices.Length,
                ["InventoryGeneration"] = catalog.InventoryGeneration,
                ["Revision"] = catalog.Revision,
                ["HasSelection"] = catalog.SelectedDeviceKey is not null,
            });
        taskbarHost?.Publish(CreateTaskbarSnapshot(controller?.State ?? AudioState.Unavailable, catalog));
    }

    private void OnBluetoothOperationStateChanged(
        object? sender,
        BluetoothOperationSnapshot operation)
    {
        Log(
            operation.Error is null ? QuickPodsLogLevel.Information : QuickPodsLogLevel.Warning,
            "BluetoothOperationStateChanged",
            "Bluetooth operation state changed.",
            new Dictionary<string, object?>
            {
                ["Action"] = operation.RequestedAction.ToString(),
                ["Operation"] = operation.Operation.ToString(),
                ["Outcome"] = operation.Outcome.ToString(),
                ["ConnectionState"] = operation.ConnectionState.ToString(),
                ["DefaultOutputState"] = operation.DefaultOutputState.ToString(),
                ["Error"] = operation.Error?.ToString(),
                ["Revision"] = operation.Revision,
            });
        taskbarHost?.Publish(CreateTaskbarSnapshot(
            controller?.State ?? AudioState.Unavailable,
            operation: operation));
    }

    private void OnTaskbarHostStateChanged(
        object? sender,
        TaskbarHostSupervisorState state)
    {
        if (state.Lifecycle != TaskbarHostLifecycle.Connected)
        {
            _ = Dispatcher.InvokeAsync(() => UpdateTaskbarSurfaceAnchor(null));
        }

        Log(
            state.Lifecycle == TaskbarHostLifecycle.DisabledForSession
                ? QuickPodsLogLevel.Warning
                : QuickPodsLogLevel.Information,
            "TaskbarHostStateChanged",
            "Taskbar host lifecycle changed.",
            new Dictionary<string, object?>
            {
                ["Lifecycle"] = state.Lifecycle.ToString(),
                ["ConsecutiveFailures"] = state.ConsecutiveFailures,
                ["HasScheduledRetry"] = state.RetryAfter is not null,
            });

        if (state.Lifecycle == TaskbarHostLifecycle.DisabledForSession)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow window)
                {
                    window.ReportSettingsFailure(
                        localizer["TaskbarDisabled"]);
                }
            });
        }
    }

    private void OnProductSettingsChanged(QuickPodsSettings settings)
    {
        QuickPodsSettings normalized = settings.Normalize();
        if (productSettingsInitialized && normalized == productSettings)
        {
            return;
        }

        bool firstApplication = !productSettingsInitialized;
        bool automaticChecksWereEnabled = productSettings.CheckForUpdatesAtStartup;
        productSettings = normalized;
        productSettingsInitialized = true;
        ProductLanguage resolvedLanguage = ProductLanguageResolver.Resolve(
            productSettings.Language);
        if (localizer.Language != resolvedLanguage)
        {
            localizer.Language = resolvedLanguage;
            ApplyLocalizationResources();
            trayIcon?.ApplyLocalization();
            if (MainWindow is MainWindow localizedWindow)
            {
                localizedWindow.ApplyLocalization();
            }
        }
        Log(
            QuickPodsLogLevel.Information,
            "SettingsApplied",
            "Product settings applied.",
            new Dictionary<string, object?>
            {
                ["DisplayMode"] = productSettings.DisplayMode.ToString(),
                ["TaskbarPlacement"] = productSettings.TaskbarPlacement.ToString(),
                ["Theme"] = productSettings.Theme.ToString(),
                ["Language"] = productSettings.Language.ToString(),
                ["MouseWheelStepPercent"] = productSettings.MouseWheelStepPercent,
                ["SetConnectedDeviceAsDefault"] =
                    productSettings.SetConnectedDeviceAsDefault,
                ["ConfirmBluetoothDisconnect"] =
                    productSettings.ConfirmBluetoothDisconnect,
                ["StartWithWindows"] = productSettings.StartWithWindows,
                ["CheckForUpdatesAtStartup"] = productSettings.CheckForUpdatesAtStartup,
            });
        ApplyTheme(productSettings.Theme);
        trayIcon?.SetTaskbarSurfaceVisible(
            productSettings.DisplayMode != QuickPodsDisplayMode.TrayOnly);
        taskbarHost?.Publish(CreateTaskbarSnapshot(
            controller?.State ?? AudioState.Unavailable));

        if (firstApplication)
        {
            ApplicationUpdateCheckResult? cached = ApplicationUpdatePolicy.TryCreateCachedResult(
                productSettings,
                GetProductVersion());
            if (cached is not null)
            {
                SetUpdateViewState(CreateUpdateViewState(cached));
            }
        }

        if (productSettings.CheckForUpdatesAtStartup &&
            (firstApplication || !automaticChecksWereEnabled) &&
            ApplicationUpdatePolicy.ShouldCheckAutomatically(
                productSettings,
                DateTimeOffset.UtcNow))
        {
            _ = Dispatcher.InvokeAsync(
                () => _ = CheckForUpdatesAsync(ApplicationUpdateCheckTrigger.Automatic),
                DispatcherPriority.Background);
        }
    }

    private Task<ApplicationUpdateViewState> CheckForUpdatesAsync(
        ApplicationUpdateCheckTrigger trigger)
    {
        Task<ApplicationUpdateViewState> check;
        lock (updateCheckSync)
        {
            if (activeUpdateCheck is null || activeUpdateCheck.IsCompleted)
            {
                activeUpdateCheck = CheckForUpdatesCoreAsync();
            }

            check = activeUpdateCheck;
        }

        return CompleteUpdateCheckRequestAsync(check, trigger);
    }

    private async Task<ApplicationUpdateViewState> CompleteUpdateCheckRequestAsync(
        Task<ApplicationUpdateViewState> check,
        ApplicationUpdateCheckTrigger trigger)
    {
        ApplicationUpdateViewState state = await check;
        if (trigger is ApplicationUpdateCheckTrigger.Tray ||
            (trigger is ApplicationUpdateCheckTrigger.Automatic &&
                state.Status == ApplicationUpdateViewStatus.UpdateAvailable))
        {
            ShowUpdateNotification(state);
        }

        return state;
    }

    private async Task<ApplicationUpdateViewState> CheckForUpdatesCoreAsync()
    {
        SetUpdateViewState(ApplicationUpdateViewState.Checking);
        try
        {
            IApplicationUpdateChecker checker = updateChecker ??
                throw new InvalidOperationException("The update service is unavailable.");
            ApplicationUpdateCheckResult result = await checker.CheckAsync(GetProductVersion());
            if (MainWindow is MainWindow window)
            {
                try
                {
                    await window.RecordUpdateCheckMetadataAsync(result);
                }
                catch (Exception exception)
                {
                    window.ReportSettingsFailure(
                        localizer.Format("SettingsSaveFailed", exception.Message));
                }
            }

            ApplicationUpdateViewState state = CreateUpdateViewState(result);
            SetUpdateViewState(state);
            Log(
                QuickPodsLogLevel.Information,
                "ApplicationUpdateChecked",
                "The latest QuickPods release was checked.",
                new Dictionary<string, object?>
                {
                    ["CurrentVersion"] = result.CurrentVersion.ToString(3),
                    ["LatestVersion"] = result.LatestVersion.ToString(3),
                    ["UpdateAvailable"] = result.IsUpdateAvailable,
                });
            return state;
        }
        catch (Exception exception)
        {
            var state = new ApplicationUpdateViewState(
                ApplicationUpdateViewStatus.Failed,
                ErrorDetail: exception.Message);
            SetUpdateViewState(state);
            Log(
                QuickPodsLogLevel.Warning,
                "ApplicationUpdateCheckFailed",
                "The latest QuickPods release could not be checked.",
                new Dictionary<string, object?>
                {
                    ["FailureType"] = exception.GetType().Name,
                });
            return state;
        }
    }

    private void SetUpdateViewState(ApplicationUpdateViewState state)
    {
        updateViewState = state;
        if (MainWindow is MainWindow window)
        {
            window.ReportUpdateCheckState(state);
        }
    }

    private static ApplicationUpdateViewState CreateUpdateViewState(
        ApplicationUpdateCheckResult result) => new(
            result.IsUpdateAvailable
                ? ApplicationUpdateViewStatus.UpdateAvailable
                : ApplicationUpdateViewStatus.UpToDate,
            result.CheckedAtUtc,
            result.LatestVersion,
            result.ReleasePage);

    private void ShowUpdateNotification(ApplicationUpdateViewState state)
    {
        switch (state.Status)
        {
            case ApplicationUpdateViewStatus.UpdateAvailable
                when state.LatestVersion is not null && state.ReleasePage is not null:
                trayIcon?.ShowNotification(
                    localizer["UpdateNotificationTitle"],
                    localizer.Format(
                        "UpdateAvailableNotification",
                        state.LatestVersion.ToString(3)),
                    () => OpenReleasePage(state.ReleasePage));
                break;
            case ApplicationUpdateViewStatus.UpToDate:
                trayIcon?.ShowNotification(
                    localizer["UpdateNotificationTitle"],
                    localizer.Format(
                        "UpToDateNotification",
                        GetProductVersion().ToString(3)));
                break;
            case ApplicationUpdateViewStatus.Failed:
                trayIcon?.ShowNotification(
                    localizer["UpdateNotificationTitle"],
                    localizer["UpdateCheckFailedNotification"]);
                break;
        }
    }

    private void OpenReleasePage(Uri releasePage)
    {
        ArgumentNullException.ThrowIfNull(releasePage);
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = releasePage.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            if (MainWindow is MainWindow window)
            {
                window.ReportSettingsFailure(
                    localizer.Format("ReleasePageOpenFailed", exception.Message));
            }
        }
    }

    private static Version GetProductVersion() =>
        typeof(App).Assembly.GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
            : new Version(0, 1, 0);

    private void StartBluetoothCatalog()
    {
        try
        {
            bluetoothCatalogPort = new WindowsBluetoothAudioCatalogPort();
            bluetoothCatalog = new BluetoothCatalogController(
                bluetoothCatalogPort,
                bluetoothSelectionStore!);
            bluetoothCatalog.StateChanged += OnBluetoothCatalogStateChanged;
            bluetoothOperationPort = new WindowsBluetoothDeviceOperationPort(
                bluetoothCatalogPort,
                (eventName, properties) => Log(
                    QuickPodsLogLevel.Information,
                    eventName,
                    "Bluetooth reconnect diagnostics.",
                    properties));
            defaultOutputOperationPort = new WindowsDefaultOutputOperationPort(bluetoothCatalogPort);
            bluetoothOperations = new BluetoothOperationController(
                bluetoothCatalog,
                bluetoothOperationPort,
                defaultOutputOperationPort,
                new WindowsBluetoothOperationGate());
            bluetoothOperations.StateChanged += OnBluetoothOperationStateChanged;
            bluetoothLifetime = new CancellationTokenSource();
        }
        catch
        {
            bluetoothOperations?.Dispose();
            defaultOutputOperationPort?.Dispose();
            bluetoothOperationPort?.Dispose();
            bluetoothCatalog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            bluetoothCatalogPort?.Dispose();
            bluetoothLifetime?.Dispose();
            bluetoothCatalog = null;
            bluetoothOperations = null;
            defaultOutputOperationPort = null;
            bluetoothOperationPort = null;
            bluetoothCatalogPort = null;
            bluetoothLifetime = null;
        }
    }

    private void InitializeSettingsStore()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickPods",
            "settings.json");
        var jsonSettings = new JsonSettingsStore<QuickPodsSettings>(settingsPath);
        jsonSettings.CorruptSettingsQuarantined += OnCorruptSettingsQuarantined;
        bluetoothSelectionStore = new JsonBluetoothSelectionStore(jsonSettings);
    }

    private void OnCorruptSettingsQuarantined(
        object? sender,
        SettingsRecoveryInfo recovery) =>
        Log(
            QuickPodsLogLevel.Warning,
            "CorruptSettingsQuarantined",
            "Corrupt settings were quarantined and safe defaults were loaded.",
            new Dictionary<string, object?>
            {
                ["FailureType"] = recovery.FailureType,
                ["QuarantineFile"] = Path.GetFileName(recovery.QuarantinedPath),
            });

    private void InitializeLogging()
    {
        logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickPods",
            "logs");
        try
        {
            Directory.CreateDirectory(logsDirectory);
            string logPath = Path.Combine(
                logsDirectory,
                $"quickpods-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
            var stream = new FileStream(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            logger = new JsonLineLogger(new StreamWriter(stream, new UTF8Encoding(false)));
        }
        catch
        {
            logger = null;
        }
    }

    private void Log(
        QuickPodsLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            logger.WriteAsync(new QuickPodsLogEntry(
                DateTimeOffset.UtcNow,
                level,
                eventName,
                message,
                properties ?? new Dictionary<string, object?>())).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Logging must never disable audio control, Bluetooth fallback, or shutdown.
        }
    }

    private async Task InitializeBluetoothCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (bluetoothCatalog is not null)
            {
                await bluetoothCatalog.InitializeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Catalog faults do not disable master-volume control or terminate the app.
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow window)
                {
                    window.ReportBluetoothCatalogFailure(exception.Message);
                }
            });
        }
    }

    private void OnHostInteractionReceived(object? sender, HostInteractionEnvelope interaction) =>
        _ = Dispatcher.InvokeAsync(() => HandleHostInteractionSafelyAsync(interaction)).Task.Unwrap();

    private void OnActivationRequested(object? sender, EventArgs eventArgs) =>
        _ = Dispatcher.InvokeAsync(ShowMainWindow);

    private async Task HandleHostInteractionAsync(HostInteractionEnvelope interaction)
    {
        if (HostInteractionEnvelope.IsObserverLifecycleNotification(interaction.Kind))
        {
            LogObserverLifecycle(interaction);
            return;
        }

        if (interaction.Kind == HostInteractionKind.TaskbarSurfaceAnchorChanged)
        {
            UpdateTaskbarSurfaceAnchor(interaction.Anchor);
            return;
        }

        if (controller is null)
        {
            return;
        }

        switch (interaction.Kind)
        {
            case HostInteractionKind.SetVolumePreview when interaction.VolumePercent is { } preview:
                controller.PreviewVolume(preview);
                break;
            case HostInteractionKind.SetVolumeCommit when interaction.VolumePercent is { } committed:
                await controller.CommitVolumeAsync(committed);
                break;
            case HostInteractionKind.ToggleMute:
                await controller.ToggleMuteAsync();
                break;
            case HostInteractionKind.OpenAudioFlyout:
            case HostInteractionKind.OpenContextMenu:
                ShowMainWindow(interaction.Anchor, activate: true, autoDismiss: true);
                break;
            case HostInteractionKind.PreviewAudioFlyout:
                ShowMainWindow(interaction.Anchor, activate: false, autoDismiss: true);
                break;
            case HostInteractionKind.TaskbarPointerExited:
                ScheduleFlyoutDismiss();
                break;
        }
    }

    private void UpdateTaskbarSurfaceAnchor(TaskbarSurfaceAnchor? anchor)
    {
        lastTaskbarAnchor = anchor;
        if (anchor is null || MainWindow is not MainWindow window || !window.IsVisible)
        {
            return;
        }

        window.PositionAboveTaskbar(anchor);
    }

    private void LogObserverLifecycle(HostInteractionEnvelope interaction)
    {
        bool fault = interaction.Kind is
            HostInteractionKind.TaskbarObserverFaulted or
            HostInteractionKind.TaskbarObserverDisconnected;
        Log(
            fault ? QuickPodsLogLevel.Warning : QuickPodsLogLevel.Information,
            "TaskbarObserverRetired",
            "A supervised taskbar observer generation was retired.",
            new Dictionary<string, object?>
            {
                ["Reason"] = interaction.Kind switch
                {
                    HostInteractionKind.TaskbarObserverTaskbarCreated => "TaskbarCreated",
                    HostInteractionKind.TaskbarObserverGenerationChanged => "ExplorerGenerationChanged",
                    HostInteractionKind.TaskbarObserverFaulted => "ObserverFaulted",
                    _ => "Disconnected",
                },
                ["GenerationOrdinal"] = interaction.ObserverGenerationOrdinal,
            });
    }

    private async Task HandleHostInteractionSafelyAsync(HostInteractionEnvelope interaction)
    {
        try
        {
            await HandleHostInteractionAsync(interaction);
        }
        catch (Exception exception)
        {
            Log(
                QuickPodsLogLevel.Warning,
                "TaskbarInteractionFailed",
                "A taskbar interaction failed without terminating QuickPods.",
                new Dictionary<string, object?>
                {
                    ["InteractionKind"] = interaction.Kind.ToString(),
                    ["FailureType"] = exception.GetType().Name,
                });
            if (MainWindow is MainWindow window)
            {
                window.ReportSettingsFailure(
                    localizer["TaskbarOperationFailed"]);
            }
        }
    }

    private void InitializeSystemLifecycle()
    {
        lifecycleRecoveryTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(750),
            DispatcherPriority.Background,
            OnLifecycleRecoveryTick,
            Dispatcher)
        {
            IsEnabled = false,
        };

        try
        {
            lifecycleEventsSubscribed = true;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }
        catch (Exception exception)
        {
            Log(
                QuickPodsLogLevel.Warning,
                "LifecycleMonitorUnavailable",
                "Windows lifecycle notifications could not be registered.",
                new Dictionary<string, object?>
                {
                    ["FailureType"] = exception.GetType().Name,
                });
            UnsubscribeSystemLifecycle();
        }
    }

    private void UnsubscribeSystemLifecycle()
    {
        lifecycleRecoveryTimer?.Stop();
        if (lifecycleRecoveryTimer is not null)
        {
            lifecycleRecoveryTimer.Tick -= OnLifecycleRecoveryTick;
            lifecycleRecoveryTimer = null;
        }

        if (!lifecycleEventsSubscribed)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        lifecycleEventsSubscribed = false;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs) =>
        QueueLifecycleRecovery("DisplaySettingsChanged");

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs) =>
        QueueLifecycleRecovery($"UserPreference:{eventArgs.Category}");

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(SystemParameters.HighContrast) or
            nameof(SystemParameters.WorkArea))
        {
            QueueLifecycleRecovery($"SystemParameter:{eventArgs.PropertyName}");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Suspend)
        {
            Log(
                QuickPodsLogLevel.Information,
                "SystemSuspending",
                "Windows is suspending; the visible product window was hidden.");
            PrepareForSystemTransition(clearPlacementAnchor: true, suspendRecovery: true);
            return;
        }

        PrepareForSystemTransition(clearPlacementAnchor: true, suspendRecovery: false);
        QueueLifecycleRecovery($"Power:{eventArgs.Mode}");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        SystemSessionTransition transition = eventArgs.Reason switch
        {
            SessionSwitchReason.SessionLock or
            SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or
            SessionSwitchReason.RemoteConnect => SystemSessionTransition.BecameUnavailable,
            SessionSwitchReason.SessionUnlock or
            SessionSwitchReason.SessionLogon or
            SessionSwitchReason.ConsoleConnect or
            SessionSwitchReason.RemoteDisconnect => SystemSessionTransition.BecameAvailable,
            _ => SystemSessionTransition.Other,
        };
        SystemSessionRecoveryDecision decision = SystemSessionRecoveryPolicy.Decide(transition);
        if (decision.HideFlyout || decision.ClearPlacementAnchor)
        {
            PrepareForSystemTransition(
                decision.ClearPlacementAnchor,
                decision.SuspendRecovery);
        }

        if (decision.QueueRecovery)
        {
            QueueLifecycleRecovery($"Session:{eventArgs.Reason}");
        }
    }

    private void PrepareForSystemTransition(
        bool clearPlacementAnchor,
        bool suspendRecovery) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            lifecycleRecoverySuspended = suspendRecovery;
            if (suspendRecovery)
            {
                lifecycleRecoveryTimer?.Stop();
                pendingLifecycleReasons.Clear();
            }

            flyoutAutoDismissActive = false;
            flyoutDismissTimer?.Stop();
            if (clearPlacementAnchor)
            {
                lastTaskbarAnchor = null;
            }

            if (MainWindow is MainWindow window)
            {
                if (clearPlacementAnchor)
                {
                    window.ClearPlacementAnchor();
                }

                window.Hide();
            }
        });

    private void QueueLifecycleRecovery(string reason)
    {
        if (disposed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (disposed || lifecycleRecoverySuspended || lifecycleRecoveryTimer is null)
            {
                return;
            }

            _ = pendingLifecycleReasons.Add(reason);
            lifecycleRecoveryTimer.Stop();
            lifecycleRecoveryTimer.Start();
        });
    }

    private async void OnLifecycleRecoveryTick(object? sender, EventArgs eventArgs)
    {
        lifecycleRecoveryTimer?.Stop();
        if (lifecycleRecoverySuspended || disposed)
        {
            return;
        }

        if (lifecycleRecoveryRunning)
        {
            lifecycleRecoveryTimer?.Start();
            return;
        }

        lifecycleRecoveryRunning = true;
        string[] reasons = [.. pendingLifecycleReasons];
        pendingLifecycleReasons.Clear();
        try
        {
            taskbarHost?.RequestEnvironmentRecovery();
            ApplyTheme(productSettings.Theme);
            if (MainWindow is MainWindow window)
            {
                await window.RecoverAfterSystemChangeAsync();
            }

            taskbarHost?.Publish(CreateTaskbarSnapshot(
                controller?.State ?? AudioState.Unavailable));
            Log(
                QuickPodsLogLevel.Information,
                "SystemLifecycleRecovered",
                "QuickPods re-evaluated Windows audio, Bluetooth, theme, and placement state.",
                new Dictionary<string, object?>
                {
                    ["Reasons"] = string.Join(",", reasons),
                    ["HighContrast"] = SystemParameters.HighContrast,
                });
        }
        catch (Exception exception)
        {
            Log(
                QuickPodsLogLevel.Warning,
                "SystemLifecycleRecoveryFailed",
                "QuickPods contained a lifecycle recovery failure.",
                new Dictionary<string, object?>
                {
                    ["FailureType"] = exception.GetType().Name,
                });
            if (MainWindow is MainWindow window)
            {
                window.ReportSettingsFailure(
                    localizer["EnvironmentRefreshFailed"]);
            }
        }
        finally
        {
            lifecycleRecoveryRunning = false;
        }
    }

    private void ShowMainWindow() =>
        ShowMainWindow(lastTaskbarAnchor, activate: true, autoDismiss: false);

    private void ShowMainWindow(
        TaskbarSurfaceAnchor? anchor,
        bool activate,
        bool autoDismiss)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        if (anchor is not null)
        {
            lastTaskbarAnchor = anchor;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.PositionAboveTaskbar(lastTaskbarAnchor);
        RequestBluetoothStateRefresh(window);
        flyoutAutoDismissActive = autoDismiss;
        flyoutDismissTimer?.Stop();

        if (activate)
        {
            _ = window.Activate();
        }
    }

    private void OnAudioTopologyChanged(object? sender, EventArgs eventArgs)
    {
        if (disposed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (disposed || bluetoothTopologyRefreshTimer is null)
            {
                return;
            }

            bluetoothTopologyRefreshTimer.Stop();
            bluetoothTopologyRefreshTimer.Start();
        }, DispatcherPriority.Background);
    }

    private void OnBluetoothTopologyRefreshTick(object? sender, EventArgs eventArgs)
    {
        bluetoothTopologyRefreshTimer?.Stop();
        Log(
            QuickPodsLogLevel.Information,
            "BluetoothTopologyRefreshRequested",
            "Windows reported an audio topology change; QuickPods requested a Bluetooth state refresh.");
        if (MainWindow is MainWindow window)
        {
            RequestBluetoothStateRefresh(window);
        }
    }

    private void RequestBluetoothStateRefresh(MainWindow window) =>
        _ = RefreshBluetoothStateSafelyAsync(window);

    private async Task RefreshBluetoothStateSafelyAsync(MainWindow window)
    {
        Task? initialization = bluetoothInitialization;
        if (initialization is null || disposed)
        {
            return;
        }

        try
        {
            await initialization;
            if (!disposed)
            {
                await window.RequestBluetoothRefreshAsync();
            }
        }
        catch (OperationCanceledException) when (
            disposed || bluetoothLifetime?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            Log(
                QuickPodsLogLevel.Warning,
                "BluetoothStateRefreshFailed",
                "QuickPods could not refresh Bluetooth state after a Windows topology change.",
                new Dictionary<string, object?>
                {
                    ["FailureType"] = exception.GetType().Name,
                });
        }
    }

    private void OnFlyoutPointerEntered(object? sender, EventArgs eventArgs) =>
        ScheduleFlyoutDismiss();

    private void OnFlyoutPointerExited(object? sender, EventArgs eventArgs) =>
        ScheduleFlyoutDismiss();

    private void ScheduleFlyoutDismiss()
    {
        if (!flyoutAutoDismissActive || flyoutDismissTimer is null)
        {
            return;
        }

        flyoutDismissTimer.Stop();
        flyoutDismissTimer.Start();
    }

    private void OnFlyoutDismissTick(object? sender, EventArgs eventArgs)
    {
        flyoutDismissTimer?.Stop();
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        switch (FlyoutDismissPolicy.Decide(
            flyoutAutoDismissActive,
            window.IsPointerWithinFlyoutBounds()))
        {
            case FlyoutDismissAction.Rearm:
                ScheduleFlyoutDismiss();
                break;
            case FlyoutDismissAction.Hide:
                flyoutAutoDismissActive = false;
                window.Hide();
                break;
        }
    }

    private void ExitApplication()
    {
        if (lifetimePolicy.IsExitRequested)
        {
            return;
        }

        lifetimePolicy.RequestExit();
        MainWindow?.Close();
        Shutdown();
    }

    private void RestartApplication()
    {
        if (lifetimePolicy.IsExitRequested)
        {
            return;
        }

        string executable = Path.Combine(AppContext.BaseDirectory, "QuickPods.exe");
        if (!File.Exists(executable))
        {
            if (MainWindow is MainWindow window)
            {
                window.ReportSettingsFailure(
                    localizer.Format("RestartExecutableMissing", executable));
            }

            return;
        }

        restartExecutable = executable;
        ExitApplication();
    }

    private TaskbarStateSnapshot CreateTaskbarSnapshot(
        AudioState audio,
        BluetoothAudioCatalogSnapshot? catalog = null,
        BluetoothOperationSnapshot? operation = null)
    {
        BluetoothAudioCatalogSnapshot currentCatalog =
            catalog ?? bluetoothCatalog?.State ?? BluetoothAudioCatalogSnapshot.Empty;
        BluetoothAudioDeviceDescriptor? selected = currentCatalog.Devices.FirstOrDefault(
            device => device.IsSelected);
        BluetoothOperationSnapshot currentOperation =
            operation ?? bluetoothOperations?.State ?? BluetoothOperationSnapshot.Idle;
        TaskbarDeviceView? selectedView = selected is null
            ? null
            : new TaskbarDeviceView(
                selected.DeviceKey.Value,
                selected.DisplayName,
                CreateBluetoothStatus(
                    selected,
                    currentCatalog.InventoryGeneration,
                    currentOperation),
                ResolveBluetoothConnection(
                    selected,
                    currentCatalog.InventoryGeneration,
                    currentOperation) == BluetoothConnectionState.Connected);
        return new(
            productSettings.DisplayMode == QuickPodsDisplayMode.TrayOnly
                ? TaskbarSurfaceMode.Hidden
                : TaskbarSurfaceMode.Native,
            Math.Clamp(audio.VolumePercent, 0, 100),
            audio.IsMuted,
            selectedView,
            resolvedTaskbarTheme,
            localizer.Language == ProductLanguage.Japanese
                ? TaskbarLanguage.Japanese
                : TaskbarLanguage.English,
            productSettings.TaskbarPlacement == QuickPodsTaskbarPlacement.NotificationAreaLeft
                ? TaskbarPlacementMode.NotificationAreaLeft
                : TaskbarPlacementMode.TaskbarLeft);
    }

    private void ApplyTheme(QuickPodsThemeMode theme)
    {
        resolvedTaskbarTheme = TaskbarThemeResolver.Resolve(
            theme,
            IsWindowsAppsLightTheme(),
            SystemParameters.HighContrast);
        if (resolvedTaskbarTheme == TaskbarThemeMode.HighContrast)
        {
            SetBrushColor("WindowBrush", WpfSystemColors.WindowColor);
            SetBrushColor("PanelBrush", WpfSystemColors.WindowColor);
            SetBrushColor("PanelBorderBrush", WpfSystemColors.WindowTextColor);
            SetBrushColor("ControlSurfaceBrush", WpfSystemColors.ControlColor);
            SetBrushColor("TrackBrush", WpfSystemColors.GrayTextColor);
            SetBrushColor("PrimaryTextBrush", WpfSystemColors.WindowTextColor);
            SetBrushColor("SecondaryTextBrush", WpfSystemColors.WindowTextColor);
            SetBrushColor("AccentBrush", WpfSystemColors.HighlightColor);
            SetBrushColor("HoverBrush", WpfSystemColors.ControlColor);
            SetBrushColor("SelectedBrush", WpfSystemColors.ControlColor);
            SetBrushColor("PressedBrush", WpfSystemColors.HotTrackColor);
            SetBrushColor("FocusBrush", WpfSystemColors.HighlightColor);
            SetBrushColor("ErrorBrush", WpfSystemColors.WindowTextColor);
            SetBrushColor("PrimaryButtonTextBrush", WpfSystemColors.HighlightTextColor);
            SetBrushColor("ToggleThumbBrush", WpfSystemColors.WindowColor);
            return;
        }

        if (resolvedTaskbarTheme == TaskbarThemeMode.Light)
        {
            SetBrushColor("WindowBrush", MediaColor.FromRgb(0xF5, 0xF7, 0xFA));
            SetBrushColor("PanelBrush", Colors.White);
            SetBrushColor("PanelBorderBrush", MediaColor.FromRgb(0xD5, 0xDA, 0xE1));
            SetBrushColor("ControlSurfaceBrush", MediaColor.FromRgb(0xE9, 0xED, 0xF2));
            SetBrushColor("TrackBrush", MediaColor.FromRgb(0xCB, 0xD1, 0xD9));
            SetBrushColor("PrimaryTextBrush", MediaColor.FromRgb(0x15, 0x18, 0x1C));
            SetBrushColor("SecondaryTextBrush", MediaColor.FromRgb(0x5A, 0x62, 0x6C));
            SetBrushColor("AccentBrush", MediaColor.FromRgb(0x32, 0xB9, 0xD0));
            SetBrushColor("HoverBrush", MediaColor.FromRgb(0xE1, 0xE6, 0xEC));
            SetBrushColor("SelectedBrush", MediaColor.FromRgb(0xD9, 0xF4, 0xF8));
            SetBrushColor("PressedBrush", MediaColor.FromRgb(0xCB, 0xD2, 0xDA));
            SetBrushColor("FocusBrush", MediaColor.FromRgb(0x00, 0x6C, 0x80));
            SetBrushColor("ErrorBrush", MediaColor.FromRgb(0xA4, 0x26, 0x2C));
            SetBrushColor("PrimaryButtonTextBrush", MediaColor.FromRgb(0x10, 0x21, 0x26));
            SetBrushColor("ToggleThumbBrush", Colors.White);
            return;
        }

        SetBrushColor("WindowBrush", MediaColor.FromRgb(0x17, 0x19, 0x1D));
        SetBrushColor("PanelBrush", MediaColor.FromRgb(0x20, 0x23, 0x28));
        SetBrushColor("PanelBorderBrush", MediaColor.FromRgb(0x36, 0x3A, 0x41));
        SetBrushColor("ControlSurfaceBrush", MediaColor.FromRgb(0x2B, 0x2E, 0x34));
        SetBrushColor("TrackBrush", MediaColor.FromRgb(0x48, 0x4D, 0x55));
        SetBrushColor("PrimaryTextBrush", MediaColor.FromRgb(0xF5, 0xF7, 0xFA));
        SetBrushColor("SecondaryTextBrush", MediaColor.FromRgb(0xAE, 0xB5, 0xBF));
        SetBrushColor("AccentBrush", MediaColor.FromRgb(0x67, 0xD7, 0xEA));
        SetBrushColor("HoverBrush", MediaColor.FromRgb(0x36, 0x3A, 0x41));
        SetBrushColor("SelectedBrush", MediaColor.FromRgb(0x1E, 0x30, 0x35));
        SetBrushColor("PressedBrush", MediaColor.FromRgb(0x40, 0x45, 0x4D));
        SetBrushColor("FocusBrush", MediaColor.FromRgb(0xB9, 0xF4, 0xFF));
        SetBrushColor("ErrorBrush", MediaColor.FromRgb(0xFF, 0xB4, 0xA9));
        SetBrushColor("PrimaryButtonTextBrush", MediaColor.FromRgb(0x10, 0x21, 0x26));
        SetBrushColor("ToggleThumbBrush", MediaColor.FromRgb(0xF5, 0xF7, 0xFA));
    }

    private void SetBrushColor(string resourceKey, MediaColor color)
    {
        Resources[resourceKey] = new SolidColorBrush(color);
    }

    private static bool IsWindowsAppsLightTheme()
    {
        try
        {
            using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalize?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private string CreateBluetoothStatus(
        BluetoothAudioDeviceDescriptor selected,
        long inventoryGeneration,
        BluetoothOperationSnapshot operation)
    {
        if (operation.Target is { } target &&
            target.DeviceKey == selected.DeviceKey &&
            target.InventoryGeneration == inventoryGeneration)
        {
            if (operation.Operation == QuickPodsOperation.Connecting)
            {
                return localizer["Connecting"];
            }

            if (operation.Operation == QuickPodsOperation.SettingDefault)
            {
                return localizer["SettingDefault"];
            }

            if (operation.Operation == QuickPodsOperation.DisconnectingOtherDevices)
            {
                return localizer["DisconnectingOthers"];
            }

            if (operation.Operation == QuickPodsOperation.Disconnecting)
            {
                return localizer["Disconnecting"];
            }

            if (operation.Outcome == BluetoothOperationOutcome.ConnectedNotDefault)
            {
                return localizer["ConnectedNotDefault"];
            }

            if (operation.ConnectionState == BluetoothConnectionState.Connected)
            {
                return operation.DefaultOutputState == DefaultOutputState.Default
                    ? localizer["ConnectedDefault"]
                    : localizer["Connected"];
            }

            if (operation.ConnectionState == BluetoothConnectionState.Disconnected)
            {
                return localizer["Disconnected"];
            }

            if (operation.Outcome == BluetoothOperationOutcome.Unsupported)
            {
                return localizer["Unsupported"];
            }
        }

        return selected.ConnectionState switch
        {
            BluetoothConnectionState.Connected => localizer["Connected"],
            BluetoothConnectionState.Disconnected => localizer["Disconnected"],
            BluetoothConnectionState.Unavailable => localizer["Unavailable"],
            _ => localizer["Checking"],
        };
    }

    private static BluetoothConnectionState ResolveBluetoothConnection(
        BluetoothAudioDeviceDescriptor selected,
        long inventoryGeneration,
        BluetoothOperationSnapshot operation) =>
        operation.Target is { } target &&
        target.DeviceKey == selected.DeviceKey &&
        target.InventoryGeneration == inventoryGeneration &&
        operation.ConnectionState != BluetoothConnectionState.Unknown
            ? operation.ConnectionState
            : selected.ConnectionState;

    private void ApplyLocalizationResources()
    {
        foreach (string key in ProductLocalizer.Keys)
        {
            Resources[key] = localizer[key];
        }
    }

    private sealed class UnavailableAudioEndpointPort : IAudioEndpointPort
    {
        private static readonly AudioState Unavailable = AudioState.Unavailable with
        {
            Capability = AudioCapability.ServiceUnavailable,
        };

        public event EventHandler<AudioStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask<AudioState> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Unavailable);

        public ValueTask<AudioState> SetVolumeAsync(
            int volumePercent,
            CancellationToken cancellationToken) => ValueTask.FromResult(Unavailable);

        public ValueTask<AudioState> SetMuteAsync(
            bool isMuted,
            CancellationToken cancellationToken) => ValueTask.FromResult(Unavailable);
    }
}
