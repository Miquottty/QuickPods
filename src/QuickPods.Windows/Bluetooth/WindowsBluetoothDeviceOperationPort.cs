using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickPods.Core.Models;
using QuickPods.Core.Ports;
using QuickPods.Windows.Audio.Interop;
using QuickPods.Windows.Bluetooth.Worker;

namespace QuickPods.Windows.Bluetooth;

public sealed class WindowsBluetoothDeviceOperationPort : IBluetoothDeviceOperationPort, IDisposable
{
    private const int ElementNotFound = unchecked((int)0x80070490);
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisconnectedStableWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(150);

    // A single one-shot reconnect can be consumed by baseband paging (for example AirPods
    // that are asleep or attached to a phone) without the profile connection completing, and
    // the Bluetooth stack does not retry it. Re-issue the request while nothing has come up.
    internal static readonly TimeSpan[] ReconnectRetrySchedule =
        [TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    private readonly WindowsBluetoothAudioCatalogPort catalog;
    private readonly BluetoothWorkerProcessRunner worker;
    private readonly MtaAudioWorker audioWorker = new();
    private readonly Action<string, IReadOnlyDictionary<string, object?>>? diagnostics;
    private int disposed;

    public WindowsBluetoothDeviceOperationPort(
        WindowsBluetoothAudioCatalogPort catalog,
        Action<string, IReadOnlyDictionary<string, object?>>? diagnostics = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.diagnostics = diagnostics;
        string workerPath = Path.Combine(AppContext.BaseDirectory, "QuickPods.BluetoothWorker.exe");
        worker = new BluetoothWorkerProcessRunner(workerPath);
    }

    public ValueTask<BluetoothDeviceOperationResult> ConnectAsync(
        BluetoothOperationTarget target,
        CancellationToken cancellationToken) => ExecuteConnectAsync(target, cancellationToken);

    public ValueTask<BluetoothDeviceOperationResult> DisconnectAsync(
        BluetoothOperationTarget target,
        CancellationToken cancellationToken) => ExecuteDisconnectAsync(target, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            audioWorker.Dispose();
        }
    }

    private async ValueTask<BluetoothDeviceOperationResult> ExecuteConnectAsync(
        BluetoothOperationTarget target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (WindowsSessionContext.IsRemoteSession ||
            !catalog.TryResolveBinding(target, out WindowsBluetoothDeviceBinding? binding))
        {
            return Failure(
                BluetoothConnectionState.Unknown,
                BluetoothMutationFailure.OwnershipUnknown);
        }

        EndpointObservation preflight = await ObserveOnceAsync(binding).ConfigureAwait(false);
        if (preflight.RenderActive)
        {
            return new(
                BluetoothConnectionState.Connected,
                RequestSubmitted: false,
                BluetoothMutationFailure.None);
        }

        // The catalog/controller already admitted only a DirectControl descriptor from this
        // exact inventory generation. Repeating every Basic Support probe here starts several
        // isolated workers for multi-profile devices and delays the actual reconnect request.
        // The mutation worker still validates the concrete adapter and safely reports failure.
        string? reconnectAdapter = ResolveReconnectAdapter(binding);
        if (reconnectAdapter is null || !catalog.TryResolveBinding(target, out _))
        {
            return Failure(preflight.ConnectionState, BluetoothMutationFailure.OwnershipUnknown);
        }

        // Like the Windows Settings "Connect" button, ask every profile of the device
        // (A2DP and Hands-Free) to reconnect. Only the stereo request decides success.
        string[] secondaryAdapters = ResolveSecondaryReconnectAdapters(binding, reconnectAdapter);
        var clock = Stopwatch.StartNew();
        var pendingRequests = new List<Task<BluetoothMutationFailure>>();
        try
        {
            Task<BluetoothMutationFailure> primary = StartReconnect(
                target.DeviceKey,
                reconnectAdapter,
                secondaryAdapters,
                pendingRequests,
                cancellationToken);
            BluetoothMutationFailure requestFailure = await primary.ConfigureAwait(false);
            ReportReconnectRequested(attempt: 1, requestFailure, clock);
            if (requestFailure != BluetoothMutationFailure.None)
            {
                return Failure(preflight.ConnectionState, requestFailure, submitted: true);
            }

            return await ObserveConnectedAsync(
                binding,
                target.DeviceKey,
                reconnectAdapter,
                secondaryAdapters,
                pendingRequests,
                clock).ConfigureAwait(false);
        }
        finally
        {
            // Never leave a reconnect worker running into the next operation (e.g. an
            // immediate disconnect). Secondary requests do not throw.
            _ = await Task.WhenAll(pendingRequests).ConfigureAwait(false);
        }
    }

