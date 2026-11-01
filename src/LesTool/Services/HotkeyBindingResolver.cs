using LesTool.Platform;
using LesTool.Services.Plugins;

namespace LesTool.Services;

internal static class HotkeyBindingResolver
{
    internal static HotkeyRegState? UnavailableState(HotkeyBinding binding)
    {
        if (!binding.Enabled || string.IsNullOrWhiteSpace(binding.Keys)) return HotkeyRegState.DisabledByUser;
        var command = ActionRegistry.Find(binding.Action);
        if (command is null)
        {
            var owner = binding.Action.Split('.')[0];
            return PluginHost.InfoOf(owner)?.Status switch
            {
                PluginStatus.Disabled => HotkeyRegState.PluginDisabled,
                PluginStatus.Failed => HotkeyRegState.PluginFailed,
                _ => HotkeyRegState.Unavailable,
            };
        }
        return !command.Scenarios.HasFlag(CommandScenarios.Hotkey)
            || !ScenarioCapabilities.Provides(CommandScenarios.Hotkey, command.Requires)
            ? HotkeyRegState.UnsupportedScenario : null;
    }

    internal static bool SameKeys(string left, string right) =>
        HotkeyService.TryParseKeys(left, out var lm, out var lk)
        && HotkeyService.TryParseKeys(right, out var rm, out var rk)
        && lm == rm && lk == rk;
}
