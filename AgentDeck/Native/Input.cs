using System.Runtime.InteropServices;

namespace AgentDeck.Native;

/// <summary>Synthetic keystrokes and window activation.</summary>
static class Input
{
    /// <summary>Marks keystrokes we send, so our own keyboard hook can ignore them.</summary>
    public const ulong OurTag = 0x53444B31;

    public const byte VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_V = 0x56;
    const uint KEYEVENTF_KEYUP = 0x0002;

    public static void Tap(byte vk, params byte[] modifiers)
    {
        foreach (var m in modifiers) keybd_event(m, 0, 0, (UIntPtr)OurTag);
        keybd_event(vk, 0, 0, (UIntPtr)OurTag);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, (UIntPtr)OurTag);
        for (int i = modifiers.Length - 1; i >= 0; i--) keybd_event(modifiers[i], 0, KEYEVENTF_KEYUP, (UIntPtr)OurTag);
    }

    /// <summary>Block until Ctrl, Shift, Alt and Win are up, so a synthetic Ctrl+V isn't read as Win+V.</summary>
    public static void WaitForModifiersReleased(int timeoutMs = 3000)
    {
        int[] keys = [0x10, 0x11, 0x12, 0x5B, 0x5C];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!keys.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0)) return;
            Thread.Sleep(10);
        }
    }

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>Bring our window forward even when another app has focus (call on the UI thread).</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return;
        uint fgThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint me = GetCurrentThreadId();
        // Windows only lets the foreground thread change focus; briefly share its input state.
        bool attached = fgThread != me && AttachThreadInput(fgThread, me, true);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        if (attached) AttachThreadInput(fgThread, me, false);
    }

    public static void UseDarkTitleBar(IntPtr hwnd, int captionRgb)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        int colorref = ((captionRgb & 0xFF) << 16) | (captionRgb & 0xFF00) | ((captionRgb >> 16) & 0xFF);
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref colorref, sizeof(int));
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