    private async ValueTask<BluetoothDeviceOperationResult> ExecuteDisconnectAsync(
        BluetoothOperationTarget target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (WindowsSessionContext.IsRemoteSession ||
            !catalog.TryResolveBinding(target, out WindowsBluetoothDeviceBinding? binding))
        {
            return Failure(
                BluetoothConnectionState.Unknown,
                BluetoothMutationFailure.OwnershipUnknown);
        }

        EndpointObservation preflight = await ObserveOnceAsync(binding).ConfigureAwait(false);
        if (preflight.AllDisconnected)
        {
            return new(
                BluetoothConnectionState.Disconnected,
                RequestSubmitted: false,
                BluetoothMutationFailure.None);
        }

        string[] disconnectAdapters = ResolveDisconnectAdapters(binding);
        if (disconnectAdapters.Length == 0 || !catalog.TryResolveBinding(target, out _))
        {
            return Failure(preflight.ConnectionState, BluetoothMutationFailure.OwnershipUnknown);
        }

        bool submitted = false;
        foreach (string adapter in disconnectAdapters)
        {
            if (!catalog.TryResolveBinding(target, out _))
            {
                EndpointObservation staleObservation = await ObserveOnceAsync(binding)
                    .ConfigureAwait(false);
                return Failure(
                    staleObservation.ConnectionState,
                    BluetoothMutationFailure.OwnershipUnknown,
                    submitted);
            }

            BluetoothMutationFailure requestFailure = await RunMutationAsync(
                target.DeviceKey,
                adapter,
                BluetoothWorkerOperation.Disconnect,
                submitted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            submitted = true;
            if (requestFailure != BluetoothMutationFailure.None)
            {
                return Failure(preflight.ConnectionState, requestFailure, submitted: true);
            }
        }

        return await ObserveDisconnectedAsync(binding).ConfigureAwait(false);
    }

    private async Task<BluetoothDeviceOperationResult> ObserveConnectedAsync(
        WindowsBluetoothDeviceBinding binding,
        BluetoothDeviceKey deviceKey,
        string reconnectAdapter,
        string[] secondaryAdapters,
        List<Task<BluetoothMutationFailure>> pendingRequests,
        Stopwatch clock)
    {
        var stopwatch = Stopwatch.StartNew();
        EndpointObservation latest = EndpointObservation.Unknown;
        int retries = 0;
        while (stopwatch.Elapsed < OperationDeadline)
        {
            latest = await ObserveOnceAsync(binding).ConfigureAwait(false);
            if (latest.RenderActive)
            {
                ReportReconnectObserved(BluetoothConnectionState.Connected, retries, clock);
                return new(
                    BluetoothConnectionState.Connected,
                    RequestSubmitted: true,
                    BluetoothMutationFailure.None);
            }

            if (ShouldRetryReconnect(stopwatch.Elapsed, retries, latest.AllDisconnected) &&
                pendingRequests.All(request => request.IsCompleted))
            {
                retries++;
                int attempt = retries + 1;
                Task<BluetoothMutationFailure> retry = StartReconnect(
                    deviceKey,
                    reconnectAdapter,
                    secondaryAdapters,
                    pendingRequests,
                    CancellationToken.None);
                pendingRequests.Add(retry);
                _ = retry.ContinueWith(
                    completed => ReportReconnectRequested(attempt, completed.Result, clock),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
            }

            await Task.Delay(ObservationInterval, CancellationToken.None).ConfigureAwait(false);
        }

        ReportReconnectObserved(latest.ConnectionState, retries, clock);
        return Failure(latest.ConnectionState, BluetoothMutationFailure.TimedOut, submitted: true);
    }

    internal static bool ShouldRetryReconnect(TimeSpan elapsed, int retriesUsed, bool allDisconnected) =>
        allDisconnected &&
        retriesUsed < ReconnectRetrySchedule.Length &&
        elapsed >= ReconnectRetrySchedule[retriesUsed];

    private Task<BluetoothMutationFailure> StartReconnect(
        BluetoothDeviceKey deviceKey,
        string reconnectAdapter,
        string[] secondaryAdapters,
        List<Task<BluetoothMutationFailure>> pendingRequests,
        CancellationToken cancellationToken)
    {
        Task<BluetoothMutationFailure> primary = RunMutationAsync(
            deviceKey,
            reconnectAdapter,
            BluetoothWorkerOperation.Reconnect,
            cancellationToken);
        foreach (string adapter in secondaryAdapters)
        {
            pendingRequests.Add(RunMutationAsync(
                deviceKey,
                adapter,
                BluetoothWorkerOperation.Reconnect,
                CancellationToken.None));
        }

        return primary;
    }

    private void ReportReconnectRequested(int attempt, BluetoothMutationFailure failure, Stopwatch clock) =>
        diagnostics?.Invoke(
            "BluetoothReconnectRequested",
            new Dictionary<string, object?>
            {
                ["Attempt"] = attempt,
                ["Failure"] = failure.ToString(),
                ["ElapsedMs"] = clock.ElapsedMilliseconds,
            });

    private void ReportReconnectObserved(BluetoothConnectionState state, int retries, Stopwatch clock) =>
        diagnostics?.Invoke(
            "BluetoothReconnectObserved",
            new Dictionary<string, object?>
            {
                ["ConnectionState"] = state.ToString(),
                ["Retries"] = retries,
                ["ElapsedMs"] = clock.ElapsedMilliseconds,
            });

    private async Task<BluetoothDeviceOperationResult> ObserveDisconnectedAsync(
        WindowsBluetoothDeviceBinding binding)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? stableSince = null;
        EndpointObservation latest = EndpointObservation.Unknown;
        while (stopwatch.Elapsed < OperationDeadline)
        {
            latest = await ObserveOnceAsync(binding).ConfigureAwait(false);
            if (latest.AllDisconnected)
            {
                stableSince ??= stopwatch.Elapsed;
                if (stopwatch.Elapsed - stableSince >= DisconnectedStableWindow)
                {
                    return new(
                        BluetoothConnectionState.Disconnected,
                        RequestSubmitted: true,
                        BluetoothMutationFailure.None);
                }
            }
            else
            {
                stableSince = null;
            }

            await Task.Delay(ObservationInterval, CancellationToken.None).ConfigureAwait(false);
        }

        return Failure(latest.ConnectionState, BluetoothMutationFailure.TimedOut, submitted: true);
    }

    private ValueTask<EndpointObservation> ObserveOnceAsync(WindowsBluetoothDeviceBinding binding) =>
        audioWorker.InvokeAsync(
            () => ReadEndpointStates(binding),
            CancellationToken.None);

    private static EndpointObservation ReadEndpointStates(WindowsBluetoothDeviceBinding binding)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            var states = new List<(BluetoothEndpointDirection Direction, BluetoothEndpointAvailability State)>();
            foreach (WindowsBluetoothEndpointBinding endpoint in binding.Endpoints)
            {
                IMMDevice? device = null;
                try
                {
                    int result = enumerator.GetDevice(endpoint.EndpointId, out device);
                    if (result < 0)
                    {
                        states.Add((
                            endpoint.Direction,
                            result == ElementNotFound
                                ? BluetoothEndpointAvailability.NotPresent
                                : BluetoothEndpointAvailability.Unknown));
                        continue;
                    }

                    HResult.ThrowIfFailed(device.GetState(out AudioDeviceState state), nameof(IMMDevice.GetState));
                    states.Add((endpoint.Direction, MapAvailability(state)));
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidCastException or CoreAudioInteropException)
                {
                    states.Add((endpoint.Direction, BluetoothEndpointAvailability.Unknown));
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }

            bool renderActive = states.Any(state =>
                state.Direction == BluetoothEndpointDirection.Render &&
                state.State == BluetoothEndpointAvailability.Active);
            bool allDisconnected = states.Count > 0 && states.All(state => state.State is
                BluetoothEndpointAvailability.NotPresent or BluetoothEndpointAvailability.Unplugged);
            BluetoothConnectionState connection = renderActive
                ? BluetoothConnectionState.Connected
                : allDisconnected
                    ? BluetoothConnectionState.Disconnected
                    : states.Count > 0 && states.All(state =>
                        state.State == BluetoothEndpointAvailability.Disabled)
                        ? BluetoothConnectionState.Unavailable
                        : BluetoothConnectionState.Unknown;
            return new(renderActive, allDisconnected, connection);
        }
        finally
        {
            ReleaseComObject(enumerator);
        }
    }

