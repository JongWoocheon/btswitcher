using BtSwitcher.Core;
using Xunit;

namespace BtSwitcher.Tests;

public class SwitchCoordinatorTests
{
    private static readonly SwitchOptions Fast = new(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(2));
    private static SwitchCoordinator Coordinator(FakeBackend b) => new(b, Fast);

    [Fact]
    public async Task SwitchConnectsThenRoutesThenDisconnectsOnlyOldDefault()
    {
        var b = new FakeBackend();
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal(new[] { "connect:B", "default:B-out", "disconnect:A" }, b.Calls);
        Assert.True(b.Devices["C"].Connected);
        Assert.Equal("B-out", b.ConsoleId);
        Assert.Equal("B-out", b.MultimediaId);
        Assert.Equal("call-device", b.CommunicationsId);
    }

    [Fact]
    public async Task CurrentOutputIsIdempotent()
    {
        var b = new FakeBackend();
        Assert.True((await Coordinator(b).ExecuteAsync(OperationKind.Switch, "A")).Succeeded);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task AlreadyConnectedTargetSkipsConnection()
    {
        var b = new FakeBackend(); b.SetConnected("B", true);
        await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(new[] { "default:B-out", "disconnect:A" }, b.Calls);
    }

    [Fact]
    public async Task NonBluetoothDefaultDoesNotDisconnectOtherConnectedBluetoothDevices()
    {
        var b = new FakeBackend { ConsoleId = "speakers", MultimediaId = "speakers" };
        await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.True(b.Devices["A"].Connected);
        Assert.True(b.Devices["C"].Connected);
        Assert.DoesNotContain(b.Calls, c => c.StartsWith("disconnect:"));
    }

    [Fact]
    public async Task OldDisconnectFailureKeepsWorkingTargetAndOffersRetry()
    {
        var b = new FakeBackend { StuckDisconnect = "A" };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Partial, result.Kind);
        Assert.Equal("A", result.RetryDisconnectId);
        Assert.Equal("B-out", b.MultimediaId);
        Assert.True(b.Devices["B"].Connected);
        Assert.DoesNotContain(b.Calls, c => c.StartsWith("restore:"));
    }

