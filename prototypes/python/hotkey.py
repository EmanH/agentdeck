"""Global modifier-only hotkey (e.g. Ctrl+Shift+Win) via a Windows low-level keyboard hook.

RegisterHotKey needs a non-modifier key, so we watch raw key events instead. Keys are never
swallowed; other apps still see them."""

import ctypes
import threading
import time
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

WH_KEYBOARD_LL = 13
WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP = 0x0100, 0x0101, 0x0104, 0x0105
KEYEVENTF_KEYUP = 0x0002
MASK_KEY = 0xE8  # unassigned virtual key; tapping it stops a Win release from opening Start
# Marks keystrokes we synthesise. Other injected input (e.g. Logitech Options mapping a mouse
# button to Ctrl+Shift+Win) must still count, so we can't just ignore LLKHF_INJECTED.
OUR_TAG = 0x53444B31

CTRL = {0x11, 0xA2, 0xA3}
SHIFT = {0x10, 0xA0, 0xA1}
WIN = {0x5B, 0x5C}
MODIFIER_GROUPS = {"ctrl": CTRL, "shift": SHIFT, "win": WIN}

LRESULT = ctypes.c_ssize_t
HOOKPROC = ctypes.WINFUNCTYPE(LRESULT, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", wintypes.DWORD), ("scanCode", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD]
user32.SetWindowsHookExW.restype = wintypes.HHOOK
user32.CallNextHookEx.argtypes = [wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
user32.CallNextHookEx.restype = LRESULT
user32.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.GetAsyncKeyState.restype = ctypes.c_short
user32.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD, ctypes.c_size_t]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE


def tap_key(vk, modifiers=()):
    """Send a key (optionally with held modifiers), tagged so our own hook ignores it."""
    for m in modifiers:
        user32.keybd_event(m, 0, 0, OUR_TAG)
    user32.keybd_event(vk, 0, 0, OUR_TAG)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, OUR_TAG)
    for m in reversed(modifiers):
        user32.keybd_event(m, 0, KEYEVENTF_KEYUP, OUR_TAG)


def wait_for_modifiers_released(timeout=3.0):
    """Block until Ctrl, Shift, Win and Alt are all up, so a synthetic Ctrl+V isn't Win+V."""
    deadline = time.monotonic() + timeout
    keys = [0x10, 0x11, 0x12, 0x5B, 0x5C]
    while time.monotonic() < deadline:
        if not any(user32.GetAsyncKeyState(vk) & 0x8000 for vk in keys):
            return
        time.sleep(0.01)


class ModifierHotkey:
    """Calls on_trigger once each time Ctrl+Shift+Win become held together."""

    def __init__(self, on_trigger):
        self.on_trigger = on_trigger
        self.down = set()
        self.fired = False
        self._proc = HOOKPROC(self._hook)  # keep a reference or the callback gets collected
        threading.Thread(target=self._loop, daemon=True).start()

    def _held(self, group):
        return bool(self.down & group)

    def _hook(self, n_code, w_param, l_param):
        if n_code == 0:
            info = ctypes.cast(l_param, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
            if info.dwExtraInfo != OUR_TAG:
                vk = info.vkCode
                if w_param in (WM_KEYDOWN, WM_SYSKEYDOWN):
                    self.down.add(vk)
                elif w_param in (WM_KEYUP, WM_SYSKEYUP):
                    self.down.discard(vk)

                all_held = all(self._held(g) for g in MODIFIER_GROUPS.values())
                if all_held and not self.fired:
                    self.fired = True
                    tap_key(MASK_KEY)  # Win now counts as "used", so releasing it won't open Start
                    threading.Thread(target=self.on_trigger, daemon=True).start()
                elif not all_held:
                    self.fired = False
        return user32.CallNextHookEx(None, n_code, w_param, l_param)

    def _loop(self):
        hook = user32.SetWindowsHookExW(WH_KEYBOARD_LL, self._proc, kernel32.GetModuleHandleW(None), 0)
        if not hook:
            print(f"Keyboard hook failed (error {ctypes.get_last_error()}); shortcut disabled.")
            return
        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
