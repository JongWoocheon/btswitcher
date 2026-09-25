using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BtSwitcher.Extension;

internal static class ExtensionIcons
{
    // Theme names describe the background: black on light, white on dark.
    internal static IconInfo AudioSwitch { get; } = new(
        light: FromAsset("audio-switch.svg"),
        dark: FromAsset("audio-switch-dark.svg"));

    // The host loads these files, so resolve paths against the extension directory.
    private static IconData FromAsset(string name) =>
        new(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", name)).AbsoluteUri);
}
