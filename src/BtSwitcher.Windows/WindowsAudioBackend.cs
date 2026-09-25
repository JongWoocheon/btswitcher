using System.Runtime.InteropServices;
using BtSwitcher.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BtSwitcher.Windows;

public sealed record EndpointDiagnostic(string Id, string Name, Guid ContainerId, int Flow,
    uint State, string[] Filters, bool CanConnect, bool CanDisconnect, string? Error);
public sealed record PairedDiagnostic(string Name, string Id, Guid ContainerId, bool Connected, bool AudioClass);
public sealed record ProbeReport(bool Elevated, string? ConsoleOutput, string? MultimediaOutput,
    IReadOnlyList<PairedDiagnostic> PairedDevices, IReadOnlyList<EndpointDiagnostic> Endpoints);

public sealed class WindowsAudioBackend : IAudioBackend
{
    private readonly SemaphoreSlim nativeGate = new(1, 1);
    private IReadOnlyList<PairedDiagnostic> pairedCache = [];
    private DateTime pairedAt;

    public async Task<AudioSnapshot> SnapshotAsync(CancellationToken token)
    {
        var report = await ProbeAsync(token);
        var devices = new List<AudioDevice>();
        foreach (var pair in report.PairedDevices.GroupBy(p => p.ContainerId).Where(g => g.Key != Guid.Empty))
        {
            var endpoints = report.Endpoints.Where(e => e.ContainerId == pair.Key).ToArray();
            var outputs = endpoints.Where(e => e.Flow == 0).ToArray();
            if (outputs.Length == 0 && !pair.Any(p => p.AudioClass)) continue; // Bluetooth mice/keyboards must not appear.
            var active = outputs.Where(e => e.State == 1).ToArray();
            var output = active.FirstOrDefault(e => e.Id == report.MultimediaOutput)
                ?? active.FirstOrDefault(e => e.Id == report.ConsoleOutput) ?? active.FirstOrDefault();
            var connected = pair.Any(p => p.Connected) || endpoints.Any(e => e.State == 1);
            var controls = endpoints.Where(e => e.State == 1 || e.State == 8).ToArray();
            var canConnect = outputs.Any(e => e.CanConnect);
            // Every currently active audio filter must be controllable, including capture.
            var canDisconnect = controls.Length > 0 && controls.Where(e => e.State == 1).All(e => e.CanDisconnect)
                && controls.Any(e => e.CanDisconnect);
            devices.Add(new(pair.Key.ToString("D"), pair.First().Name, connected, output?.Id,
                outputs.Any(e => e.Id == report.MultimediaOutput), canConnect, canDisconnect,
                canConnect ? null : outputs.Length == 0 ? "No audio endpoint is available yet. Set up the audio device in Windows Bluetooth settings first." : "The current driver does not provide a usable connection interface. Make sure Bluetooth is turned on, or use Windows Bluetooth settings."));
        }
        return new(devices.OrderByDescending(d => d.IsDefault).ThenByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), report.ConsoleOutput, report.MultimediaOutput);
    }

    public async Task<ProbeReport> ProbeAsync(CancellationToken token = default)
    {
        await nativeGate.WaitAsync(token);
        try
        {
            if (DateTime.UtcNow - pairedAt > TimeSpan.FromSeconds(5))
            {
                var keys = new[] { "System.Devices.ContainerId", "System.Devices.Aep.ContainerId", "System.Devices.Aep.IsConnected", "System.Devices.Aep.Bluetooth.Cod.Major" };
                var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), keys).AsTask(token);
                var lowEnergy = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), keys).AsTask(token);
                pairedCache = classic.Concat(lowEnergy).Select(d => new PairedDiagnostic(d.Name, d.Id, ReadContainer(d),
                    d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connected) && connected is true,
                    d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var major) && Convert.ToInt32(major) == 4)).ToArray();
                pairedAt = DateTime.UtcNow;
            }
            var pairs = pairedCache;
            return await Task.Run(() => ReadReport(pairs, token), token);
        }
        finally { nativeGate.Release(); }
    }

    public Task ConnectAsync(string deviceId, CancellationToken token) => SendAsync(deviceId, 0, token);
    public Task DisconnectAsync(string deviceId, CancellationToken token) => SendAsync(deviceId, 1, token);

    private async Task SendAsync(string deviceId, uint propertyId, CancellationToken token)
    {
        if (!Guid.TryParse(deviceId, out var container)) throw new ArgumentException("Invalid device ID.");
        var report = await ProbeAsync(token);
        if (!report.PairedDevices.Any(p => p.ContainerId == container)) throw new InvalidOperationException("The device is no longer in the paired devices list.");
        var endpoints = report.Endpoints.Where(e => e.ContainerId == container)
            .Where(e => propertyId == 0 ? e.Flow == 0 && e.CanConnect : e.CanDisconnect).ToArray();
        var filters = endpoints.SelectMany(e => e.Filters).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (filters.Length == 0) throw new NotSupportedException("The device driver does not support this operation. Use Windows Bluetooth settings.");
        await nativeGate.WaitAsync(token);
        try
        {
            await Task.Run(() =>
            {
                var enumerator = AudioNative.Create<IMMDeviceEnumerator>(AudioNative.EnumeratorClass);
                try
                {
                    var errors = new List<string>();
                    var attempts = 0;
                    foreach (var id in filters)
                    {
                        token.ThrowIfCancellationRequested();
                        IMMDevice? device = null;
                        object? control = null;
                        try
                        {
                            AudioNative.Check(enumerator.GetDevice(id, out device));
                            var iid = typeof(IKsControl).GUID;
                            AudioNative.Check(device.Activate(ref iid, 23, IntPtr.Zero, out control));
                            var ks = (IKsControl)control;
                            if (!Supports(ks, propertyId)) continue;
                            // ONESHOT uses GET but changes the connection. Never call in ProbeAsync.
                            var request = new KsProperty { Set = AudioNative.BtAudio, Id = propertyId, Flags = 1 };
                            var hr = ks.KsProperty(ref request, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                            if (hr < 0) errors.Add($"0x{hr:X8}"); else attempts++;
                        }
                        finally { AudioNative.Release(control); AudioNative.Release(device); }
                    }
                    if (attempts == 0) throw new InvalidOperationException("The driver did not accept the request: " + string.Join(", ", errors));
                    // Partial driver acceptance is evaluated by the coordinator's actual state poll.
                }
                finally { AudioNative.Release(enumerator); }
            }, token);
        }
        finally { nativeGate.Release(); }
    }

    public Task SetDefaultAsync(string endpointId, CancellationToken token) =>
        RestoreDefaultsAsync(endpointId, endpointId, token);

    public async Task RestoreDefaultsAsync(string? consoleId, string? multimediaId, CancellationToken token)
    {
        await nativeGate.WaitAsync(token);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var policy = AudioNative.Create<IPolicyConfig>(AudioNative.PolicyClass);
                try
                {
                    if (consoleId is not null) AudioNative.Check(policy.SetDefaultEndpoint(consoleId, 0));
                    if (multimediaId is not null) AudioNative.Check(policy.SetDefaultEndpoint(multimediaId, 1));
                }
                finally { AudioNative.Release(policy); }
            }, token);
        }
        finally { nativeGate.Release(); }
    }

    private static Guid ReadContainer(DeviceInformation info)
    {
        foreach (var key in new[] { "System.Devices.ContainerId", "System.Devices.Aep.ContainerId" })
            if (info.Properties.TryGetValue(key, out var value) && Guid.TryParse(value?.ToString(), out var id) && id != Guid.Empty)
                return id;
        return Guid.Empty;
    }

    private static ProbeReport ReadReport(IReadOnlyList<PairedDiagnostic> pairs, CancellationToken token)
    {
        var enumerator = AudioNative.Create<IMMDeviceEnumerator>(AudioNative.EnumeratorClass);
        IMMDeviceCollection? collection = null;
        try
        {
            var endpoints = new List<EndpointDiagnostic>();
            AudioNative.Check(enumerator.EnumAudioEndpoints(2, 15, out collection));
            AudioNative.Check(collection.GetCount(out var count));
            for (uint index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                IMMDevice? device = null;
                IPropertyStore? store = null;
                try
                {
                    AudioNative.Check(collection.Item(index, out device));
                    AudioNative.Check(device.GetId(out var id));
                    AudioNative.Check(device.GetState(out var state));
                    AudioNative.Check(((IMMEndpoint)device).GetDataFlow(out var flow));
                    AudioNative.Check(device.OpenPropertyStore(0, out store));
                    var name = Property(store, AudioNative.FriendlyName)?.ToString() ?? id;
                    var container = Property(store, AudioNative.ContainerId) as Guid? ?? Guid.Empty;
                    var filters = new List<string>();
                    bool connect = false, disconnect = false;
                    string? error = null;
                    // Do not touch KS on non-Bluetooth devices, even for capability queries.
                    if (pairs.Any(p => p.ContainerId == container && container != Guid.Empty))
                    {
                        try
                        {
                            filters = Filters(device);
                            foreach (var filter in filters)
                            {
                                IMMDevice? adapter = null;
                                object? control = null;
                                try
                                {
                                    AudioNative.Check(enumerator.GetDevice(filter, out adapter));
                                    var iid = typeof(IKsControl).GUID;
                                    AudioNative.Check(adapter.Activate(ref iid, 23, IntPtr.Zero, out control));
                                    connect |= Supports((IKsControl)control, 0);
                                    disconnect |= Supports((IKsControl)control, 1);
                                }
                                finally { AudioNative.Release(control); AudioNative.Release(adapter); }
                            }
                        }
                        catch (Exception ex) { error = $"{ex.Message} (0x{ex.HResult:X8})"; }
                    }
                    endpoints.Add(new(id, name, container, flow, state, filters.ToArray(), connect, disconnect, error));
                }
                finally { AudioNative.Release(store); AudioNative.Release(device); }
            }
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            return new(elevated, Default(enumerator, 0), Default(enumerator, 1), pairs, endpoints);
        }
        finally { AudioNative.Release(collection); AudioNative.Release(enumerator); }
    }

    private static object? Property(IPropertyStore store, PropertyKey key)
    {
        var hr = store.GetValue(ref key, out var value);
        try
        {
            if (hr < 0) return null;
            return value.Type == 72 ? value.GuidValue : value.StringValue;
        }
        finally { AudioNative.PropVariantClear(ref value); }
    }

    private static string? Default(IMMDeviceEnumerator enumerator, int role)
    {
        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0, role, out device) < 0) return null;
            AudioNative.Check(device.GetId(out var id));
            return id;
        }
        finally { AudioNative.Release(device); }
    }

    private static List<string> Filters(IMMDevice device)
    {
        object? topologyObject = null;
        try
        {
            var iid = typeof(IDeviceTopology).GUID;
            AudioNative.Check(device.Activate(ref iid, 23, IntPtr.Zero, out topologyObject));
            var topology = (IDeviceTopology)topologyObject;
            AudioNative.Check(topology.GetConnectorCount(out var count));
            var result = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                IConnector? connector = null;
                try
                {
                    AudioNative.Check(topology.GetConnector(i, out connector));
                    if (connector.GetDeviceIdConnectedTo(out var id) >= 0 && !string.IsNullOrEmpty(id)) result.Add(id);
                }
                finally { AudioNative.Release(connector); }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { AudioNative.Release(topologyObject); }
    }

    private static bool Supports(IKsControl control, uint id)
    {
        var request = new KsProperty { Set = AudioNative.BtAudio, Id = id, Flags = 0x200 }; // BASICSUPPORT: read-only
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, 0);
            var hr = control.KsProperty(ref request, (uint)Marshal.SizeOf<KsProperty>(), buffer, 4, out var returned);
            return hr >= 0 && returned >= 4 && (Marshal.ReadInt32(buffer) & 1) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
