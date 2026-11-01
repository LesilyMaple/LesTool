using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LesTool.Views.Pages;

namespace LesTool.Tests;
internal static class RecoveryVisualCheck
{
    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = new Application();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            app.Resources.MergedDictionaries.Clear();
            foreach (var resource in new[] { "tokens", "Design", theme })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LesTool;component/Themes/" + resource + ".xaml", UriKind.Relative) });
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                var body = new StackPanel { Margin = new Thickness(32, 24, 32, 24) };
                var title = new TextBlock { Text = "设置", FontWeight = FontWeights.SemiBold };
                title.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeTitle"); title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                body.Children.Add(title);
                body.Children.Add(new SettingsRecoveryNotice("应用配置需要恢复", () => { }));
                var surface = new Border { Width = 640, Height = 280, Child = body };
                surface.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
                TextElementFont(surface);
                surface.Measure(new Size(640, 280)); surface.Arrange(new Rect(0, 0, 640, 280)); surface.UpdateLayout();
                var image = new RenderTargetBitmap((int)(640 * scale), (int)(280 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                image.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(directory, $"recovery-{theme}-{(int)(scale * 100)}.png")); encoder.Save(file);
            }
        }
        app.Shutdown();
    }
    private static void TextElementFont(DependencyObject target) => System.Windows.Documents.TextElement.SetFontFamily(target, new FontFamily("Segoe UI, Microsoft YaHei"));
}
