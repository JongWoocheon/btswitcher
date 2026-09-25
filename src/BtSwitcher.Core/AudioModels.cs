namespace BtSwitcher.Core;

public sealed record AudioDevice(string Id, string Name, bool Connected, string? ReadyOutputId,
    bool IsDefault, bool CanConnect, bool CanDisconnect, string? UnavailableReason = null);

public sealed record AudioSnapshot(IReadOnlyList<AudioDevice> Devices, string? ConsoleOutputId,
    string? MultimediaOutputId)
{
    public AudioDevice? Find(string id) => Devices.FirstOrDefault(d => d.Id == id);
    public AudioDevice? DefaultBluetooth => Devices.FirstOrDefault(d => d.IsDefault);
}

public interface IAudioBackend
{
    Task<AudioSnapshot> SnapshotAsync(CancellationToken token);
    Task ConnectAsync(string deviceId, CancellationToken token);
    Task DisconnectAsync(string deviceId, CancellationToken token);
    Task SetDefaultAsync(string endpointId, CancellationToken token);
    Task RestoreDefaultsAsync(string? consoleId, string? multimediaId, CancellationToken token);
}

public enum OperationKind { Switch, Connect, Disconnect }
public enum ResultKind { Success, Partial, Failed, Busy }
public sealed record OperationResult(ResultKind Kind, string Message, string? RetryDisconnectId = null)
{
    public bool Succeeded => Kind == ResultKind.Success;
}
public sealed record SwitchOptions(TimeSpan ConnectTimeout, TimeSpan DisconnectTimeout, TimeSpan PollInterval)
{
    public static SwitchOptions Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(350));
}
