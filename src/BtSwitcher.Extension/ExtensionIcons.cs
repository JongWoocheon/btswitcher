using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BtSwitcher.Extension;

internal static class ExtensionIcons
{
    // Command Palette loads these packaged PNGs from the extension directory.
    // The light theme needs black artwork; the dark theme needs white artwork.
    internal static IconInfo AudioSwitch { get; } = IconHelpers.FromRelativePaths(
        @"Assets\Square44x44Logo.targetsize-32_altform-lightunplated.png",
        @"Assets\Square44x44Logo.targetsize-32_altform-unplated.png");
}
