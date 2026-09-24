"""Live microphone waveform on the Stream Deck's left column; key 15 toggles listening."""

import os
import threading
import time
from collections import deque
from pathlib import Path

# The StreamDeck library finds hidapi.dll via PATH, so expose the copy in this folder.
HERE = Path(__file__).resolve().parent
os.environ["PATH"] = str(HERE) + os.pathsep + os.environ.get("PATH", "")

import numpy as np
import sounddevice as sd
from PIL import Image, ImageDraw, ImageFont
from StreamDeck.DeviceManager import DeviceManager
from StreamDeck.ImageHelpers import PILHelper

WAVE_KEYS = [0, 5, 10]    # keys 1, 6, 11 (top to bottom)
TOGGLE_KEY = 14           # key 15, bottom right
KEY_GAP = 18              # virtual pixels for the bezel between keys, keeps the wave continuous
FPS = 30
BAR_COUNT = 7
FLOOR_DB, CEIL_DB = -55.0, -12.0   # mic level mapped onto 0..1
WAVE_COLOR = (0, 220, 255)


def load_font(size):
    try:
        return ImageFont.truetype("arialbd.ttf", size)
    except OSError:
        return ImageFont.load_default(size)


class MicLevel:
    """Streams the default mic and exposes a smoothed 0..1 loudness."""

    def __init__(self):
        self.level = 0.0
        self.stream = None

    def _callback(self, indata, frames, time_info, status):
        rms = float(np.sqrt(np.mean(np.square(indata))))
        db = 20 * np.log10(rms + 1e-9)
        target = min(max((db - FLOOR_DB) / (CEIL_DB - FLOOR_DB), 0.0), 1.0)
        # Fast attack, slower release so the bars bounce rather than flicker.
        rate = 0.6 if target > self.level else 0.15
        self.level += (target - self.level) * rate

    @property
    def running(self):
        return self.stream is not None

    def start(self):
        if not self.stream:
            self.stream = sd.InputStream(channels=1, blocksize=1024, callback=self._callback)
            self.stream.start()

    def stop(self):
        if self.stream:
            self.stream.stop()
            self.stream.close()
            self.stream = None
            self.level = 0.0


class WaveformDeck:
    def __init__(self, deck):
        self.deck = deck
        self.mic = MicLevel()
        self.key_w, self.key_h = deck.key_image_format()["size"]
        self.canvas_h = self.key_h * len(WAVE_KEYS) + KEY_GAP * (len(WAVE_KEYS) - 1)
        self.history = deque([0.0] * BAR_COUNT, maxlen=BAR_COUNT)
        self.black = PILHelper.to_native_key_format(deck, PILHelper.create_key_image(deck))
        self.stop_event = threading.Event()

    # --- drawing -------------------------------------------------------------

    def draw_waveform(self):
        """Render the tall canvas: mirrored bars, newest level in the centre, older levels outward."""
        canvas = Image.new("RGB", (self.key_w, self.canvas_h), "black")
        draw = ImageDraw.Draw(canvas)
        levels = list(self.history)
        centre = BAR_COUNT // 2
        slot = self.key_w / BAR_COUNT
        bar_w = max(int(slot * 0.6), 3)
        mid_y = self.canvas_h / 2
        t = time.monotonic()
        for i in range(BAR_COUNT):
            age = abs(i - centre)
            level = levels[-1 - age]
            # A little per-bar wobble so it reads as a waveform, not a meter.
            wobble = 0.85 + 0.15 * np.sin(t * 9 + i * 1.7)
            half = max(level * wobble * (1 - age * 0.12), 0.015) * (self.canvas_h / 2 - 4)
            x0 = int(i * slot + (slot - bar_w) / 2)
            draw.rounded_rectangle(
                (x0, int(mid_y - half), x0 + bar_w, int(mid_y + half)),
                radius=bar_w // 2, fill=WAVE_COLOR,
            )
        return canvas

    def push_waveform(self, canvas):
        for n, key in enumerate(WAVE_KEYS):
            top = n * (self.key_h + KEY_GAP)
            tile = canvas.crop((0, top, self.key_w, top + self.key_h))
            with self.deck:
                self.deck.set_key_image(key, PILHelper.to_native_key_format(self.deck, tile))

    def draw_toggle(self):
        on = self.mic.running
        image = PILHelper.create_key_image(self.deck)
        w, h = image.size
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((2, 2, w - 3, h - 3), radius=10, fill=(0, 150, 60) if on else (70, 70, 70))
        # Simple microphone glyph.
        cx = w // 2
        draw.rounded_rectangle((cx - 7, 10, cx + 7, 34), radius=7, fill="white")
        draw.arc((cx - 13, 18, cx + 13, 42), start=0, end=180, fill="white", width=3)
        draw.line((cx, 42, cx, 48), fill="white", width=3)
        if not on:
            draw.line((cx - 16, 12, cx + 16, 46), fill=(230, 60, 60), width=4)
        draw.text((cx, 60), "ON" if on else "OFF", font=load_font(14), fill="white", anchor="mm")
        with self.deck:
            self.deck.set_key_image(TOGGLE_KEY, PILHelper.to_native_key_format(self.deck, image))

    def clear_waveform(self):
        with self.deck:
            for key in WAVE_KEYS:
                self.deck.set_key_image(key, self.black)

    # --- behaviour -----------------------------------------------------------

    def on_key(self, deck, key, pressed):
        if key != TOGGLE_KEY or not pressed:
            return
        if self.mic.running:
            self.mic.stop()
            self.clear_waveform()
            print("Listening: off")
        else:
            self.mic.start()
            print("Listening: on")
        self.draw_toggle()

    def run(self):
        with self.deck:
            for key in range(self.deck.key_count()):
                self.deck.set_key_image(key, self.black)
        self.draw_toggle()
        self.deck.set_key_callback(self.on_key)

        frame = 1 / FPS
        while not self.stop_event.is_set():
            start = time.monotonic()
            if self.mic.running:
                self.history.append(self.mic.level)
                self.push_waveform(self.draw_waveform())
            self.stop_event.wait(max(frame - (time.monotonic() - start), 0))

    def close(self):
        self.stop_event.set()
        self.mic.stop()


def main():
    decks = DeviceManager().enumerate()
    if not decks:
        print("No Stream Deck found.")
        return

    deck = decks[0]
    deck.open()
    deck.reset()
    deck.set_brightness(70)
    print(f"Opened {deck.deck_type()} ({deck.key_count()} keys). "
          f"Mic: {sd.query_devices(kind='input')['name']}")
    print("Press key 15 (bottom right) to toggle listening. Ctrl+C to quit.")

    app = WaveformDeck(deck)
    try:
        app.run()
    except KeyboardInterrupt:
        pass
    finally:
        app.close()
        with deck:
            deck.reset()
            deck.close()


if __name__ == "__main__":
    main()
