using System.Collections.Immutable;
using QuickPods.Core.Models;
using QuickPods.Windows.Bluetooth;
using Xunit;

namespace QuickPods.Foundation.Tests;

public sealed class BluetoothReconnectPolicyTests
{
    private static readonly BluetoothDeviceKey Target = new($"bt-{new string('A', 64)}");

    [Fact]
    public void HeadsetReconnectsStereoFirstAndHandsFreeAsSecondary()
    {
        WindowsBluetoothDeviceBinding binding = Binding(
            Endpoint(BluetoothEndpointDirection.Render, BluetoothAudioProfile.Stereo, "a2dp"),
            Endpoint(BluetoothEndpointDirection.Render, BluetoothAudioProfile.HandsFree, "hfp"),
            Endpoint(BluetoothEndpointDirection.Capture, BluetoothAudioProfile.HandsFree, "hfp"));

        string? primary = WindowsBluetoothDeviceOperationPort.ResolveReconnectAdapter(binding);

        Assert.Equal("a2dp", primary);
        Assert.Equal(
            ["hfp"],
            WindowsBluetoothDeviceOperationPort.ResolveSecondaryReconnectAdapters(binding, primary!));
    }

    [Fact]
    public void StereoOnlyDeviceHasNoSecondaryReconnect()
    {
        WindowsBluetoothDeviceBinding binding = Binding(
            Endpoint(BluetoothEndpointDirection.Render, BluetoothAudioProfile.Stereo, "a2dp"));

        Assert.Empty(WindowsBluetoothDeviceOperationPort.ResolveSecondaryReconnectAdapters(
            binding,
            "a2dp"));
    }

    [Theory]
    [InlineData(3.9, 0, false)]
    [InlineData(4.0, 0, true)]
    [InlineData(7.9, 1, false)]
    [InlineData(8.0, 1, true)]
    [InlineData(14.0, 2, false)]
    public void RetriesFollowTheBoundedSchedule(double seconds, int retriesUsed, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsBluetoothDeviceOperationPort.ShouldRetryReconnect(
                TimeSpan.FromSeconds(seconds),
                retriesUsed,
                allDisconnected: true));
    }

    [Fact]
    public void RetryIsSkippedOnceAnyEndpointHasStartedToComeUp()
    {
        Assert.False(WindowsBluetoothDeviceOperationPort.ShouldRetryReconnect(
            TimeSpan.FromSeconds(10),
            retriesUsed: 0,
            allDisconnected: false));
    }

    private static WindowsBluetoothDeviceBinding Binding(
        params WindowsBluetoothEndpointBinding[] endpoints) =>
        new(Target, Guid.NewGuid(), [.. endpoints], HasAmbiguousAdapterOwnership: false);

    private static WindowsBluetoothEndpointBinding Endpoint(
        BluetoothEndpointDirection direction,
        BluetoothAudioProfile profile,
        string adapter) =>
        new(
            $"endpoint-{direction}-{profile}",
            direction,
            profile,
            BluetoothEndpointAvailability.NotPresent,
            ImmutableArray.Create(adapter));
}
