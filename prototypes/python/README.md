# Python prototype

Where AgentDeck started: small scripts that drive a Stream Deck directly (no Elgato software) using
[python-elgato-streamdeck](https://github.com/abcminiuser/python-elgato-streamdeck).

| Script | What it does |
|---|---|
| `paint_demo.py` | Paints numbered, colored keys; pressed keys flash white |
| `mic_waveform.py` | Live microphone waveform across keys 1/6/11; key 15 toggles listening |
| `dictate.py` | Toggle-to-talk dictation: Soniox streaming STT, gpt-6-luna cleanup, paste with Ctrl+V |
| `hotkey.py` | Global Ctrl+Shift+Win hotkey via a low-level keyboard hook |

## Setup

```powershell
python -m venv .venv
.\.venv\Scripts\pip install streamdeck pillow sounddevice numpy websockets pyperclip openai
```

On Windows the library also needs `hidapi.dll` next to the scripts: download `hidapi-win.zip` from the
[libusb/hidapi releases](https://github.com/libusb/hidapi/releases) and copy `x64\hidapi.dll` here.
Quit the Elgato app (and AgentDeck) first; only one program can drive the deck at a time.
