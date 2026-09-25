using BtSwitcher.Core;
using BtSwitcher.Windows;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BtSwitcher.Extension;

internal sealed partial class DevicesPage : DynamicListPage, IDisposable
{
    private readonly IAudioBackend backend;
    private readonly SwitchCoordinator coordinator;
    private readonly Timer timer;
    private readonly object sync = new();
    private AudioSnapshot snapshot = new([], null, null);
    private string status = "Loading devices…";
    private string? retryId;
    private int refreshing;
    private int operating;
    private volatile bool disposed;
    private DateTime lastViewed;

    public DevicesPage(IAudioBackend backend)
    {
        this.backend = backend;
        coordinator = new(backend);
        Id = "btswitcher.devices";
        Name = "Bluetooth Switcher";
        Title = "Bluetooth Switcher";
        Icon = ExtensionIcons.AudioSwitch;
        PlaceholderText = "Search paired Bluetooth audio devices...";
        EmptyContent = new CommandItem(new OpenUrlCommand("ms-settings:bluetooth") { Name = "Open" })
        { Title = "No matching Bluetooth audio devices", Subtitle = "Make sure Bluetooth is turned on and your device is paired, or try a different search." };
        timer = new Timer(_ =>
        {
            // Poll only shortly after the page is visited; no extra background service.
            if (!disposed && DateTime.UtcNow - lastViewed < TimeSpan.FromMinutes(2)) _ = RefreshAsync();
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override IListItem[] GetItems()
    {
        lastViewed = DateTime.UtcNow;
        _ = RefreshAsync();
        lock (sync)
        {
            var rows = new List<IListItem>();
            if (!string.IsNullOrEmpty(status))
            {
                var message = new ListItem(new NoOpCommand()) { Title = status, Subtitle = operating != 0 ? "Operation in progress. You can still search and view devices." : "Operation result", Section = "Status" };
                if (retryId is not null && operating == 0)
                    message.MoreCommands = [new CommandContextItem(new DeviceCommand(this, OperationKind.Disconnect, retryId, "Retry disconnecting the previous device"))];
                rows.Add(message);
            }
            var matching = snapshot.Devices.Where(d => d.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (matching.Length == 0 && !string.IsNullOrWhiteSpace(SearchText))
                rows.Add(new ListItem(new NoOpCommand()) { Title = "No matching devices", Subtitle = "Try a different name or clear the search.", Section = "Paired devices" });
            foreach (var d in matching)
            {
                var subtitle = d.IsDefault ? "Current default audio output" : d.Connected ? "Connected" : "Not connected";
                if (operating == 0 && !d.CanConnect && d.ReadyOutputId is null) subtitle += " · Connection unavailable";
                rows.Add(new ListItem(operating == 0 ? new DeviceCommand(this, OperationKind.Switch, d.Id, "Switch to this device") : new NoOpCommand())
                {
                    Title = snapshot.Devices.Count(other => other.Name.Equals(d.Name, StringComparison.CurrentCultureIgnoreCase)) > 1
                        ? $"{d.Name} · {d.Id[..8]}" : d.Name,
                    Subtitle = subtitle, Icon = new IconInfo("\uE7F6"), Section = "Paired devices",
                    MoreCommands = operating == 0 ?
                    [new CommandContextItem(new DeviceCommand(this, OperationKind.Connect, d.Id, "Connect only")),
                     new CommandContextItem(new DeviceCommand(this, OperationKind.Disconnect, d.Id, "Disconnect"))] : [],
                });
            }
            rows.Add(new ListItem(new RefreshCommand(this)) { Title = "Refresh device list", Icon = new IconInfo("\uE72C"), Section = "Tools" });
            rows.Add(new ListItem(new OpenUrlCommand("ms-settings:bluetooth") { Name = "Open" }) { Title = "Open Windows Bluetooth settings", Section = "Tools" });
            return rows.ToArray();
        }
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (disposed || Interlocked.Exchange(ref refreshing, 1) != 0) return;
        try
        {
            var current = await backend.SnapshotAsync(default);
            bool changed;
            lock (sync)
            {
                changed = !snapshot.Devices.SequenceEqual(current.Devices) || snapshot.ConsoleOutputId != current.ConsoleOutputId
                    || snapshot.MultimediaOutputId != current.MultimediaOutputId;
                snapshot = current;
                if (status == "Loading devices…" || force)
                {
                    if (operating == 0) { status = current.Devices.Count == 0 ? "No paired Bluetooth audio devices found. Check Windows Bluetooth settings." : ""; retryId = null; }
                    changed = true;
                }
            }
            if (changed && !disposed) RaiseItemsChanged();
        }
        catch (Exception ex)
        {
            lock (sync) { if (operating == 0) status = "Failed to load devices: " + ex.Message; }
            if (!disposed) RaiseItemsChanged();
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }

    internal ICommandResult Execute(OperationKind kind, string id)
    {
        if (Interlocked.CompareExchange(ref operating, 1, 0) != 0) return CommandResult.KeepOpen();
        try
        {
            using var lease = WindowsOperationLease.TryAcquire();
            if (lease is null)
            {
                SetStatus("Another instance is operating on a device. Try again later.");
                return CommandResult.KeepOpen();
            }
            IsLoading = true;
            SetStatus("Checking device status…");
            var result = coordinator.ExecuteAsync(kind, id, SetStatus).GetAwaiter().GetResult();
            // Successful operations dismiss the palette. Do not leave a stale result
            // as the first selectable row when the page is opened again.
            lock (sync) { status = result.Succeeded ? "" : result.Message; retryId = result.RetryDisconnectId; }
            return result.Succeeded ? CommandResult.Dismiss() : CommandResult.KeepOpen();
        }
        catch (Exception ex) { SetStatus("Operation failed: " + ex.Message); return CommandResult.KeepOpen(); }
        finally
        {
            Volatile.Write(ref operating, 0);
            IsLoading = false;
            RaiseItemsChanged();
            _ = RefreshAsync();
        }
    }

    private void SetStatus(string message)
    {
        lock (sync) { status = message; retryId = null; }
        RaiseItemsChanged();
    }

    public void Dispose() { disposed = true; timer.Dispose(); }

    private sealed partial class DeviceCommand : InvokableCommand
    {
        private readonly DevicesPage page;
        private readonly OperationKind kind;
        private readonly string deviceId;
        public DeviceCommand(DevicesPage page, OperationKind kind, string id, string name)
        { this.page = page; this.kind = kind; deviceId = id; Name = name; Id = $"btswitcher.{kind}.{id}"; }
        public override ICommandResult Invoke() => page.Execute(kind, deviceId);
    }

    private sealed partial class RefreshCommand(DevicesPage page) : InvokableCommand
    {
        public override string Name => "Refresh";
        public override ICommandResult Invoke() { _ = page.RefreshAsync(true); return CommandResult.KeepOpen(); }
    }
}
