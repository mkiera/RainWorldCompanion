import argparse
import json
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--world", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    args.output.mkdir(parents=True, exist_ok=True)
    composite = Image.open(root / "art/maps/World_Map_(Downpour).png").convert("RGB")
    origin = (4900, 1850)
    area = composite.crop((*origin, 6500, 2850))
    pixels = np.array(area)
    target = ((pixels[:, :, 1] > pixels[:, :, 0] * 1.5)
              & (np.abs(pixels[:, :, 2].astype(float) - pixels[:, :, 1]) < 15)
              & (pixels[:, :, 1] > 90)).astype(np.float32)
    source = np.array(Image.open(args.world / "hi/map_hi.png").convert("RGB"))
    rooms = []
    for line in (args.world / "hi/map_image_hi.txt").read_text().splitlines():
        name, rectangle = line.split(":")
        x, y, width, height = map(int, rectangle.split(","))
        crop = source[source.shape[0] - y - height:source.shape[0] - y, x:x + width]
        mask = ((crop[:, :, 0] > 200) & (crop[:, :, 1] < 100)).astype(np.float32)
        candidates = []
        for scale in np.arange(0.8, 1.61, 0.05):
            template = cv2.resize(mask, None, fx=scale, fy=scale, interpolation=cv2.INTER_NEAREST)
            scores = cv2.matchTemplate(target, template, cv2.TM_CCOEFF_NORMED)
            for _ in range(6):
                _, score, _, position = cv2.minMaxLoc(scores)
                px, py = position
                candidates.append({"score": round(score, 4), "x": px + origin[0], "y": py + origin[1],
                                   "width": template.shape[1], "height": template.shape[0], "scale": round(float(scale), 2)})
                scores[max(0, py - height // 2):py + height // 2 + 1,
                       max(0, px - width // 2):px + width // 2 + 1] = -1
        candidates.sort(key=lambda item: item["score"], reverse=True)
        best = candidates[0]
        alternative = next((item for item in candidates if abs(item["x"] - best["x"]) > width / 2
                            or abs(item["y"] - best["y"]) > height / 2), candidates[-1])
        ys, xs = np.nonzero(mask)
        rooms.append({"room": name, "best": best, "margin": round(best["score"] - alternative["score"], 4),
                      "terrain_bounds": [int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)],
                      "candidates": candidates})
    connections = {}
    for line in (args.world / "hi/world_hi.txt").read_text().split("END ROOMS")[0].splitlines()[1:]:
        parts = line.split(":")
        if len(parts) >= 2:
            connections[parts[0].strip()] = [name.strip() for name in parts[1].split(",") if name.strip() != "DISCONNECTED"]
    accepted = {room["room"]: room for room in rooms if not room["room"].startswith("HI_S")
                and room["best"]["score"] >= 0.85 and room["margin"] >= 0.08 and room["best"]["scale"] == 1}
    def center(candidate):
        return np.array([candidate["x"] + candidate["width"] / 2, candidate["y"] + candidate["height"] / 2])
    for room in rooms:
        if not room["room"].startswith("GATE_") or room["room"] in accepted:
            continue
        neighbors = [accepted[name]["best"] for name in connections.get(room["room"], []) if name in accepted]
        if not neighbors:
            continue
        candidates = [candidate for candidate in room["candidates"] if candidate["scale"] == 1 and candidate["score"] >= 0.8]
        ranked = sorted(candidates, key=lambda candidate: min(np.linalg.norm(center(candidate) - center(neighbor)) for neighbor in neighbors))
        if len(ranked) < 2:
            continue
        distances = [min(np.linalg.norm(center(candidate) - center(neighbor)) for neighbor in neighbors) for candidate in ranked]
        if distances[0] < 200 and distances[1] - distances[0] > 100:
            room["best"] = ranked[0]
            room["connection_distance"] = round(float(distances[0]), 2)
            accepted[room["room"]] = room
    catalog_path = root / "src/RainWorldCompanion.Core/Saves/RoomMapCatalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    old = {room["RoomId"].upper(): room for room in catalog["Downpour"]}
    overrides = []
    before = composite.crop((*origin, 6500, 2850))
    before_draw = ImageDraw.Draw(before)
    for name, room in accepted.items():
        best = room["best"]
        left, top, right, bottom = room["terrain_bounds"]
        x, y = best["x"] + left, best["y"] + top
        width, height = right - left, bottom - top
        prior = old.get(name)
        if prior:
            room["center_correction"] = round(float(np.linalg.norm(np.array([x + width / 2, y + height / 2]) - np.array([prior["X"], prior["Y"]]))), 2)
            for bounds in prior["Bounds"]:
                bx, by = bounds["X"] - origin[0], bounds["Y"] - origin[1]
                before_draw.rectangle((bx, by, bx + bounds["Width"], by + bounds["Height"]), outline="orange")
        overrides.append({"RoomId": prior["RoomId"] if prior else name, "RegionCode": "HI",
                          "X": x + width / 2, "Y": y + height / 2,
                          "Bounds": [{"X": x, "Y": y, "Width": width, "Height": height}],
                          "MatchKind": "terrain-template-connected" if "connection_distance" in room else "terrain-template"})
    before.save(args.output / "previous-placements.png")
    (args.output / "overrides.json").write_text(json.dumps({"Downpour": overrides}, indent=2) + "\n", encoding="utf-8")
    if args.apply:
        override_path = catalog_path.with_name("RoomMapOverrides.json")
        saved = json.loads(override_path.read_text(encoding="utf-8")) if override_path.exists() else {}
        existing = {room["RoomId"].upper(): room for room in saved.get("Downpour", [])}
        existing.update({room["RoomId"].upper(): room for room in overrides})
        saved["Downpour"] = list(existing.values())
        override_path.write_text(json.dumps(saved, indent=2) + "\n", encoding="utf-8")
        old.update({room["RoomId"].upper(): room for room in overrides})
        catalog["Downpour"] = sorted(old.values(), key=lambda room: (room["RegionCode"], room["RoomId"]))
        catalog_path.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
    (args.output / "matches.json").write_text(json.dumps(rooms, indent=2), encoding="utf-8")
    draw = ImageDraw.Draw(area)
    for room in accepted.values():
        best = room["best"]
        x, y = best["x"] - origin[0], best["y"] - origin[1]
        color = "lime"
        left, top, right, bottom = room["terrain_bounds"]
        draw.rectangle((x + left, y + top, x + right, y + bottom), outline=color)
        draw.text((x, y - 10), room["room"], fill=color)
        print(room["room"], best, "margin", room["margin"])
    area.save(args.output / "matches.png")
    print("Accepted", len(accepted), "of", len(rooms), "rooms. Shelter anchors unchanged.")


if __name__ == "__main__":
    main()
