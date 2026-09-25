using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using BtSwitcher.Windows;

namespace BtSwitcher.Extension;

public sealed partial class BtSwitcherExtensionCommandsProvider : CommandProvider, IDisposable
{
    private readonly DevicesPage page = new(new WindowsAudioBackend());
    public BtSwitcherExtensionCommandsProvider()
    {
        Id = "btswitcher";
        DisplayName = "Bluetooth Switcher";
        Icon = ExtensionIcons.AudioSwitch;
    }
    public override ICommandItem[] TopLevelCommands() =>
    [new CommandItem(page) { Title = "Bluetooth Switcher" }];
    public override void Dispose() => page.Dispose();
}
