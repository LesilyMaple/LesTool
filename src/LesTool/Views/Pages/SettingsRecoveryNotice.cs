using System.Windows;
using System.Windows.Controls;
using LesTool.Platform.Controls;

namespace LesTool.Views.Pages;

// Primary: explain protected state. Secondary: explain archive + restart.
// Tertiary danger action: two-click reset. Existing SettingsSection, Body/Caption,
// semantic brushes and spacing tokens own all visual values; no new style.
internal sealed class SettingsRecoveryNotice : SettingsSection
{
    internal SettingsRecoveryNotice(string title, Action reset)
    {
        Title = title;
        Caption = "配置无法读取，原文件已受保护。恢复默认会先归档现有文件，恢复后请重新启动应用。";
        var button = new Button { Content = "归档原件并恢复默认", HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(Button.ForegroundProperty, "DangerBrush");
        var armed = false; long armedAt = 0;
        button.LostKeyboardFocus += (_, _) => { armed = false; button.Content = "归档原件并恢复默认"; };
        button.Click += (_, _) =>
        {
            if (!armed || Environment.TickCount64 - armedAt > 5000)
            { armed = true; armedAt = Environment.TickCount64; button.Content = "再次点击确认恢复默认"; return; }
            try
            {
                reset(); button.IsEnabled = false; button.Content = "已恢复，请重新启动";
                Caption = "已恢复默认配置；原文件已另存归档。重新启动应用后生效。";
            }
            catch (Exception error)
            {
                armed = false; button.Content = "重试恢复默认";
                Caption = "恢复未完成，原文件已保留：" + error.Message;
            }
        };
        Content = button;
    }
}
