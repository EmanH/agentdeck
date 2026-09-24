"""Paint a rainbow of numbered keys on the Stream Deck; pressed keys flash white."""

import colorsys
import os
import threading
from pathlib import Path

# The StreamDeck library finds hidapi.dll via PATH, so expose the copy in this folder.
HERE = Path(__file__).resolve().parent
os.environ["PATH"] = str(HERE) + os.pathsep + os.environ.get("PATH", "")

from PIL import Image, ImageDraw, ImageFont
from StreamDeck.DeviceManager import DeviceManager
from StreamDeck.ImageHelpers import PILHelper


def load_font(size):
    try:
        return ImageFont.truetype("arialbd.ttf", size)
    except OSError:
        return ImageFont.load_default(size)


def key_color(key, count):
    r, g, b = colorsys.hsv_to_rgb(key / count, 0.85, 0.95)
    return int(r * 255), int(g * 255), int(b * 255)


def render_key(deck, key, pressed):
    image = PILHelper.create_key_image(deck)
    w, h = image.size
    draw = ImageDraw.Draw(image)
    bg = (255, 255, 255) if pressed else key_color(key, deck.key_count())
    fg = (0, 0, 0) if pressed else (255, 255, 255)
    draw.rounded_rectangle((2, 2, w - 3, h - 3), radius=10, fill=bg)
    draw.text((w / 2, h / 2), str(key + 1), font=load_font(30), fill=fg, anchor="mm")
    return PILHelper.to_native_key_format(deck, image)


def on_key(deck, key, pressed):
    print(f"Key {key + 1} {'pressed' if pressed else 'released'}")
    with deck:
        deck.set_key_image(key, render_key(deck, key, pressed))


def main():
    decks = DeviceManager().enumerate()
    if not decks:
        print("No Stream Deck found.")
        return

    deck = decks[0]
    deck.open()
    deck.reset()
    deck.set_brightness(70)
    print(f"Opened {deck.deck_type()} (serial {deck.get_serial_number()}), "
          f"{deck.key_count()} keys, firmware {deck.get_firmware_version()}")

    for key in range(deck.key_count()):
        deck.set_key_image(key, render_key(deck, key, False))

    deck.set_key_callback(on_key)
    print("Press keys on the deck. Ctrl+C to quit.")
    try:
        threading.Event().wait()
    except KeyboardInterrupt:
        pass
    finally:
        with deck:
            deck.reset()
            deck.close()


if __name__ == "__main__":
    main()
