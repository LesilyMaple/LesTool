using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using Serilog;
using ICell = NPOI.SS.UserModel.ICell;

namespace LesTool.Plugin.Preview;

/// <summary>
/// Office 只读解析（三层瀑布第二层的数据层）：NPOI 读 xlsx/xls/docx/doc 为
/// 纯数据模型，后台线程执行（不触任何 UI）；渲染由 OfficeViews 在 UI 线程消费。
/// 预览语义是"看到什么"：公式不重算只读缓存值，复杂排版（图表/条件格式/页眉
/// 页脚/浮动图）不读取。护栏：Excel 单表 500 行/40 列/6000 格，Word 2000 块、
/// 图片 30 张、单图 10MB；超出截断并在模型上标注，由视图提示。
/// </summary>
internal static class OfficeDocument
{
    internal const long MaxFileBytes = 20 * 1024 * 1024;

    internal const int SheetMaxRows = 500;
    internal const int SheetMaxCols = 40;
    internal const int SheetMaxCells = 6000;

    internal const int DocMaxBlocks = 2000;
    internal const int DocMaxImages = 30;
    internal const long DocMaxImageBytes = 10 * 1024 * 1024;

    // ---------- 模型 ----------

    internal sealed class ExcelWorkbook
    {
        public required List<ExcelSheet> Sheets { get; init; }
        public bool Truncated { get; set; }
    }

    internal sealed class ExcelSheet
    {
        public required string Name { get; init; }
        public required double[] ColWidthsDip { get; init; }
        public required double[] RowHeightsDip { get; init; }
        public required List<ExcelCell> Cells { get; init; } // 含合并区首格（带跨度）
        public bool Truncated { get; set; }
    }

    internal sealed class ExcelCell
    {
        public required int Row { get; init; }
        public required int Col { get; init; }
        public int RowSpan { get; init; } = 1;
        public int ColSpan { get; init; } = 1;
        public required string Text { get; init; }
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public string? FgHex { get; init; }   // "#RRGGBB"
        public string? BgHex { get; init; }
        public byte Align { get; init; }      // 0 默认 1 左 2 中 3 右
    }

    internal sealed class WordDocument
    {
        public required List<object> Blocks { get; init; } // WordParagraph | WordTable | WordImage
        public bool Truncated { get; set; }
    }

    internal sealed class WordParagraph
    {
        public required List<WordRun> Runs { get; init; }
        public byte Align { get; init; }      // 0 默认/左 2 中 3 右
        public bool Heading { get; init; }
    }

    internal sealed record WordRun(string Text, bool Bold, bool Italic, double SizePt, string? FgHex);

    internal sealed class WordTable
    {
        public required List<WordCell> Cells { get; init; }
        public int Rows { get; set; }
        public int Cols { get; set; }
    }

    internal sealed class WordCell
    {
        public required int Row { get; init; }
        public required int Col { get; init; }
        public required string Text { get; init; }
        public bool Bold { get; init; }
    }

    internal sealed class WordImage
    {
        public required byte[] Bytes { get; init; }
    }

    // ---------- Excel 读取 ----------

    internal static ExcelWorkbook ReadExcel(string path)
    {
        GuardFile(path);
        using var stream = File.OpenRead(path);
        using IWorkbook book = WorkbookFactory.Create(stream); // 自动识别 xlsx/xls
        if (book.NumberOfSheets == 0)
            throw new InvalidDataException("工作簿没有任何工作表");

        var workbook = new ExcelWorkbook { Sheets = new List<ExcelSheet>(book.NumberOfSheets) };
        for (int s = 0; s < book.NumberOfSheets; s++)
        {
            ISheet sheet = book.GetSheetAt(s);
            workbook.Sheets.Add(ReadSheet(book, sheet));
        }
        Log.Debug("Excel 解析完成：{Path}（{Sheets} 表）", path, workbook.Sheets.Count);
        return workbook;
    }

