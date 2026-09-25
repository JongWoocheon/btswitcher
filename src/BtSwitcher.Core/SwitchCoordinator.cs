namespace BtSwitcher.Core;

/// <summary>Owns the transaction. A successful native request is never treated as observed success.</summary>
public sealed class SwitchCoordinator(IAudioBackend backend, SwitchOptions? options = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SwitchOptions timing = options ?? SwitchOptions.Default;
    public bool IsBusy => gate.CurrentCount == 0;

    public async Task<OperationResult> ExecuteAsync(OperationKind kind, string targetId,
        Action<string>? progress = null, CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token))
            return new(ResultKind.Busy, "Another operation is in progress. Please wait.");
        try
        {
            var before = await backend.SnapshotAsync(token);
            var target = before.Find(targetId) ?? throw new InvalidOperationException("The device is no longer available. Refresh the device list.");
            if (kind == OperationKind.Switch)
                return await SwitchAsync(before, target, progress, token);
            if (kind == OperationKind.Connect)
            {
                if (target.ReadyOutputId is not null) return new(ResultKind.Success, "The device is already connected.");
                if (!target.CanConnect) throw new NotSupportedException(target.UnavailableReason ?? "The driver does not support connecting this device.");
                progress?.Invoke($"Connecting to {target.Name}…");
                await ConnectReadyAsync(targetId, token);
                return new(ResultKind.Success, "Connected. The app did not change the default audio output.");
            }
            if (!target.Connected) return new(ResultKind.Success, "The device is already disconnected.");
            if (!target.CanDisconnect) throw new NotSupportedException("The driver does not support fully disconnecting this device.");
            progress?.Invoke($"Disconnecting {target.Name}…");
            await DisconnectVerifiedAsync(targetId, token);
            return new(ResultKind.Success, "Device disconnected.");
        }
        catch (Exception ex)
        {
            return new(ResultKind.Failed, $"Operation failed: {Describe(ex)}");
        }
        finally { gate.Release(); }
    }

    private async Task<OperationResult> SwitchAsync(AudioSnapshot before, AudioDevice target,
        Action<string>? progress, CancellationToken token)
    {
        if (target.ReadyOutputId is { } current && before.ConsoleOutputId == current && before.MultimediaOutputId == current)
            return new(ResultKind.Success, "This device is already the default audio output.");
        if (target.ReadyOutputId is null && !target.CanConnect)
            return new(ResultKind.Failed, target.UnavailableReason ?? "The driver does not provide a usable connection interface for this device.");

        var old = before.DefaultBluetooth;
        if (old?.Id == target.Id) old = null;
        bool targetTouched = false, oldTouched = false, outputTouched = false;
        try
        {
            var ready = target;
            if (ready.ReadyOutputId is null)
            {
                progress?.Invoke($"Connecting to {target.Name}…");
                targetTouched = true;
                try { ready = await ConnectReadyAsync(target.Id, token); }
                catch (TimeoutException) when (old is { Connected: true, CanDisconnect: true })
                {
                    // Do not disconnect the old device for unsupported operations or arbitrary errors.
                    // Recheck first: a late completion may already have made the target ready.
                    ready = (await backend.SnapshotAsync(token)).Find(target.Id) ?? target;
                    if (ready.ReadyOutputId is null)
                    {
                        progress?.Invoke($"Connection is still pending. Disconnecting {old.Name} before trying again…");
                        oldTouched = true;
                        await DisconnectVerifiedAsync(old.Id, token);
                        ready = await ConnectReadyAsync(target.Id, token);
                    }
                }
            }
            var endpoint = ready.ReadyOutputId ?? throw new InvalidOperationException("The device does not have an available playback endpoint yet.");
            progress?.Invoke($"Switching audio output to {target.Name}…");
            outputTouched = true; // SetDefault may partially succeed before throwing.
            await backend.SetDefaultAsync(endpoint, token);
            await WaitAsync(s => s.ConsoleOutputId == endpoint && s.MultimediaOutputId == endpoint,
                timing.DisconnectTimeout, "The default audio output did not finish switching.", token);
        }
        catch (Exception ex)
        {
            progress?.Invoke("Switch failed. Restoring the previous state…");
            var recovery = await RecoverAsync(before, target, old, targetTouched, oldTouched, outputTouched);
            return new(ResultKind.Failed, $"Switch failed: {Describe(ex)} {recovery}");
        }

        // B is verified. Failure to clean up A must never roll back working playback on B.
        OperationResult? cleanupFailure = null;
        if (old is not null)
        {
            try
            {
                var actual = (await backend.SnapshotAsync(token)).Find(old.Id);
                if (actual is { Connected: true })
                {
                    progress?.Invoke($"Disconnecting the previous device, {old.Name}…");
                    if (!actual.CanDisconnect) throw new NotSupportedException("The driver does not support fully disconnecting this device.");
                    await DisconnectVerifiedAsync(old.Id, token);
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = new(ResultKind.Partial,
                    $"Switched to {target.Name}, but could not confirm that {old.Name} disconnected: {Describe(ex)}", old.Id);
            }
        }
        // The target may disappear while the old link is being disconnected.
        try
        {
            var final = await backend.SnapshotAsync(token);
            var readyId = final.Find(target.Id)?.ReadyOutputId;
            if (readyId is null || final.ConsoleOutputId != readyId || final.MultimediaOutputId != readyId)
            {
                progress?.Invoke("The target output has changed. Restoring the previous state…");
                var recovery = await RecoverAsync(before, target, old, targetTouched, old is not null, true);
                return new(ResultKind.Failed, "Final state verification failed. The target is no longer an available default audio output. " + recovery);
            }
        }
        catch (Exception ex)
        {
            progress?.Invoke("Unable to verify the final audio output. Attempting to restore the previous state…");
            var recovery = await RecoverAsync(before, target, old, targetTouched, old is not null, true);
            return new(ResultKind.Failed, "Unable to verify the final audio output state: " + Describe(ex) + " " + recovery);
        }
        return cleanupFailure ?? new(ResultKind.Success, $"Switched to {target.Name}.");
    }

    private async Task<string> RecoverAsync(AudioSnapshot before, AudioDevice target, AudioDevice? old,
        bool targetTouched, bool oldTouched, bool outputTouched)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var errors = new List<string>();
        // Free the link before reconnecting A on hardware that cannot keep both connected.
        if (targetTouched && !target.Connected)
        {
            try { await DisconnectVerifiedAsync(target.Id, token); }
            catch (Exception ex) { errors.Add($"Failed to clean up the target connection ({Describe(ex)})"); }
        }
        if (old is { Connected: true })
        {
            try
            {
                var now = (await backend.SnapshotAsync(token)).Find(old.Id);
                if (oldTouched || now?.ReadyOutputId is null)
                    await ConnectReadyAsync(old.Id, token);
            }
            catch (Exception ex) { errors.Add($"Failed to reconnect the previous device ({Describe(ex)})"); }
        }
        // Even connecting a device may cause Windows to change defaults automatically.
        if (outputTouched || targetTouched || oldTouched)
        {
            try
            {
                await backend.RestoreDefaultsAsync(before.ConsoleOutputId, before.MultimediaOutputId, token);
                await WaitAsync(s => s.ConsoleOutputId == before.ConsoleOutputId && s.MultimediaOutputId == before.MultimediaOutputId,
                    timing.DisconnectTimeout, "The previous audio output was not restored.", token);
            }
            catch (Exception ex) { errors.Add($"Failed to restore the default audio output ({Describe(ex)})"); }
        }
        return errors.Count == 0 ? "Previous state restored and verified." : "Recovery was incomplete: " + string.Join("; ", errors);
    }

    private async Task<AudioDevice> ConnectReadyAsync(string id, CancellationToken token)
    {
        await backend.ConnectAsync(id, token);
        var state = await WaitAsync(s => s.Find(id)?.ReadyOutputId is not null,
            timing.ConnectTimeout, "Timed out waiting for the device to connect. Make sure it is powered on and not in use by another device.", token);
        return state.Find(id)!;
    }

    private async Task DisconnectVerifiedAsync(string id, CancellationToken token)
    {
        var state = await backend.SnapshotAsync(token);
        if (state.Find(id) is not { Connected: true }) return;
        await backend.DisconnectAsync(id, token);
        await WaitAsync(s => s.Find(id) is not { Connected: true }, timing.DisconnectTimeout,
            "Timed out waiting for the device to disconnect.", token);
    }

    private async Task<AudioSnapshot> WaitAsync(Func<AudioSnapshot, bool> predicate, TimeSpan timeout,
        string error, CancellationToken token)
    {
        var end = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            var state = await backend.SnapshotAsync(token);
            if (predicate(state)) return state;
            if (end.Elapsed >= timeout) break;
            await Task.Delay(timing.PollInterval, token);
        } while (true);
        throw new TimeoutException(error);
    }

    private static string Describe(Exception ex) => ex is OperationCanceledException ? "The operation was canceled or recovery timed out." : ex.Message;
}
