namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 垂直长图拼接器：纯字节运算，无 UI / Win32 依赖，可离线单测。
/// 长图与帧都是 BGRA、同宽、stride = width*4（同一采集区域的连续抓帧天然满足）。
///
/// 匹配：取长图尾部条带为模板，在新帧里自上而下逐位置算 SAD（降采样：隔行隔列），
/// 最小平均差的位置 k 即对齐点，新帧 [k+模板高, 帧高) 为新增内容。
/// 主尝试失败（差值超阈值）时用"上移模板"二次尝试，避让长图最底部的
/// 滚动条/悬停高亮等固定干扰。固定表头类干扰通常被搜索自然跳过
/// （表头与模板内容不同，SAD 不会最小）。
/// </summary>
internal sealed class VerticalStitcher
{
    private const int TemplateRows = 96;        // 模板高度（行）
    private const int MinTemplateRows = 24;     // 长图很短时的下限
    private const int FallbackShift = 16;       // 二次尝试：模板上移行数
    private const double MatchThreshold = 9.0;  // 平均每通道差（0-255），超过判为无匹配

    private byte[] _image = Array.Empty<byte>();
    private int _width;
    private int _height;

    public int Width => _width;
    public int Height => _height;
    private int Stride => _width * 4;

    public enum AppendStatus { Appended, NoNewContent, NoMatch }

    public sealed record AppendResult(AppendStatus Status, int NewRows, double BestDiff);

    /// <summary>首帧整幅入图。</summary>
    public void AddFirst(byte[] frame, int width, int height)
    {
        _width = width;
        _height = height;
        _image = (byte[])frame.Clone();
    }

    /// <summary>尝试追加一帧。无新增（滚到底/重复）返回 NoNewContent；
    /// 与长图尾部对不上（跳跃过大/内容突变）返回 NoMatch。</summary>
    public AppendResult TryAppend(byte[] frame, int frameHeight)
    {
        if (_height == 0)
            throw new InvalidOperationException("必须先 AddFirst");

        int t = Math.Min(TemplateRows, _height);
        (int K, double Diff, int TRows) match = Search(frame, frameHeight, _height - t, t);
        double bestDiff = match.Diff;

        if (match.Diff > MatchThreshold)
        {
            // 二次尝试：模板上移，避开长图最底部可能的固定干扰
            int t2 = Math.Min(TemplateRows, _height - FallbackShift);
            if (t2 >= MinTemplateRows && t2 != t)
            {
                var second = Search(frame, frameHeight, _height - FallbackShift - t2, t2);
                bestDiff = Math.Min(bestDiff, second.Diff);
                if (second.Diff < match.Diff)
                    match = second;
            }
        }

        if (match.Diff > MatchThreshold)
            return new AppendResult(AppendStatus.NoMatch, 0, match.Diff);

        int newRows = frameHeight - match.K - match.TRows;
        if (newRows <= 0)
            return new AppendResult(AppendStatus.NoNewContent, 0, match.Diff);

        int newHeight = _height + newRows;
        if (_image.Length < newHeight * Stride)
            Array.Resize(ref _image, newHeight * Stride);
        Buffer.BlockCopy(frame, (match.K + match.TRows) * Stride,
            _image, _height * Stride, newRows * Stride);
        _height = newHeight;
        return new AppendResult(AppendStatus.Appended, newRows, match.Diff);
    }

    /// <summary>完整长图副本（输出用；很长时才调）。</summary>
    public byte[] Snapshot()
    {
        var result = new byte[_height * Stride];
        Buffer.BlockCopy(_image, 0, result, 0, result.Length);
        return result;
    }

    /// <summary>预览缩略：均匀抽行到不超过 maxRows（宽不变，UI 再整体缩放）。</summary>
    public byte[] PreviewRows(int maxRows)
    {
        int rows = Math.Min(maxRows, _height);
        var result = new byte[rows * Stride];
        for (int i = 0; i < rows; i++)
        {
            int src = (int)((long)i * _height / rows) * Stride;
            Buffer.BlockCopy(_image, src, result, i * Stride, Stride);
        }
        return result;
    }

    /// <summary>在新帧中为长图 [tmplStart, tmplStart+tRows) 的模板找最佳对齐行 k。</summary>
    private (int K, double Diff, int TRows) Search(byte[] frame, int frameHeight, int tmplStart, int tRows)
    {
        int stride = Stride;
        int bestK = -1;
        double bestDiff = double.MaxValue;

        for (int k = 0; k + tRows <= frameHeight; k++)
        {
            long sad = 0;
            long count = 0;
            for (int r = 0; r < tRows; r += 2)     // 隔行采样
            {
                int rowTemplate = (tmplStart + r) * stride;
                int rowFrame = (k + r) * stride;
                for (int x = 0; x < _width; x += 2) // 隔列采样
                {
                    int i = x * 4;
                    sad += Math.Abs(_image[rowTemplate + i] - frame[rowFrame + i]);
                    sad += Math.Abs(_image[rowTemplate + i + 1] - frame[rowFrame + i + 1]);
                    sad += Math.Abs(_image[rowTemplate + i + 2] - frame[rowFrame + i + 2]);
                    count += 3;
                }
            }
            if (count == 0)
                break;
            double diff = (double)sad / count;
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestK = k;
            }
        }
        return bestK < 0
            ? (0, double.MaxValue, tRows) // 帧比模板还矮，视为无匹配
            : (bestK, bestDiff, tRows);
    }
}