    private static ExcelSheet ReadSheet(IWorkbook book, ISheet sheet)
    {
        int lastRow = (int)sheet.LastRowNum;
        bool truncated = false;

        // 合并区：首格带跨度，其余格跳过
        var mergedOwner = new Dictionary<(int Row, int Col), (int RowSpan, int ColSpan)>();
        var mergedSlave = new HashSet<(int Row, int Col)>();
        for (int i = 0; i < sheet.NumMergedRegions; i++)
        {
            var r = sheet.GetMergedRegion(i);
            if (r.FirstRow > SheetMaxRows - 1 || r.FirstColumn > SheetMaxCols - 1) continue;
            int rowSpan = Math.Min((int)r.LastRow - r.FirstRow + 1, SheetMaxRows - r.FirstRow);
            int colSpan = Math.Min(r.LastColumn - r.FirstColumn + 1, SheetMaxCols - r.FirstColumn);
            mergedOwner[(r.FirstRow, r.FirstColumn)] = (rowSpan, colSpan);
            for (int rr = r.FirstRow; rr <= r.FirstRow + rowSpan - 1; rr++)
                for (int cc = r.FirstColumn; cc <= r.FirstColumn + colSpan - 1; cc++)
                    if (rr != r.FirstRow || cc != r.FirstColumn)
                        mergedSlave.Add((rr, cc));
        }

        int rowCount = Math.Min(lastRow + 1, SheetMaxRows);
        if (lastRow + 1 > SheetMaxRows) truncated = true;

        int colCount = 0;
        var cells = new List<ExcelCell>();
        double[] widths = new double[SheetMaxCols];
        double[] heights = new double[rowCount];

        for (int r = 0; r < rowCount; r++)
        {
            IRow? row = sheet.GetRow(r);
            heights[r] = row is null || row.Height == -1
                ? 20.0 // Excel 默认行高 15pt ≈ 20 DIP
                : Math.Clamp(row.HeightInPoints * 96.0 / 72.0, 12, 200);
            if (row is null) continue;

            int lastCol = Math.Min((int)row.LastCellNum, SheetMaxCols);
            colCount = Math.Max(colCount, lastCol);
            for (int c = 0; c < lastCol; c++)
            {
                if (mergedSlave.Contains((r, c))) continue;
                ICell? cell = row.GetCell(c);
                string? text = CellText(cell);
                if (string.IsNullOrEmpty(text)) continue;
                if (cells.Count >= SheetMaxCells)
                {
                    truncated = true;
                    break;
                }

                ICellStyle? style = cell?.CellStyle;
                IFont? font = style?.GetFont(book);
                var (rowSpan, colSpan) = mergedOwner.GetValueOrDefault((r, c), (1, 1));
                cells.Add(new ExcelCell
                {
                    Row = r,
                    Col = c,
                    RowSpan = rowSpan,
                    ColSpan = colSpan,
                    Text = text,
                    Bold = font?.IsBold ?? false,
                    Italic = font?.IsItalic ?? false,
                    FgHex = FontHex(book, font),
                    BgHex = FillHex(style),
                    Align = AlignOf(style),
                });
            }
            if (cells.Count >= SheetMaxCells) break;
        }
        if ((int)(sheet.GetRow(0)?.LastCellNum ?? 0) > SheetMaxCols
            || (int)(sheet.GetRow(sheet.LastRowNum)?.LastCellNum ?? 0) > SheetMaxCols)
            truncated = true;

        for (int c = 0; c < SheetMaxCols; c++)
        {
            double raw = sheet.GetColumnWidth(c); // 1/256 字符宽，默认 2043 ≈ 8 字符
            widths[c] = Math.Clamp(raw / 256.0 * 7.2 + 6, 24, 400);
        }

        Array.Resize(ref widths, Math.Max(colCount, 1));
        return new ExcelSheet
        {
            Name = sheet.SheetName,
            ColWidthsDip = widths,
            RowHeightsDip = heights,
            Cells = cells,
            Truncated = truncated,
        };
    }

    /// <summary>显示值：公式取缓存结果；数字保留 15 位有效；日期列给日期格式。</summary>
    private static string? CellText(ICell? cell)
    {
        if (cell is null) return null;
        try
        {
            CellType type = cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;
            return type switch
            {
                CellType.String => cell.StringCellValue,
                CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                    ? cell.DateCellValue?.ToString("yyyy-MM-dd HH:mm")
                    : cell.NumericCellValue.ToString("G15"),
                CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
                CellType.Error => "#ERR",
                _ => null,
            };
        }
        catch
        {
            // 单格取值失败（怪异格式）置空，不挡整表
            return null;
        }
    }

