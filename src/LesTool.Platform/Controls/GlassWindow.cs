using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LesTool.Platform.Controls;

/// <summary>
/// 亚克力玻璃行为（attached）：挂在浮层面板窗上，当前皮肤声明 SkinMeta.Acrylic=true 时
/// 给窗口挂 DWM 模糊背景（Win10 1803+）。只在"启用"时才碰 DWM——对分层透明窗口
/// 无故调用 SetWindowCompositionAttribute 也可能引入合成异常，未启用一律不动它；
/// 皮肤切走时仅对"曾经开过"的窗口补一次关闭。
/// </summary>
public static class GlassWindow
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GlassWindow), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    private static readonly HashSet<Window> _active = new(); // 当前挂着亚克力的窗口

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w) return;
        if ((bool)e.NewValue)
        {
            w.Loaded += (_, _) => Apply(w);
            w.Closed += (_, _) => _active.Remove(w);
        }
        else
        {
            Apply(w, forceDisable: true);
        }
    }

    /// <summary>皮肤切换后由 ThemeManager 调用：只重刷挂了本行为的窗口。</summary>
    public static void RefreshAll()
    {
        if (Application.Current is null) return;
        foreach (Window w in Application.Current.Windows)
        {
            if (GetEnabled(w)) Apply(w);
        }
    }

    private static void Apply(Window w, bool forceDisable = false)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return; // 未加载完，Loaded 回调会再触发

        bool on = !forceDisable
                  && Application.Current?.TryFindResource("SkinMeta.Acrylic") is bool b && b
                  && Application.Current?.TryFindResource("SkinGlassTint") is Color;
        if (on)
        {
            var tint = (Color)Application.Current!.TryFindResource("SkinGlassTint")!;
            SetAccent(hwnd, AccentEnableAcrylicBlurBehind, tint);
            _active.Add(w);
        }
        else if (_active.Remove(w))
        {
            // 只关闭真正开过的窗口；从未启用的不碰 DWM
            SetAccent(hwnd, AccentDisable, default);
        }
    }

    private const int AccentDisable = 1;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int WindowCompositionAttribute = 19; // WCA_ACCENT_POLICY

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int DataSize;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private static void SetAccent(IntPtr hwnd, int state, Color tint)
    {
        var policy = new AccentPolicy
        {
            AccentState = state,
            GradientColor = (uint)(tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R),
        };
        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
            DataSize = Marshal.SizeOf<AccentPolicy>(),
        };
        try
        {
            Marshal.StructureToPtr(policy, data.Data, false);
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(data.Data);
        }
    }
}
