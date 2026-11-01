namespace LesTool.Platform;

/// <summary>
/// 轮盘手势抑制闸（平台级）。截图框选等全屏会话期间要独占鼠标交互，
/// 经抑制作用域向轮盘手势声明"这段时间别响应右键拖动"。
/// 计数式而非布尔：嵌套作用域安全（多会话并存的将来场景）。
/// 鼠标钩子线程只读 <see cref="IsSuppressed"/>（volatile 语义，无锁无委托调用），
/// 不会因抑制检查触碰任何功能模块代码——LowLevelHooksTimeout 纪律保持。
/// </summary>
public static class RadialGuard
{
    private static int _count;

    /// <summary>当前是否有会话抑制轮盘手势（钩子线程热路径调用）。</summary>
    public static bool IsSuppressed => Volatile.Read(ref _count) > 0;

    /// <summary>进入抑制；用完必须 Dispose 返回的实例（推荐 using）。</summary>
    public static IDisposable BeginSuppress()
    {
        Interlocked.Increment(ref _count);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // 幂等：重复 Dispose 安全
            Interlocked.Decrement(ref _count);
        }
    }
}
