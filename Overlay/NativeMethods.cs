using System.Runtime.InteropServices;

namespace NekoNoTe.Overlay;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080; // タスクバー/Alt+Tabに表示しない

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    public static bool TryGetWindowPosition(IntPtr hWnd, out int x, out int y)
    {
        if (GetWindowRect(hWnd, out var rect))
        {
            x = rect.Left;
            y = rect.Top;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>ネイティブのタイトルバーをダークテーマ配色にする(Windows 10 1809以降)。</summary>
    public static void EnableDarkTitleBar(IntPtr hWnd)
    {
        var enabled = 1;
        DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    private static long GetWindowExStyle(IntPtr hWnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hWnd, GWL_EXSTYLE);

    private static void SetWindowExStyle(IntPtr hWnd, long exStyle)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }
        else
        {
            SetWindowLong32(hWnd, GWL_EXSTYLE, unchecked((int)exStyle));
        }
    }

    /// <summary>
    /// ウィンドウをクリック・マウスイベントが背後へ完全に貫通する状態にする。
    /// これによりオーバーレイの下にあるゲームやアプリの操作を妨げない。
    /// </summary>
    public static void MakeClickThrough(IntPtr hWnd)
    {
        var exStyle = GetWindowExStyle(hWnd);
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
        SetWindowExStyle(hWnd, exStyle);
    }
}
