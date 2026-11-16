using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NekoNoTe.Recorder;

/// <summary>
/// ゲームなど別のウィンドウが前にある時でも、キーとマウス(真ん中・サイドボタン・ホイール)を受け取る。
/// 受け取るだけで横取りはしない(ゲームにもそのまま届く)。UI スレッドで作ること。
/// </summary>
public sealed class InputHook : IDisposable
{
    public const int VkMButton = 0x04, VkXButton1 = 0x05, VkXButton2 = 0x06;

    private readonly HookProc _keyProc, _mouseProc;   // GC に回収されないよう持っておく
    private IntPtr _keyHook, _mouseHook;
    private readonly HashSet<int> _down = new();

    /// <summary>キー(またはマウスのボタン)が押された。今押されているキー全部も渡す</summary>
    public event Action<int, IReadOnlyCollection<int>>? KeyDown;
    public event Action<int, IReadOnlyCollection<int>>? KeyUp;
    /// <summary>ホイール(上が +1、下が -1)。今押されているキーも渡す</summary>
    public event Action<int, IReadOnlyCollection<int>>? Wheel;

    public InputHook(bool mouse = true)
    {
        _keyProc = KeyboardProc;
        _mouseProc = MouseProc;
        using var cur = Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        var h = GetModuleHandle(mod.ModuleName);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, h, 0);
        if (mouse) _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, h, 0);
    }

    /// <summary>左右の区別をなくす(左 Shift も右 Shift も «Shift»)</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;

    /// <summary>押されたキーが組み合わせと合っているか(余計な修飾キーが押されていたら別の組み合わせとみなす: Ctrl+R と R を区別する)</summary>
    public static bool Matches(List<int> combo, int vk, IReadOnlyCollection<int> down) =>
        combo.Count > 0 && combo.Contains(vk) && combo.All(down.Contains)
        && down.Where(IsModifier).All(combo.Contains);

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = Normalize(Marshal.ReadInt32(lParam));
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (_down.Add(vk)) KeyDown?.Invoke(vk, _down.ToArray());   // 押しっぱなしのくり返しは無視
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                if (_down.Remove(vk)) KeyUp?.Invoke(vk, _down.ToArray());
            }
        }
        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int x = (int)(info.mouseData >> 16);
            switch (msg)
            {
                case WM_MOUSEWHEEL:
                    short delta = (short)(info.mouseData >> 16);
                    Wheel?.Invoke(delta > 0 ? 1 : -1, _down.ToArray());
                    break;
                case WM_MBUTTONDOWN: Press(VkMButton); break;
                case WM_MBUTTONUP: _down.Remove(VkMButton); break;
                case WM_XBUTTONDOWN: Press(x == 1 ? VkXButton1 : VkXButton2); break;
                case WM_XBUTTONUP: _down.Remove(x == 1 ? VkXButton1 : VkXButton2); break;
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void Press(int vk)
    {
        _down.Add(vk);
        KeyDown?.Invoke(vk, _down.ToArray());
    }

    /// <summary>キーの組み合わせを «Ctrl + V» のような文字にする</summary>
    public static string Describe(IReadOnlyCollection<int> keys)
    {
        if (keys.Count == 0) return "なし";
        return string.Join(" + ", keys.OrderBy(k => IsModifier(k) ? 0 : 1).Select(Name));
    }

    public static string Name(int vk) => vk switch
    {
        0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt", 0x5B or 0x5C => "Win",
        VkMButton => "マウス中", VkXButton1 => "マウス戻る", VkXButton2 => "マウス進む",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab", 0x08 => "BackSpace",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "テンキー" + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => ((System.Windows.Forms.Keys)vk).ToString(),
    };

    public void Dispose()
    {
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyHook = _mouseHook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int WM_MOUSEWHEEL = 0x20A, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
}
