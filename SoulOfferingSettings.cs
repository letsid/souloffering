using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System.Windows.Forms;

namespace SoulOffering;

public class SoulOfferingSettings : ISettings
{
    [Menu("Enable Plugin")]
    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Weapon Swap Key", 1000)]
    public HotkeyNode WeaponSwapKey { get; set; } = new(Keys.X);

    [Menu("Soul Offering Key")]
    public HotkeyNode SoulOfferingKey { get; set; } = new(Keys.Q);

    [Menu("Weapon Swap Delay", "Delay after weapon swap animation in milliseconds", 2000)]
    public RangeNode<int> WeaponSwapDelay { get; set; } = new(1300, 500, 2000);

    [Menu("Cast Delay", "Delay after casting Soul Offering in milliseconds")]
    public RangeNode<int> CastDelay { get; set; } = new(400, 200, 2000);

    [Menu("Action Delay", "General delay between actions in milliseconds")]
    public RangeNode<int> ActionDelay { get; set; } = new(200, 100, 2000);

    [Menu("Debug Mode", "Enable debug logging", 3000)]
    public ToggleNode DebugMode { get; set; } = new(false);

    [Menu("Enable Logging", "Show plugin activity in log window", 3100)]
    public ToggleNode EnableLogging { get; set; } = new(false);

    [Menu("Disable in Safe Zones", "Prevents the plugin from executing in towns, hideouts, or other peaceful areas", 4000)]
    public ToggleNode DisableInSafeZones { get; set; } = new(true);
}