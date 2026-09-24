using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentDeck.Native;

/// <summary>
/// Global Ctrl+Shift+Win hotkey via a low-level keyboard hook (RegisterHotKey needs a non-modifier key).
/// Keys are never swallowed. Injected input from other apps (e.g. Logitech Options mapping a mouse button)
/// still counts; only keystrokes tagged with <see cref="Input.OurTag"/> are ignored.
/// Must be created on a thread with a message loop (the WPF UI thread).
/// </summary>
sealed class ModifierHotkey : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    static readonly HashSet<uint> Ctrl = [0x11, 0xA2, 0xA3], Shift = [0x10, 0xA0, 0xA1], Win = [0x5B, 0x5C];
    const byte MaskKey = 0xE8; // unassigned VK; tapping it stops the Win release from opening Start

    readonly Action _onTrigger;
    readonly HookProc _proc; // keep a reference or the callback gets collected
    readonly HashSet<uint> _down = [];
    readonly IntPtr _hook;
    bool _fired;

    public ModifierHotkey(Action onTrigger)
    {
        _onTrigger = onTrigger;
        _proc = Hook;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Info($"Keyboard hook failed ({Marshal.GetLastWin32Error()}); shortcut disabled.");
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((ulong)info.dwExtraInfo != Input.OurTag)
            {
                int msg = (int)wParam;
                if (msg is 0x0100 or 0x0104) _down.Add(info.vkCode);      // WM_KEYDOWN / WM_SYSKEYDOWN
                else if (msg is 0x0101 or 0x0105) _down.Remove(info.vkCode); // WM_KEYUP / WM_SYSKEYUP

                bool allHeld = _down.Overlaps(Ctrl) && _down.Overlaps(Shift) && _down.Overlaps(Win);
                if (allHeld && !_fired)
                {
                    _fired = true;
                    Input.Tap(MaskKey);
                    _onTrigger();
                }
                else if (!allHeld) _fired = false;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
