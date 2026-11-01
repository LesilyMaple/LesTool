using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LesTool.Services.Plugins;

/// <summary>Explicit developer/CI smoke mode; real plugin initialization with a disposable profile and no input automation.</summary>
internal static class PluginStartupCheck
{
    internal static async Task<int> RunAsync(string reportPath)
    {
        var errors = new List<string>(); var checks = new List<string>(); var skipped = new List<string>();
        var temporary = Path.Combine(Path.GetTempPath(), "LesTool.StartupCheck", Guid.NewGuid().ToString("N"));
        var previousRoot = AppPaths.DataRoot;
        AppPaths.DataRoot = temporary;
        var plugins = Array.Empty<PluginInfo>();
        try
        {
            Directory.CreateDirectory(temporary);
            foreach (var id in new[] { "downloads", "preview" })
            {
                var settings = Path.Combine(temporary, "plugins", id, "settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
                File.WriteAllText(settings, "{\"enabled\":false,\"sweepMode\":\"Off\"}");
            }
            PluginHost.Load();
            plugins = PluginHost.Discovered.ToArray();
            foreach (var plugin in plugins.Where(p => p.Status != PluginStatus.Loaded)) errors.Add(plugin.Id + ": " + plugin.Reason);
            if (plugins.Length == 0) throw new InvalidDataException("No plugins discovered");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                var dictionaries = Application.Current.Resources.MergedDictionaries;
                dictionaries.RemoveAt(dictionaries.Count - 1);
                dictionaries.Add(new ResourceDictionary { Source = new Uri("/LesTool;component/Themes/" + theme + ".xaml", UriKind.Relative) });
                foreach (var page in PluginHost.Pages)
                {
                    try
                    {
                        var view = page.Factory();
                        view.Measure(new Size(980, 1600)); view.Arrange(new Rect(0, 0, 980, 1600)); view.UpdateLayout();
                        checks.Add(page.PluginId + ":settings:" + theme);
                    }
                    catch (Exception error) { errors.Add(page.PluginId + ":settings:" + theme + ": " + error); }
                }
            }
            if (plugins.Any(p => p.Id == "preview" && p.Status == PluginStatus.Loaded))
            {
                try { CheckOffice(temporary, checks); }
                catch (Exception error) { errors.Add("preview:office: " + error); }
            }
            var hasModels = File.Exists(Path.Combine(AppContext.BaseDirectory, "plugins", "screenshot", "assets", "models", "v5", "ch_PP-OCRv5_mobile_det.onnx"));
            if (!hasModels) skipped.Add("screenshot:native-ocr:optional-models-absent");
            if (hasModels && plugins.Any(p => p.Id == "screenshot" && p.Status == PluginStatus.Loaded))
            {
                try
                {
                    var assembly = PluginHost.AssemblyOf("screenshot");
                    using var reflection = AssemblyLoadContext.GetLoadContext(assembly)!.EnterContextualReflection();
                    var ocr = assembly.GetType("LesTool.Plugin.Screenshot.ScreenshotOcr", true)!;
                    try
                    {
                        var visual = new DrawingVisual();
                        using (var drawing = visual.RenderOpen())
                        {
                            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1024, 180));
                            drawing.DrawText(new FormattedText("LesTool 123 Architecture Test", System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, new Typeface("Arial"), 48, Brushes.Black, 1), new Point(12, 18));
                        }
                        var image = new RenderTargetBitmap(1024, 180, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var sample = File.Create(Path.ChangeExtension(reportPath, ".ocr-sample.png"))) encoder.Save(sample);
                        var task = (Task)ocr.GetMethod("RunAsync")!.Invoke(null, new object[] { image })!;
                        await task;
                        var result = (System.Collections.ICollection?)task.GetType().GetProperty("Result")!.GetValue(task);
                        if (result is null || result.Count == 0) throw new InvalidDataException("OCR returned no text from the synthetic sample");
                        checks.Add("screenshot:native-ocr:synthetic-text");
                        var windowType = assembly.GetType("LesTool.Plugin.Screenshot.OcrResultWindow", true)!;
                        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                            new object[] { image, result, new Rect(0, 0, 1024, 180) }, null)!;
                        try { window.Measure(new Size(980, 600)); window.UpdateLayout(); checks.Add("screenshot:ocr-window:baml"); }
                        finally { window.Close(); }
                    }
                    finally { ocr.GetMethod("Release")!.Invoke(null, null); }
                }
                catch (Exception error) { errors.Add("screenshot:native-ocr: " + error); }
            }
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            try { await PluginHost.ShutdownAllAsync(); await PluginHost.FlushSettingsAsync(CancellationToken.None); }
            catch (Exception error) { errors.Add("shutdown: " + error); }
            AppPaths.DataRoot = previousRoot;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Plugins = plugins.Select(p => new { p.Id, Status = p.Status.ToString(), p.Reason }), Checks = checks, Skipped = skipped, Errors = errors,
            ProfileIsolated = true, InputHooksEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        // This uniquely allocated profile contains only this check's synthetic settings/history.
        if (Path.GetDirectoryName(Path.GetFullPath(temporary)) == Path.Combine(Path.GetTempPath(), "LesTool.StartupCheck"))
            try { Directory.Delete(temporary, recursive: true); } catch (IOException) { }
        return errors.Count == 0 ? 0 : 1;
    }
    private static void CheckOffice(string root, List<string> checks)
    {
        var assembly = PluginHost.AssemblyOf("preview");
        using var reflection = AssemblyLoadContext.GetLoadContext(assembly)!.EnterContextualReflection();
        var window = (Window)Activator.CreateInstance(assembly.GetType("LesTool.Plugin.Preview.PreviewWindow", true)!, nonPublic: true)!;
        try { window.Measure(new Size(640, 480)); checks.Add("preview:window:baml"); } finally { window.Close(); }
        const string relationship = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
        var excel = Path.Combine(root, "sample.xlsx"); var word = Path.Combine(root, "sample.docx");
        WriteZip(excel, new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = $"<Types xmlns='{contentTypes}'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>",
            ["_rels/.rels"] = $"<Relationships xmlns='{relationship}'><Relationship Id='rId1' Type='{office}/officeDocument' Target='xl/workbook.xml'/></Relationships>",
            ["xl/workbook.xml"] = $"<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='{office}'><sheets><sheet name='Sample' sheetId='1' r:id='rId1'/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns='{relationship}'><Relationship Id='rId1' Type='{office}/worksheet' Target='worksheets/sheet1.xml'/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>LesTool</t></is></c><c r='B1'><v>123</v></c></row></sheetData></worksheet>",
        });
        WriteZip(word, new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = $"<Types xmlns='{contentTypes}'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>",
            ["_rels/.rels"] = $"<Relationships xmlns='{relationship}'><Relationship Id='rId1' Type='{office}/officeDocument' Target='word/document.xml'/></Relationships>",
            ["word/document.xml"] = "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>LesTool Preview</w:t></w:r></w:p><w:sectPr/></w:body></w:document>",
        });
        var documents = assembly.GetType("LesTool.Plugin.Preview.OfficeDocument", true)!;
        var views = assembly.GetType("LesTool.Plugin.Preview.OfficeViews", true)!;
        foreach (var item in new[] { ("Excel", excel, "Sheets"), ("Word", word, "Blocks") })
        {
            var model = documents.GetMethod("Read" + item.Item1, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { item.Item2 })!;
            var collection = (System.Collections.ICollection)model.GetType().GetProperty(item.Item3)!.GetValue(model)!;
            if (collection.Count == 0) throw new InvalidDataException("Empty Office preview: " + item.Item1);
            var view = (FrameworkElement)views.GetMethod("Build" + item.Item1, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { model })!;
            view.Measure(new Size(640, 1000)); view.Arrange(new Rect(0, 0, 640, 1000)); view.UpdateLayout();
            checks.Add("preview:" + item.Item1 + ":parse-and-view");
        }
    }

    private static void WriteZip(string path, Dictionary<string, string> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        { using var writer = new StreamWriter(archive.CreateEntry(item.Key).Open()); writer.Write(item.Value); }
    }

}
