import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw


def render(background, rooms, color, destination):
    image = Image.open(background).convert("RGB")
    draw = ImageDraw.Draw(image)
    for room in rooms:
        outline = "orange" if color == "lime" and room["MatchKind"].startswith("reference-affine") else color
        for bounds in room["Bounds"]:
            x, y = bounds["X"], bounds["Y"]
            draw.rectangle((x, y, x + bounds["Width"], y + bounds["Height"]), outline=outline, width=1)
        if room["Bounds"]:
            x, y = room["X"], room["Y"]
            draw.line((x - 3, y, x + 3, y), fill=outline)
            draw.line((x, y - 3, x, y + 3), fill=outline)
    image.save(destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--map", default="Downpour")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    args.output.mkdir(parents=True, exist_ok=True)
    background = root / f"art/maps/World_Map_({args.map}).png"
    for label, path, color in (("before", args.before, "orange"), ("after", args.after, "lime")):
        rooms = json.loads(path.read_text(encoding="utf-8"))[args.map]
        destination = args.output / f"{args.map.lower()}-{label}.png"
        render(background, rooms, color, destination)
        with Image.open(destination) as image:
            print(destination, image.size)


if __name__ == "__main__":
    main()
