import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw

from MatchWorldRooms import ROOT, SAVES, TIMELINES, candidates_for, source_rooms


def trim(mask):
    ys, xs = np.nonzero(mask)
    return mask[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def position(entry):
    return np.array([entry["X"], entry["Y"]])


def terrain(image):
    pixels = np.asarray(image)
    return ((pixels.max(axis=2) > 25) & (pixels.min(axis=2) < 245)).astype(np.float32)


def rectangle(mask, x, y, name, region, method):
    height, width = mask.shape
    return {"RoomId": name, "RegionCode": region, "X": x + width / 2, "Y": y + height / 2,
            "Bounds": [{"X": x, "Y": y, "Width": width, "Height": height}],
            "MatchKind": "terrain-template-" + method}


def patch_consensus(target, mask):
    height, width = mask.shape
    votes = []
    for divisions in (3, 4):
        current = []
        for row in range(divisions):
            for column in range(divisions):
                x, right = column * width // divisions, (column + 1) * width // divisions
                y, bottom = row * height // divisions, (row + 1) * height // divisions
                patch = mask[y:bottom, x:right]
                if min(patch.shape) < 8 or patch.size < 180 or patch.sum() < 25 or patch.std() < 0.2:
                    continue
                candidates = candidates_for(target, patch, [1.0], count=2)
                if len(candidates) == 2 and candidates[0]["score"] >= 0.92 and candidates[0]["score"] - candidates[1]["score"] >= 0.07:
                    current.append((candidates[0]["x"] - x, candidates[0]["y"] - y, patch.size))
        for x, y, _ in current:
            supporters = [v for v in current if np.linalg.norm(np.array(v[:2]) - (x, y)) <= 2]
            if len(supporters) >= 3 and sum(v[2] for v in supporters) >= mask.size * 0.25:
                votes.append((int(round(np.median([v[0] for v in supporters]))),
                              int(round(np.median([v[1] for v in supporters]))), len(supporters)))
    if not votes or any(np.linalg.norm(np.array(v[:2]) - votes[0][:2]) > 3 for v in votes):
        return None
    x, y, count = max(votes, key=lambda v: v[2])
    if x < 0 or y < 0 or x + width > target.shape[1] or y + height > target.shape[0]:
        return None
    return x, y, count


def local_translation(pairs):
    if len(pairs) < 3:
        return None
    deltas = np.array([target - source for source, target in pairs])
    best = max((np.linalg.norm(deltas - delta, axis=1) <= 3 for delta in deltas), key=np.sum)
    if best.sum() < 3 or best.sum() < len(pairs) * 0.75:
        return None
    sources = np.array([source for source, _ in pairs])[best]
    if np.linalg.norm(sources.max(axis=0) - sources.min(axis=0)) < 50:
        return None
    return np.median(deltas[best], axis=0), int(best.sum())


def apply_reviewed(output):
    before = json.loads((output / "before.json").read_text())
    current = json.loads((SAVES / "RoomMapCatalog.json").read_text())
    if current != before:
        raise ValueError("Catalog changed since matching. Run the pass again before applying.")
    evidence = json.loads((output / "results.json").read_text())["resolved"]
    omissions = json.loads((SAVES / "RoomMapOmissions.json").read_text())
    overrides = json.loads((SAVES / "RoomMapOverrides.json").read_text())
    for map_id in TIMELINES:
        rooms = {e["RoomId"].upper(): e for e in current[map_id]}
        corrections = {e["RoomId"].upper(): e for e in overrides[map_id]}
        for change in evidence:
            if change["map"] != map_id:
                continue
            name = change["room"]
            if name in omissions.get(map_id, {}):
                continue
            old = rooms.get(name)
            if old and (old["MatchKind"] == "den-anchor" or old["MatchKind"].startswith("terrain-template")):
                raise ValueError(f"Refusing to replace verified placement: {map_id} {name}")
            rooms[name] = change["entry"]
            corrections[name] = change["entry"]
        current[map_id] = sorted(rooms.values(), key=lambda e: (e["RegionCode"], e["RoomId"]))
        overrides[map_id] = sorted(corrections.values(), key=lambda e: (e["RegionCode"], e["RoomId"]))
    (SAVES / "RoomMapCatalog.json").write_text(json.dumps(current, indent=2) + "\n")
    (SAVES / "RoomMapOverrides.json").write_text(json.dumps(overrides, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--installation", type=Path, required=True)
    parser.add_argument("--reports", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--apply-reviewed", action="store_true")
    args = parser.parse_args()
    if args.apply_reviewed:
        apply_reviewed(args.output)
        return
    args.output.mkdir(parents=True, exist_ok=True)
    cv2.setNumThreads(1)
    catalog = json.loads((SAVES / "RoomMapCatalog.json").read_text())
    (args.output / "before.json").write_text(json.dumps(catalog, indent=2))
    entries = {m: {e["RoomId"].upper(): e for e in rows} for m, rows in catalog.items()}
    fixed = {m: {n: e for n, e in rows.items() if e["MatchKind"].startswith("terrain-template")}
             for m, rows in entries.items()}
    masks, graphs, pending = {}, defaultdict(lambda: defaultdict(set)), {}
    for path in sorted(args.reports.glob("*.json")):
        report = json.loads(path.read_text())
        map_id, region = report["map"], report["region"]
        raw, links = source_rooms(args.installation, region, TIMELINES[map_id], map_id == "Vanilla")
        masks[map_id, region] = {name: trim(mask) for name, mask in raw.items() if name in links}
        for name, neighbors in links.items():
            graphs[map_id][name].update(neighbors)
            for neighbor in neighbors:
                graphs[map_id][neighbor].add(name)
        for name in report["unmatched"]:
            if name not in fixed[map_id] and entries[map_id].get(name, {}).get("MatchKind") != "den-anchor":
                pending.setdefault((map_id, name), []).append(region)
    evidence, excluded = [], []
    for key, regions in list(pending.items()):
        available = [region for region in regions if key[1] in masks[key[0], region]]
        if not available:
            excluded.append({"map": key[0], "room": key[1], "reason": "No active room with source geometry"})
            del pending[key]
        else:
            pending[key] = available
    images = {}

    def accept(map_id, name, entry, details):
        if name in entries[map_id]:
            entry["RoomId"] = entries[map_id][name]["RoomId"]
        entries[map_id][name] = entry
        fixed[map_id][name] = entry
        evidence.append(dict(map=map_id, room=name, entry=entry, **details))
        del pending[map_id, name]

    for map_id in TIMELINES:
        image = Image.open(ROOT / f"art/maps/World_Map_({map_id}).png").convert("RGB")
        images[map_id] = image
        for (current, name), regions in list(pending.items()):
            if current != map_id:
                continue
            region = regions[0]
            mask = masks[map_id, region][name]
            known = [e for e in entries[map_id].values() if e["RegionCode"] in regions]
            left = max(0, int(min(e["X"] for e in known)) - 950)
            top = max(0, int(min(e["Y"] for e in known)) - 950)
            right = min(image.width, int(max(e["X"] for e in known)) + 950)
            bottom = min(image.height, int(max(e["Y"] for e in known)) + 950)
            target = terrain(image.crop((left, top, right, bottom)))
            candidates = candidates_for(target, mask, [1.0], count=8)
            neighbors = [fixed[map_id][n] for n in graphs[map_id][name] if n in fixed[map_id]]
            ranked = []
            for candidate in candidates:
                point = np.array([left + candidate["x"] + mask.shape[1] / 2,
                                  top + candidate["y"] + mask.shape[0] / 2])
                if candidate["score"] < 0.85 or any(np.linalg.norm(point - position(e)) < 8 for e in fixed[map_id].values()):
                    continue
                distances = [np.linalg.norm(point - position(e)) for e in neighbors]
                if distances:
                    ranked.append((float(np.mean(distances)), max(distances), candidate))
            ranked.sort(key=lambda item: item[0])
            if len(neighbors) >= 2 and ranked and ranked[0][1] < 350 and (len(ranked) == 1 or ranked[1][0] - ranked[0][0] >= 90):
                candidate = ranked[0][2]
                accept(map_id, name, rectangle(mask, left + candidate["x"], top + candidate["y"], name, region, "graph"),
                       {"method": "connections", "neighbors": [e["RoomId"] for e in neighbors], "score": candidate["score"]})
                continue
            consensus = patch_consensus(target, mask)
            if consensus:
                x, y, count = consensus
                point = np.array([left + x + mask.shape[1] / 2, top + y + mask.shape[0] / 2])
                if any(np.linalg.norm(point - position(e)) < 8 for e in fixed[map_id].values()):
                    continue
                accept(map_id, name, rectangle(mask, left + x, top + y, name, region, "patches"),
                       {"method": "partial shapes", "agreeing_patches": count})
        print(map_id, Counter(e["method"] for e in evidence if e["map"] == map_id), flush=True)

    for (map_id, name), regions in list(pending.items()):
        proposals = []
        for region in regions:
            mask = masks[map_id, region][name]
            for other in TIMELINES:
                reference = fixed[other].get(name)
                other_masks = masks.get((other, region), {})
                if other == map_id or reference is None or name not in other_masks or not np.array_equal(mask, other_masks[name]):
                    continue
                pairs = []
                for neighbor, source in fixed[other].items():
                    destination = fixed[map_id].get(neighbor)
                    if destination is None or neighbor == name or neighbor not in other_masks or neighbor not in masks[map_id, region]:
                        continue
                    if np.linalg.norm(position(source) - position(reference)) > 500:
                        continue
                    if np.array_equal(other_masks[neighbor], masks[map_id, region][neighbor]):
                        pairs.append((position(source), position(destination)))
                translation = local_translation(pairs)
                if translation is None:
                    continue
                delta, count = translation
                point = position(reference) + delta
                x, y = np.rint(point - np.array(mask.shape[::-1]) / 2).astype(int)
                image = images[map_id]
                if x < 0 or y < 0 or x + mask.shape[1] > image.width or y + mask.shape[0] > image.height:
                    continue
                patch = terrain(image.crop((x, y, x + mask.shape[1], y + mask.shape[0])))
                score = float(cv2.matchTemplate(patch, mask, cv2.TM_CCOEFF_NORMED)[0, 0])
                if score >= 0.85 and all(np.linalg.norm(point - position(e)) >= 8 for e in fixed[map_id].values()):
                    proposals.append((x, y, region, other, count, score))
        if proposals and all(np.linalg.norm(np.array(p[:2]) - proposals[0][:2]) <= 3 for p in proposals):
            x, y, region, other, count, score = max(proposals, key=lambda p: p[4])
            accept(map_id, name, rectangle(masks[map_id, region][name], int(x), int(y), name, region, "transfer"),
                   {"method": "timeline transfer", "source_map": other, "supporting_rooms": count, "score": score})

    result = {"resolved": evidence, "excluded": excluded,
              "remaining": [{"map": m, "room": n, "regions": r} for (m, n), r in pending.items()]}
    (args.output / "results.json").write_text(json.dumps(result, indent=2))
    for change in evidence:
        entry, map_id = change["entry"], change["map"]
        x, y = int(entry["X"]), int(entry["Y"])
        image = images[map_id]
        left, top = max(0, x - 220), max(0, y - 180)
        preview = image.crop((left, top, min(image.width, x + 220), min(image.height, y + 180)))
        draw = ImageDraw.Draw(preview)
        for e in fixed[map_id].values():
            if abs(e["X"] - x) > 300 or abs(e["Y"] - y) > 260:
                continue
            color = "lime" if e["RoomId"] == entry["RoomId"] else "cyan"
            for b in e["Bounds"]:
                bx, by = b["X"] - left, b["Y"] - top
                draw.rectangle((bx, by, bx + b["Width"], by + b["Height"]), outline=color)
                draw.text((bx, by - 11), e["RoomId"], fill=color)
        preview.save(args.output / f"{map_id}-{entry['RoomId']}.png")
    after = {m: sorted(rows.values(), key=lambda e: (e["RegionCode"], e["RoomId"])) for m, rows in entries.items()}
    (args.output / "after.json").write_text(json.dumps(after, indent=2) + "\n")
    if args.apply:
        apply_reviewed(args.output)
    print(Counter(e["method"] for e in evidence), "remaining", len(pending), "excluded", len(excluded), flush=True)


if __name__ == "__main__":
    main()