    private static byte AlignOf(ICellStyle? style)
    {
        if (style is null) return 0;
        try
        {
            return style.Alignment switch
            {
                HorizontalAlignment.Center => 2,
                HorizontalAlignment.Right => 3,
                HorizontalAlignment.Left or HorizontalAlignment.Justify => 1,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string? FontHex(IWorkbook book, IFont? font)
    {
        if (font is null) return null;
        try
        {
            if (font is XSSFFont xfont && xfont.GetXSSFColor()?.RGB is { Length: 3 or 4 } rgb)
                return ToHex(rgb);
        }
        catch
        {
            // 颜色拿不到按默认色（.xls 索引调色板一期不解析）
        }
        return null;
    }

    private static string? FillHex(ICellStyle? style)
    {
        if (style is null) return null;
        try
        {
            if (style.FillPattern == FillPattern.SolidForeground
                && style.FillForegroundColorColor is XSSFColor fill
                && fill.RGB is { Length: > 0 } rgb)
                return ToHex(rgb);
        }
        catch
        {
            // 同上
        }
        return null;
    }

    private static string ToHex(byte[] rgb) =>
        rgb.Length >= 3 ? $"#{rgb[^3]:X2}{rgb[^2]:X2}{rgb[^1]:X2}" : "#000000";

    // ---------- Word 读取 ----------

    internal static WordDocument ReadWord(string path)
    {
        GuardFile(path);
        if (path.EndsWith(".doc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("暂不支持老格式 .doc，请转存为 .docx 后预览");

        using var stream = File.OpenRead(path);
        using var doc = new XWPFDocument(stream);
        var document = new WordDocument { Blocks = new List<object>() };
        int images = 0;

        // NPOI 无混合 body 迭代器：段落与表格分开读，表格统一追加在段落后
        //（位置可能与原文不同，内容不丢——已知降级）
        foreach (XWPFParagraph para in doc.Paragraphs)
        {
            if (document.Blocks.Count >= DocMaxBlocks)
            {
                document.Truncated = true;
                break;
            }
            ReadParagraph(para, document, ref images);
        }
        foreach (XWPFTable table in doc.Tables)
        {
            if (document.Blocks.Count >= DocMaxBlocks)
            {
                document.Truncated = true;
                break;
            }
            ReadTable(table, document);
        }
        Log.Debug("Word 解析完成：{Path}（{Blocks} 块）", path, document.Blocks.Count);
        return document;
    }

    private static void ReadParagraph(XWPFParagraph para, WordDocument document, ref int images)
    {
        var runs = new List<WordRun>();
        foreach (XWPFRun run in para.Runs)
        {
            string text = run.Text ?? string.Empty;
            if (text.Length == 0) continue;
            double sizePt = run.FontSize > 0 ? run.FontSize : 11;
            runs.Add(new WordRun(text, run.IsBold, run.IsItalic, sizePt, NormalizeHex(run.GetColor())));
        }

        foreach (XWPFPicture picture in para.Runs.SelectMany(r => r.GetEmbeddedPictures()))
        {
            if (images >= DocMaxImages)
            {
                document.Truncated = true;
                break;
            }
            byte[]? data = picture.GetPictureData()?.Data;
            if (data is not { Length: > 0 } || data.Length > DocMaxImageBytes) continue;
            document.Blocks.Add(new WordImage { Bytes = data });
            images++;
        }

        if (runs.Count == 0)
        {
            // 空段（含只有图片的段）：图片已收，段落本身占位换行
            document.Blocks.Add(new WordParagraph { Runs = new List<WordRun>(), Align = 0, Heading = false });
            return;
        }

        string styleId = para.StyleID ?? string.Empty;
        bool heading = styleId.Contains("heading", StringComparison.OrdinalIgnoreCase)
                       || styleId.Contains("标题", StringComparison.Ordinal);
        document.Blocks.Add(new WordParagraph
        {
            Runs = runs,
            Align = para.Alignment switch
            {
                ParagraphAlignment.CENTER => 2,
                ParagraphAlignment.RIGHT => 3,
                _ => 0,
            },
            Heading = heading,
        });
    }

    private static void ReadTable(XWPFTable table, WordDocument document)
    {
        var wordTable = new WordTable { Cells = new List<WordCell>() };
        int rows = table.Rows.Count;
        int cols = 0;
        for (int r = 0; r < rows && r < SheetMaxRows; r++)
        {
            XWPFTableRow row = table.Rows[r];
            var cells = row.GetTableCells();
            cols = Math.Max(cols, cells.Count);
            for (int c = 0; c < cells.Count && c < SheetMaxCols; c++)
            {
                XWPFTableCell cell = cells[c];
                string text = cell.GetTextRecursively().Trim();
                if (text.Length == 0) continue;
                wordTable.Cells.Add(new WordCell
                {
                    Row = r,
                    Col = c,
                    Text = text,
                    Bold = cell.Paragraphs.Count > 0 && cell.Paragraphs[0].Runs.Count > 0 && cell.Paragraphs[0].Runs[0].IsBold,
                });
            }
        }
        wordTable.Rows = Math.Min(rows, SheetMaxRows);
        wordTable.Cols = Math.Clamp(cols, 1, SheetMaxCols);
        if (rows > SheetMaxRows || wordTable.Cells.Count >= SheetMaxCells) document.Truncated = true;
        document.Blocks.Add(wordTable);
    }

    private static string? NormalizeHex(string? color) =>
        string.IsNullOrEmpty(color) ? null : $"#{color.TrimStart('#').ToUpperInvariant()}";

    private static void GuardFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException($"文件超过 {MaxFileBytes / 1024 / 1024} MB 预览上限");
    }
}
