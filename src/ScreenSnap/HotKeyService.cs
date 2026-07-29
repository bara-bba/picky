using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenSnap;

/// <summary>A selectable global-hotkey preset: display name + Win32 modifiers/virtual-key.</summary>
internal sealed record HotKeyDef(string Name, uint Modifiers, uint VirtualKey)
{
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_WIN = 0x8;

    private const uint VK_S = 0x53;
    private const uint VK_1 = 0x31;
    private const uint VK_SNAPSHOT = 0x2C; // PrintScreen

    public static readonly IReadOnlyList<HotKeyDef> Presets = new[]
    {
        new HotKeyDef("Win+Shift+S", MOD_WIN | MOD_SHIFT, VK_S),
        new HotKeyDef("Ctrl+Shift+S", MOD_CONTROL | MOD_SHIFT, VK_S),
        new HotKeyDef("PrtScn", 0, VK_SNAPSHOT),
        new HotKeyDef("Ctrl+Shift+1", MOD_CONTROL | MOD_SHIFT, VK_1),
    };

    public static HotKeyDef? FromName(string? name) => Presets.FirstOrDefault(p => p.Name == name);

    public override string ToString() => Name;
}

/// <summary>
/// Registers a single system-wide hotkey and invokes a callback when it fires. Prefers
/// RegisterHotKey against a hidden message window; for keys Windows reserves (notably
/// Print Screen), it falls back to a low-level keyboard hook so the shortcut still works.
/// Only one hotkey is active at a time.
/// </summary>
internal sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotKeyId = 0xB001;

    // Modifier bits (match HotKeyDef) + low-level-hook plumbing.
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_WIN = 0x8;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly HwndSource _source;
    private readonly Action _onPressed;
    private bool _registered;

    // Low-level-hook fallback state.
    private IntPtr _llHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _llProc; // kept referenced so the delegate isn't GC'd
    private HotKeyDef? _hookDef;

    public HotKeyService(Action onPressed)
    {
        _onPressed = onPressed;

        // A hidden 0-size top-level window that only exists to receive WM_HOTKEY.
        _source = new HwndSource(new HwndSourceParameters("ScreenSnapHotKey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>Switches the active hotkey; returns false if it couldn't be claimed at all.</summary>
    public bool Apply(HotKeyDef def)
    {
        // Tear down whichever mechanism was previously active.
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotKeyId);
            _registered = false;
        }

        RemoveHook();

        _registered = RegisterHotKey(_source.Handle, HotKeyId, def.Modifiers | MOD_NOREPEAT, def.VirtualKey);
        if (_registered)
        {
            return true;
        }

        // RegisterHotKey refused it (e.g. Print Screen is reserved by Windows) — watch the
        // key with a global low-level keyboard hook instead.
        _hookDef = def;
        _llProc = HookCallback;
        _llHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llProc, GetModuleHandle(null), 0);
        return _llHook != IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            _onPressed();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hookDef is { } def)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
                if (vk == def.VirtualKey && ModifiersMatch(def.Modifiers))
                {
                    _onPressed();
                    return (IntPtr)1; // swallow so the OS default (e.g. Snipping) doesn't also fire
                }
            }
        }

        return CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private static bool ModifiersMatch(uint modifiers)
    {
        bool ctrl = Down(VK_CONTROL);
        bool shift = Down(VK_SHIFT);
        bool alt = Down(VK_MENU);
        bool win = Down(VK_LWIN) || Down(VK_RWIN);

        return ctrl == ((modifiers & MOD_CONTROL) != 0)
            && shift == ((modifiers & MOD_SHIFT) != 0)
            && alt == ((modifiers & MOD_ALT) != 0)
            && win == ((modifiers & MOD_WIN) != 0);

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private void RemoveHook()
    {
        if (_llHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
        }

        _llProc = null;
        _hookDef = null;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotKeyId);
        }

        RemoveHook();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