    private async Task<BluetoothMutationFailure> RunMutationAsync(
        BluetoothDeviceKey targetKey,
        string adapterDeviceId,
        BluetoothWorkerOperation operation,
        CancellationToken cancellationToken)
    {
        BluetoothWorkerRunResult result;
        try
        {
            result = await worker.RunAsync(
                targetKey,
                adapterDeviceId,
                operation,
                mutationConfirmed: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return BluetoothMutationFailure.Faulted;
        }

        return result.Status switch
        {
            BluetoothWorkerRunStatus.TimedOut => BluetoothMutationFailure.TimedOut,
            BluetoothWorkerRunStatus.ContainmentFailed => BluetoothMutationFailure.ContainmentFailed,
            BluetoothWorkerRunStatus.Faulted => BluetoothMutationFailure.Faulted,
            BluetoothWorkerRunStatus.Completed when result.Response?.HResult == 0 =>
                BluetoothMutationFailure.None,
            _ => BluetoothMutationFailure.Rejected,
        };
    }

    internal static string? ResolveReconnectAdapter(WindowsBluetoothDeviceBinding binding)
    {
        string[] candidates = [.. binding.Endpoints
            .Where(endpoint =>
                endpoint.Direction == BluetoothEndpointDirection.Render &&
                endpoint.Profile == BluetoothAudioProfile.Stereo)
            .SelectMany(endpoint => endpoint.AdapterDeviceIds)
            .Distinct(StringComparer.Ordinal)];
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static string[] ResolveSecondaryReconnectAdapters(
        WindowsBluetoothDeviceBinding binding,
        string reconnectAdapter) =>
        [.. ResolveDisconnectAdapters(binding)
            .Where(adapter => !string.Equals(adapter, reconnectAdapter, StringComparison.Ordinal))];

    private static string[] ResolveDisconnectAdapters(WindowsBluetoothDeviceBinding binding) =>
        [.. binding.Endpoints
            .SelectMany(endpoint => endpoint.AdapterDeviceIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static BluetoothDeviceOperationResult Failure(
        BluetoothConnectionState state,
        BluetoothMutationFailure failure,
        bool submitted = false) =>
        new(state, submitted, failure);

    private static BluetoothEndpointAvailability MapAvailability(AudioDeviceState state)
    {
        if ((state & AudioDeviceState.Active) != 0)
        {
            return BluetoothEndpointAvailability.Active;
        }

        if ((state & AudioDeviceState.Disabled) != 0)
        {
            return BluetoothEndpointAvailability.Disabled;
        }

        if ((state & AudioDeviceState.NotPresent) != 0)
        {
            return BluetoothEndpointAvailability.NotPresent;
        }

        return (state & AudioDeviceState.Unplugged) != 0
            ? BluetoothEndpointAvailability.Unplugged
            : BluetoothEndpointAvailability.Unknown;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private sealed record EndpointObservation(
        bool RenderActive,
        bool AllDisconnected,
        BluetoothConnectionState ConnectionState)
    {
        internal static EndpointObservation Unknown { get; } = new(
            false,
            false,
            BluetoothConnectionState.Unknown);
    }
}
