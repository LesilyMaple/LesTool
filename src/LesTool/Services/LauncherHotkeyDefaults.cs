namespace LesTool.Services;
internal static class LauncherHotkeyDefaults
{
    internal static bool InitializeOnce(AppSettingsData settings)
    {
        if (settings.LauncherHotkeyInitialized != false) return false;
        settings.LauncherHotkeyInitialized = true;
        if (!settings.Hotkeys.Any(binding => binding.Action.Equals("panel.launcher", StringComparison.OrdinalIgnoreCase)))
            settings.Hotkeys.Add(new() { Keys = "Alt+Space", Action = "panel.launcher", Enabled = true });
        return true;
    }
}
