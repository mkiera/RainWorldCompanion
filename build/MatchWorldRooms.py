import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import json
import re
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
SAVES = ROOT / "src/RainWorldCompanion.Core/Saves"
TIMELINES = {"Vanilla": "white", "Downpour": "gourmand", "Artificer": "artificer",
             "Spearmaster": "spear", "Rivulet": "rivulet", "Saint": "saint"}


def campaign_matches(selector, timeline):
    selector = selector.lower()
    excluded = selector.startswith("x-")
    names = selector[2:] if excluded else selector
    matches = timeline.lower() in {name.strip() for name in names.split(",")}
    return not matches if excluded else matches


def world_connections(paths, timeline):
    rooms, rules = {}, []
    for path in paths:
        if not path.exists():
            continue
        section = None
        for raw in path.read_text(encoding="utf-8-sig").splitlines():
            line = raw.split("//", 1)[0].strip()
            if line in ("ROOMS", "CONDITIONAL LINKS"):
                section = line
                continue
            if line.startswith("END "):
                section = None
                continue
            condition = re.match(r"^\(([^)]+)\)(.*)$", line)
            if condition:
                if not campaign_matches(condition[1], timeline):
                    continue
                line = condition[2]
            parts = [part.strip().upper() for part in line.split(":")]
            if section == "ROOMS" and len(parts) >= 2:
                rooms[parts[0]] = [name.strip() for name in parts[1].split(",")]
            elif section == "CONDITIONAL LINKS" and len(parts) >= 3:
                rules.append(parts)
    hidden = set()
    for parts in rules:
        applies = campaign_matches(parts[0], timeline)
        if parts[1] == "EXCLUSIVEROOM" and not applies or parts[1] == "HIDEROOM" and applies:
            hidden.add(parts[2])
        elif applies and len(parts) == 4 and parts[1] in rooms:
            links = rooms[parts[1]]
            if parts[2].isdigit():
                index = int(parts[2])
                if index < len(links):
                    links[index] = parts[3]
            else:
                rooms[parts[1]] = [parts[3] if link == parts[2] else link for link in links]
    return {name: [link for link in links if link != "DISCONNECTED" and link not in hidden]
            for name, links in rooms.items() if name not in hidden}


def source_rooms(installation, region, timeline, vanilla):
    code = region.lower()
    directories = [installation / "world" / code]
    if not vanilla:
        directories.append(installation / "mods/moreslugcats/world" / code)
    textures = {}
    connections = {}
    world_paths = [directory / f"world_{code}.txt" for directory in directories]
    if not vanilla:
        world_paths.append(installation / "mods/moreslugcats/modify/world" / code / f"world_{code}.txt")
    connections = world_connections(world_paths, timeline)
    for directory in directories:
        for suffix in ("", f"-{timeline}"):
            metadata = directory / f"map_image_{code}{suffix}.txt"
            texture = directory / f"map_{code}{suffix}.png"
            if not metadata.exists() or not texture.exists():
                continue
            textures = {}
            pixels = np.array(Image.open(texture).convert("RGB"))
            for line in metadata.read_text(encoding="utf-8-sig").splitlines():
                if ":" not in line:
                    continue
                name, values = line.split(":", 1)
                x, y, width, height = map(int, values.split(","))
                crop = pixels[max(0, pixels.shape[0] - y - height):pixels.shape[0] - y, x:x + width]
                mask = ((crop[:, :, 0] > 200) & (crop[:, :, 1] < 100)).astype(np.float32)
                if mask.size and mask.sum() > 5 and mask.std() > 0:
                    textures[name.strip().upper()] = mask
    return textures, connections