    [Fact]
    public async Task UnsupportedTargetNeverDisturbsOldDevice()
    {
        var b = new FakeBackend(); b.Devices["B"] = b.Devices["B"] with { CanConnect = false };
        Assert.Equal(ResultKind.Failed, (await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B")).Kind);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task ConnectErrorDoesNotUseDestructiveFallback()
    {
        var b = new FakeBackend { ConnectError = "B" };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.DoesNotContain("disconnect:A", b.Calls);
        Assert.Equal("A-out", b.MultimediaId);
    }

    [Fact]
    public async Task ExclusiveHardwareCanDisconnectOldAndRetry()
    {
        var b = new FakeBackend { RequireExclusive = true };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "connect:B", "disconnect:A", "connect:B", "default:B-out" }, b.Calls);
        Assert.True(b.Devices["C"].Connected);
    }

    [Fact]
    public async Task OfflineTargetRestoresOldAfterFallback()
    {
        var b = new FakeBackend { Offline = "B" };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.True(b.Devices["A"].Connected);
        Assert.Equal("A-out", b.MultimediaId);
        Assert.Contains("connect:A", b.Calls);
        Assert.Contains("Previous state restored and verified", result.Message);
    }

    [Fact]
    public async Task PartialDefaultWriteRestoresBothOriginalRolesAndNewLink()
    {
        var b = new FakeBackend { ConsoleId = "console-speaker", FailDefaultAfterConsole = true };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.Equal("console-speaker", b.ConsoleId);
        Assert.Equal("A-out", b.MultimediaId);
        Assert.False(b.Devices["B"].Connected);
        Assert.True(b.Devices["A"].Connected);
    }

    [Fact]
    public async Task RollbackPreservesTargetThatWasAlreadyConnected()
    {
        var b = new FakeBackend { FailDefaultAfterConsole = true }; b.SetConnected("B", true);
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.True(b.Devices["B"].Connected);
        Assert.DoesNotContain("disconnect:B", b.Calls);
    }

    [Fact]
    public async Task SuccessfulRequestWithoutStateChangeIsNotSuccess()
    {
        var b = new FakeBackend { IgnoreDefaultWrite = true };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.Equal("A-out", b.MultimediaId);
        Assert.DoesNotContain("disconnect:A", b.Calls);
    }

    [Fact]
    public async Task RecoveryFailureIsVisible()
    {
        var b = new FakeBackend { Offline = "B", FailRestore = true };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.Contains("Recovery was incomplete", result.Message);
    }

    [Fact]
    public async Task DefaultWriteFailureAfterNewConnectionCleansUpBeforeRestoringOld()
    {
        var b = new FakeBackend { RequireExclusive = true, FailDefaultAfterConsole = true };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.True(b.Calls.IndexOf("disconnect:B") < b.Calls.IndexOf("connect:A"));
        Assert.Equal("A-out", b.ConsoleId);
        Assert.False(b.Devices["B"].Connected);
    }

    [Fact]
    public async Task ConnectionAloneDoesNotActivelyChangeDefaultOrDisconnectOld()
    {
        var b = new FakeBackend();
        Assert.True((await Coordinator(b).ExecuteAsync(OperationKind.Connect, "B")).Succeeded);
        Assert.Equal(new[] { "connect:B" }, b.Calls);
        Assert.Equal("A-out", b.MultimediaId);
    }

    [Fact]
    public async Task ExplicitDisconnectOnlyTouchesSelectedDevice()
    {
        var b = new FakeBackend();
        Assert.True((await Coordinator(b).ExecuteAsync(OperationKind.Disconnect, "C")).Succeeded);
        Assert.Equal(new[] { "disconnect:C" }, b.Calls);
        Assert.True(b.Devices["A"].Connected);
    }

    [Fact]
    public async Task MissingDeviceFailsWithoutMutation()
    {
        var b = new FakeBackend();
        Assert.Equal(ResultKind.Failed, (await Coordinator(b).ExecuteAsync(OperationKind.Switch, "missing")).Kind);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task ConcurrentModificationIsRejected()
    {
        var b = new FakeBackend { ConnectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var c = Coordinator(b);
        var first = c.ExecuteAsync(OperationKind.Switch, "B");
        await b.ConnectEntered.Task;
        Assert.Equal(ResultKind.Busy, (await c.ExecuteAsync(OperationKind.Disconnect, "C")).Kind);
        b.ConnectionGate.SetResult();
        Assert.True((await first).Succeeded);
        Assert.True(b.Devices["C"].Connected);
    }

    [Fact]
    public async Task SameDefaultDeviceWithSplitRolesStillAlignsBothRoles()
    {
        var b = new FakeBackend { ConsoleId = "speakers" };
        Assert.True((await Coordinator(b).ExecuteAsync(OperationKind.Switch, "A")).Succeeded);
        Assert.Equal(new[] { "default:A-out" }, b.Calls);
        Assert.Equal("A-out", b.ConsoleId);
    }

    [Fact]
    public async Task ProgressDistinguishesConnectionRoutingAndCleanup()
    {
        var b = new FakeBackend(); var messages = new List<string>();
        await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B", messages.Add);
        Assert.Contains(messages, m => m.Contains("Connecting to"));
        Assert.Contains(messages, m => m.Contains("audio output"));
        Assert.Contains(messages, m => m.Contains("Disconnecting the previous device"));
    }

    [Fact]
    public async Task TargetDisappearingDuringCleanupCannotBeReportedAsSuccess()
    {
        var b = new FakeBackend { DropTargetDuringOldDisconnect = true };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.True(b.Devices["A"].Connected);
        Assert.Equal("A-out", b.MultimediaId);
        Assert.Contains("Final state verification failed", result.Message);
    }

    [Fact]
    public async Task PartialCompletionRequiresTargetStillBeUsable()
    {
        var b = new FakeBackend { DropTargetDuringOldDisconnect = true, StuckDisconnect = "A" };
        var result = await Coordinator(b).ExecuteAsync(OperationKind.Switch, "B");
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.Null(result.RetryDisconnectId);
        Assert.Equal("A-out", b.MultimediaId);
    }

    [Fact]
    public async Task CancelDuringConnectionRecoversUsingIndependentTokenAndReleasesGate()
    {
        var b = new FakeBackend { ConnectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var c = Coordinator(b);
        using var cancellation = new CancellationTokenSource();
        var operation = c.ExecuteAsync(OperationKind.Switch, "B", token: cancellation.Token);
        await b.ConnectEntered.Task;
        cancellation.Cancel();
        var result = await operation;
        Assert.Equal(ResultKind.Failed, result.Kind);
        Assert.Equal("A-out", b.MultimediaId);
        Assert.False(c.IsBusy);
        Assert.True((await c.ExecuteAsync(OperationKind.Disconnect, "C")).Succeeded);
    }
}

internal sealed class FakeBackend : IAudioBackend
{
    public Dictionary<string, AudioDevice> Devices { get; } = new()
    {
        ["A"] = new("A", "Earphones A", true, "A-out", true, true, true),
        ["B"] = new("B", "Earphones B", false, null, false, true, true),
        ["C"] = new("C", "Speaker C", true, "C-out", false, true, true),
    };
    public string? ConsoleId = "A-out", MultimediaId = "A-out";
    public string CommunicationsId = "call-device";
    public List<string> Calls { get; } = [];
    public bool RequireExclusive, FailDefaultAfterConsole, IgnoreDefaultWrite, FailRestore, DropTargetDuringOldDisconnect;
    public string? Offline, ConnectError, StuckDisconnect;
    public TaskCompletionSource? ConnectionGate;
    public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetConnected(string id, bool connected) => Devices[id] = Devices[id] with
    { Connected = connected, ReadyOutputId = connected ? id + "-out" : null };
    public Task<AudioSnapshot> SnapshotAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new AudioSnapshot(Devices.Values.Select(d => d with { IsDefault = d.ReadyOutputId is not null && d.ReadyOutputId == MultimediaId }).ToArray(), ConsoleId, MultimediaId));
    }
    public async Task ConnectAsync(string id, CancellationToken token)
    {
        Calls.Add("connect:" + id); ConnectEntered.TrySetResult();
        if (ConnectionGate is not null) await ConnectionGate.Task.WaitAsync(token);
        if (ConnectError == id) throw new NotSupportedException("driver error");
        if (Offline == id || (RequireExclusive && id == "B" && Devices["A"].Connected)) return;
        SetConnected(id, true);
    }
    public Task DisconnectAsync(string id, CancellationToken token)
    {
        Calls.Add("disconnect:" + id);
        if (StuckDisconnect != id)
        {
            SetConnected(id, false);
            if (ConsoleId == id + "-out") ConsoleId = "speakers";
            if (MultimediaId == id + "-out") MultimediaId = "speakers";
        }
        if (id == "A" && DropTargetDuringOldDisconnect)
        {
            SetConnected("B", false); ConsoleId = "speakers"; MultimediaId = "speakers";
        }
        return Task.CompletedTask;
    }
    public Task SetDefaultAsync(string endpointId, CancellationToken token)
    {
        Calls.Add("default:" + endpointId);
        if (IgnoreDefaultWrite) return Task.CompletedTask;
        ConsoleId = endpointId;
        if (FailDefaultAfterConsole) throw new InvalidOperationException("second role failed");
        MultimediaId = endpointId;
        return Task.CompletedTask;
    }
    public Task RestoreDefaultsAsync(string? consoleId, string? multimediaId, CancellationToken token)
    {
        Calls.Add($"restore:{consoleId}/{multimediaId}");
        if (FailRestore) throw new InvalidOperationException("restore failed");
        ConsoleId = consoleId; MultimediaId = multimediaId;
        return Task.CompletedTask;
    }
}
