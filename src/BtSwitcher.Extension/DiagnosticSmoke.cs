using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using BtSwitcher.Windows;
using WinRT;

namespace BtSwitcher.Extension;

/// <summary>Read-only SDK/WinRT surface smoke test. Does not invoke any device command.</summary>
internal static class DiagnosticSmoke
{
    internal static async Task RunAsync(string output)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            // SDK command defaults must not introduce localized labels into this English-only extension.
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            using (var lease = WindowsOperationLease.TryAcquire())
            {
                if (lease is null) throw new InvalidOperationException("A device transaction is currently active; retry the smoke test when idle.");
                using var second = WindowsOperationLease.TryAcquire();
                if (second is not null) throw new InvalidOperationException("Named operation lock allowed concurrent owners.");
            }
            using var done = new ManualResetEvent(false);
            using var extension = new BtSwitcherExtension(done);
            // Exercise the generated WinRT marshaler, not only managed class construction.
            var pointer = MarshalInspectable<IExtension>.FromManaged(extension);
            try
            {
                var projected = MarshalInspectable<IExtension>.FromAbi(pointer);
                var provider = (ICommandProvider)projected.GetProvider(ProviderType.Commands);
                var top = provider.TopLevelCommands();
                if (top.Length != 1 || top[0].Title != "Bluetooth Switcher") throw new InvalidOperationException("Missing Bluetooth devices entry.");
                var page = (IListPage)top[0].Command;
                if (top[0].Command.Name != "Bluetooth Switcher" || page.PlaceholderText != "Search paired Bluetooth audio devices..."
                    || page.EmptyContent.Title != "No matching Bluetooth audio devices" || page.EmptyContent.Command.Name != "Open")
                    throw new InvalidOperationException("Page labels must remain English under a non-English UI culture.");
                _ = page.GetItems();
                IListItem[] items = [];
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    await Task.Delay(250);
                    items = page.GetItems();
                    if (!items.Any(i => i.Title == "Loading devices…")) break;
                }
                if (items.Any(i => i.Title.StartsWith("Failed to load devices") || i.Title == "Loading devices…"))
                    throw new InvalidOperationException("Device page did not complete its read-only load.");
                var deviceRows = items.Where(i => i.Section == "Paired devices").ToArray();
                if (deviceRows.Any(i => i.MoreCommands.Length != 2)) throw new InvalidOperationException("Missing connect/disconnect menu.");
                if (deviceRows.Any(i => i.Command.Name != "Switch to this device"
                    || !i.MoreCommands.Cast<ICommandItem>().Select(c => c.Command.Name).SequenceEqual(["Connect only", "Disconnect"])))
                    throw new InvalidOperationException("Device actions must remain English under a non-English UI culture.");
                var settings = items.Single(i => i.Title == "Open Windows Bluetooth settings");
                var refresh = items.Single(i => i.Title == "Refresh device list");
                if (settings.Command.Name != "Open" || refresh.Command.Name != "Refresh")
                    throw new InvalidOperationException("Tool actions must remain English under a non-English UI culture.");
                var beforeSearch = deviceRows.Select(i => new { i.Title, i.Subtitle, Actions = i.MoreCommands.Length }).ToArray();
                ((IDynamicListPage)page).SearchText = "__no_device_matches_this__";
                var searched = page.GetItems();
                if (!searched.Any(i => i.Title == "No matching devices")) throw new InvalidOperationException("Search did not filter the list.");
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
                {
                    Passed = true, Test = "Read-only in-process WinRT and page contract (not host UI or hardware switching)",
                    TopLevelTitle = top[0].Title, Devices = beforeSearch, SearchVerified = true, OperationLockVerified = true,
                    EnglishLabelsVerified = true, TestedUICulture = CultureInfo.CurrentUICulture.Name,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { Marshal.Release(pointer); }
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    }
}