def region_target(image, anchors):
    width, height = image.size
    left = max(0, int(min(a["X"] for a in anchors)) - 550)
    top = max(0, int(min(a["Y"] for a in anchors)) - 550)
    right = min(width, int(max(a["X"] for a in anchors)) + 550)
    bottom = min(height, int(max(a["Y"] for a in anchors)) + 550)
    votes = Counter()
    for anchor in anchors:
        x, y = int(anchor["X"]), int(anchor["Y"])
        colors = Counter(image.crop((max(0, x - 65), max(0, y - 65), min(width, x + 65), min(height, y + 65))).getdata())
        colors = [(color, count) for color, count in colors.most_common(30)
                  if max(color) > 45 and max(color) < 250 and max(color) - min(color) > 10]
        for color, count in colors[:2]:
            votes[color] += count
    if not votes:
        raise ValueError("No region terrain color near shelter anchors")
    dominant = np.array(votes.most_common(1)[0][0], dtype=float)
    pixels = np.array(image.crop((left, top, right, bottom))).astype(float)
    norm = np.linalg.norm(pixels, axis=2)
    target = ((norm > 25) & (np.min(pixels, axis=2) < 245)).astype(np.float32)
    return (left, top), target, dominant.astype(int).tolist()


def candidates_for(target, mask, scales, count=5):
    candidates = []
    for scale in scales:
        template = cv2.resize(mask, None, fx=scale, fy=scale, interpolation=cv2.INTER_NEAREST)
        height, width = template.shape
        if height >= target.shape[0] or width >= target.shape[1] or template.std() == 0:
            continue
        scores = cv2.matchTemplate(target, template, cv2.TM_CCOEFF_NORMED)
        for _ in range(count):
            _, score, _, (x, y) = cv2.minMaxLoc(scores)
            candidates.append({"score": round(score, 5), "x": x, "y": y, "scale": scale,
                               "width": width, "height": height})
            scores[max(0, y - height // 2):y + height // 2 + 1,
                   max(0, x - width // 2):x + width // 2 + 1] = -1
    return sorted(candidates, key=lambda c: c["score"], reverse=True)


def center(candidate):
    return np.array([candidate["x"] + candidate["width"] / 2, candidate["y"] + candidate["height"] / 2])


def refine_cached(result, installation, anchors, output, partial=False):
    map_id, region = result["map"], result["region"]
    masks, connections = source_rooms(installation, region, TIMELINES[map_id], map_id == "Vanilla")
    accepted = {name: candidate for name, candidate in result["accepted"].items() if name in masks}
    unmatched = {name: candidates for name, candidates in result["unmatched"].items() if name in masks}
    for _ in range(5):
        for name, candidates in list(unmatched.items()):
            neighbors = [accepted[n] for n in connections.get(name, []) if n in accepted]
            if not neighbors:
                continue
            best = candidates[0]
            distinct = best["score"] >= 0.85 and best["score"] - candidates[1]["score"] >= 0.025
            available = [c for c in candidates if c["score"] >= 0.55 and c["score"] >= best["score"] - 0.12
                         and all(np.linalg.norm(center(c) - center(placed)) > 8 for placed in accepted.values())]
            ranked = sorted(available, key=lambda c: sum(np.linalg.norm(center(c) - center(n)) for n in neighbors) / len(neighbors))
            if not ranked:
                continue
            distances = [sum(np.linalg.norm(center(c) - center(n)) for n in neighbors) / len(neighbors) for c in ranked]
            alternatives = [sum(np.linalg.norm(center(c) - center(n)) for n in neighbors) / len(neighbors)
                            for c in candidates if c != ranked[0]]
            local_match = (ranked[0]["score"] >= 0.55 and distances[0] < 150
                           and alternatives and min(alternatives) - distances[0] > 90)
            if distances[0] < 220 and ((distinct and ranked[0] == best)
                                       or (len(ranked) > 1 and distances[1] - distances[0] > 90)
                                       or (len(ranked) == 1 and ranked[0]["score"] >= 0.8) or local_match):
                accepted[name] = dict(ranked[0], method="terrain-template-connected")
                del unmatched[name]
    image = Image.open(ROOT / f"art/maps/World_Map_({map_id}).png").convert("RGB")
    origin, target, _ = region_target(image, anchors)
    if partial and result["scale"] == 1.0:
        for name in list(unmatched):
            mask = masks[name]
            height, width = mask.shape
            votes = []
            for px, py, pw, ph in ((0, 0, width // 2, height // 2), (width // 2, 0, width - width // 2, height // 2),
                                   (0, height // 2, width // 2, height - height // 2),
                                   (width // 2, height // 2, width - width // 2, height - height // 2)):
                patch = mask[py:py + ph, px:px + pw]
                if patch.size < 100 or patch.sum() < 10 or patch.std() < 0.1:
                    continue
                candidates = candidates_for(target, patch, [result["scale"]], count=2)
                if candidates and candidates[0]["score"] >= 0.85 and candidates[0]["score"] - candidates[1]["score"] > 0.05:
                    votes.append((candidates[0]["x"] - px, candidates[0]["y"] - py))
            consensus = next((vote for vote in votes if sum(np.linalg.norm(np.array(vote) - other) <= 3 for other in votes) >= 2), None)
            if consensus is not None and consensus[0] >= 0 and consensus[1] >= 0 and consensus[0] + width < target.shape[1] and consensus[1] + height < target.shape[0]:
                accepted[name] = {"x": consensus[0], "y": consensus[1], "width": width, "height": height,
                                  "scale": 1.0, "score": None, "method": "terrain-template-parts"}
                del unmatched[name]
    entries = []
    preview = image.crop((origin[0], origin[1], origin[0] + target.shape[1], origin[1] + target.shape[0]))
    draw = ImageDraw.Draw(preview)
    for name, match in accepted.items():
        scaled = cv2.resize(masks[name], (match["width"], match["height"]), interpolation=cv2.INTER_NEAREST)
        ys, xs = np.nonzero(scaled)
        x, y = origin[0] + match["x"] + int(xs.min()), origin[1] + match["y"] + int(ys.min())
        width, height = int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1)
        entries.append({"RoomId": name, "RegionCode": region, "X": x + width / 2, "Y": y + height / 2,
                        "Bounds": [{"X": x, "Y": y, "Width": width, "Height": height}], "MatchKind": match["method"]})
        px, py = x - origin[0], y - origin[1]
        draw.rectangle((px, py, px + width, py + height), outline="lime")
        draw.text((px, py - 10), name, fill="lime")
    result.update(accepted=accepted, unmatched=unmatched, entries=entries,
                  matched=len(accepted), total=len(accepted) + len(unmatched))
    stem = output / f"{map_id}-{region}"
    stem.with_suffix(".json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    preview.save(stem.with_suffix(".png"))
    print(f"{map_id} {region}: refined {len(accepted)}/{result['total']}", flush=True)
    return result


def match_region(installation, map_id, region, anchors, output):
    image = Image.open(ROOT / f"art/maps/World_Map_({map_id}).png").convert("RGB")
    masks, connections = source_rooms(installation, region, TIMELINES[map_id], map_id == "Vanilla")
    origin, target, color = region_target(image, anchors)
    shelters = {a["RoomId"].upper() for a in anchors}
    probes = sorted(((name, mask) for name, mask in masks.items() if name not in shelters),
                    key=lambda pair: pair[1].size, reverse=True)[:6]
    scale_votes = Counter()
    for _, mask in probes:
        candidates = candidates_for(target, mask, [0.5, 1.0, 1.5, 2.0], count=1)
        if candidates and candidates[0]["score"] > 0.9:
            scale_votes[candidates[0]["scale"]] += 1
    scale = scale_votes.most_common(1)[0][0] if scale_votes else 1.0
    found = {}
    accepted = {}
    for name, mask in masks.items():
        if name in shelters:
            continue
        candidates = candidates_for(target, mask, [scale])
        if not candidates:
            continue
        found[name] = candidates
        if candidates[0]["score"] >= 0.85 and candidates[0]["score"] - candidates[1]["score"] >= 0.08:
            accepted[name] = dict(candidates[0], method="terrain-template")
    for _ in range(3):
        for name, candidates in found.items():
            if name in accepted:
                continue
            neighbors = [accepted[n] for n in connections.get(name, []) if n in accepted]
            if not neighbors:
                continue
            eligible = [c for c in candidates if c["score"] >= 0.7 and c["score"] >= candidates[0]["score"] - 0.12]
            ranked = sorted(eligible, key=lambda c: sum(np.linalg.norm(center(c) - center(n)) for n in neighbors) / len(neighbors))
            if not ranked:
                continue
            distances = [sum(np.linalg.norm(center(c) - center(n)) for n in neighbors) / len(neighbors) for c in ranked]
            if distances[0] < 220 * scale and (len(ranked) == 1 or distances[1] - distances[0] > 90 * scale):
                accepted[name] = dict(ranked[0], method="terrain-template-connected")
    entries = []
    preview = image.crop((origin[0], origin[1], origin[0] + target.shape[1], origin[1] + target.shape[0]))
    draw = ImageDraw.Draw(preview)
    for name, match in accepted.items():
        scaled = cv2.resize(masks[name], (match["width"], match["height"]), interpolation=cv2.INTER_NEAREST)
        ys, xs = np.nonzero(scaled)
        x = origin[0] + match["x"] + int(xs.min())
        y = origin[1] + match["y"] + int(ys.min())
        width, height = int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1)
        entries.append({"RoomId": name, "RegionCode": region, "X": x + width / 2, "Y": y + height / 2,
                        "Bounds": [{"X": x, "Y": y, "Width": width, "Height": height}], "MatchKind": match["method"]})
        px, py = x - origin[0], y - origin[1]
        draw.rectangle((px, py, px + width, py + height), outline="lime")
        draw.text((px, py - 10), name, fill="lime")
    stem = output / f"{map_id}-{region}"
    preview.save(stem.with_suffix(".png"))
    details = {"map": map_id, "region": region, "color": color, "scale": scale,
               "matched": len(accepted), "total": len(found), "accepted": accepted,
               "unmatched": {name: candidates[:3] for name, candidates in found.items() if name not in accepted}, "entries": entries}
    stem.with_suffix(".json").write_text(json.dumps(details, indent=2), encoding="utf-8")
    print(f"{map_id} {region}: {len(accepted)}/{len(found)}, scale {scale}, color {color}", flush=True)
    return details


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--installation", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--maps", nargs="+", default=list(TIMELINES))
    parser.add_argument("--regions", nargs="+")
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--refine", action="store_true")
    parser.add_argument("--partial", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    cv2.setNumThreads(1)
    tasks = []
    for map_id in args.maps:
        dens = json.loads((SAVES / f"{map_id}Dens.json").read_text(encoding="utf-8-sig"))
        for region in sorted({a["RegionCode"] for a in dens}):
            if not args.regions or region in args.regions:
                tasks.append((map_id, region, [a for a in dens if a["RegionCode"] == region]))
    def run(task):
        cached = args.output / f"{task[0]}-{task[1]}.json"
        if args.resume and cached.exists():
            result = json.loads(cached.read_text(encoding="utf-8"))
            return refine_cached(result, args.installation, task[2], args.output, args.partial) if args.refine else result
        return match_region(args.installation, *task, args.output)
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        results = list(pool.map(run, tasks))
    if args.apply:
        overrides_path = SAVES / "RoomMapOverrides.json"
        overrides = json.loads(overrides_path.read_text(encoding="utf-8"))
        catalog_path = SAVES / "RoomMapCatalog.json"
        catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
        for result in results:
            map_id = result["map"]
            existing = {entry["RoomId"].upper(): entry for entry in catalog[map_id]}
            corrected = {entry["RoomId"].upper(): entry for entry in overrides.get(map_id, [])}
            for entry in result["entries"]:
                name = entry["RoomId"].upper()
                if name in existing:
                    if existing[name]["MatchKind"] == "den-anchor":
                        continue
                    entry["RoomId"] = existing[name]["RoomId"]
                if name not in corrected:
                    corrected[name] = entry
            existing.update(corrected)
            catalog[map_id] = sorted(existing.values(), key=lambda entry: (entry["RegionCode"], entry["RoomId"]))
            overrides[map_id] = sorted(corrected.values(), key=lambda entry: (entry["RegionCode"], entry["RoomId"]))
        for path, data in ((overrides_path, overrides), (catalog_path, catalog)):
            path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
