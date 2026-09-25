import argparse
import csv
import json
import math
import heapq
import gzip
import subprocess
from collections import defaultdict
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw

from MatchWorldRooms import ROOT, SAVES, TIMELINES, world_connections


REGIONS = {
    "CC": "CHIMNEY CANOPY", "CL": "SILENT CONSTRUCT", "DM": "LOOKS TO THE MOON",
    "DS": "DRAINAGE SYSTEM", "GW": "GARBAGE WASTES", "HI": "INDUSTRIAL COMPLEX",
    "HR": "RUBICON", "LC": "METROPOLIS", "LF": "FARM ARRAYS", "LM": "WATERFRONT FACILITY",
    "MS": "SUBMERGED SUPERSTRUCTURE", "OE": "OUTER EXPANSE", "RM": "THE ROT",
    "SB": "SUBTERRANEAN", "SH": "SHADED CITADEL", "SI": "SKY ISLANDS", "SL": "SHORELINE",
    "SS": "FIVE PEBBLES", "SU": "OUTSKIRTS", "UG": "UNDERGROWTH", "UW": "THE EXTERIOR",
    "VS": "PIPEYARD",
}
SAINT_REGIONS = {"CC": "SOLITARY TOWERS", "GW": "GLACIAL WASTELAND", "HI": "ICY MONUMENT",
                 "LF": "DESOLATE FIELDS", "SB": "PRIMORDIAL UNDERGROUND", "SI": "WINDSWEPT SPIRES",
                 "SL": "FRIGID COAST", "SU": "SUBURBAN DRIFTS", "VS": "BARREN CONDUITS"}


def room_rectangles(room):
    return room["Bounds"] or [{"X": room["X"] - 12, "Y": room["Y"] - 12, "Width": 24, "Height": 24}]


def rectangle_slice(rect, shape, padding=0):
    x = max(0, math.floor(rect["X"]) - padding)
    y = max(0, math.floor(rect["Y"]) - padding)
    right = min(shape[1], math.ceil(rect["X"] + rect["Width"]) + padding)
    bottom = min(shape[0], math.ceil(rect["Y"] + rect["Height"]) + padding)
    return slice(y, bottom), slice(x, right)


def read_labels(pixels, map_id, output, tesseract):
    rows = []
    gray = cv2.cvtColor(pixels, cv2.COLOR_RGB2GRAY)
    binary = 255 - cv2.threshold(gray, 130, 255, cv2.THRESH_BINARY)[1]
    tiles = [(x, y, 1700, mode) for mode in ("gray", "color") for y in range(0, pixels.shape[0], 1500) for x in range(0, pixels.shape[1], 1500)]
    tiles.extend((0, 0, max(pixels.shape), mode) for mode in ("gray", "color"))
    color_binary = 255 - cv2.threshold(pixels.max(axis=2), 130, 255, cv2.THRESH_BINARY)[1]
    for x, y, size, mode in tiles:
        suffix = "" if mode == "gray" else "-color"
        stem = output / f"{map_id}-{x}-{y}-{size}{suffix}"
        image_path = stem.with_suffix(".png")
        tsv_path = stem.with_suffix(".tsv")
        if not tsv_path.exists():
            source = binary if mode == "gray" else color_binary
            cv2.imwrite(str(image_path), source[y:y + size, x:x + size])
            subprocess.run([str(tesseract), str(image_path), str(stem), "--psm", "11", "tsv"],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        with tsv_path.open(encoding="utf-8") as stream:
            blocks = defaultdict(list)
            for row in csv.DictReader(stream, delimiter="\t", quoting=csv.QUOTE_NONE):
                if row["level"] == "5" and row["text"].strip():
                    blocks[("line", row["block_num"], row["par_num"], row["line_num"])].append(row)
                    blocks[("block", row["block_num"])].append(row)
            for words in blocks.values():
                text = " ".join(w["text"] for w in words).upper()
                left = min(int(w["left"]) for w in words) + x
                top = min(int(w["top"]) for w in words) + y
                right = max(int(w["left"]) + int(w["width"]) for w in words) + x
                bottom = max(int(w["top"]) + int(w["height"]) for w in words) + y
                rows.append({"Text": text, "X": left, "Y": top, "Width": right - left,
                             "Height": bottom - top, "Confidence": sum(float(w["conf"]) for w in words) / len(words),
                             "Lines": len({w["line_num"] for w in words}),
                             "Words": [{"Text": w["text"].upper(), "X": int(w["left"]) + x, "Y": int(w["top"]) + y,
                                        "Width": int(w["width"]), "Height": int(w["height"])} for w in words]})
    return rows


def title_words(label, title):
    target = "".join(c for c in title if c.isalpha())
    words = label["Words"]
    for start in range(len(words)):
        for end in range(start + 1, len(words) + 1):
            candidate = "".join(c for word in words[start:end] for c in word["Text"] if c.isalpha())
            if candidate == target:
                return words[start:end]
            if len(candidate) > len(target):
                break
    return []


def tighten_heading(label, pixels, map_id):
    label = dict(label)
    if map_id == "Vanilla":
        label["Width"] += 55
    rows, columns = rectangle_slice(label, pixels.shape[:2])
    crop = pixels[rows, columns].astype(int)
    white = (crop.min(axis=2) > 150) & (crop.max(axis=2) - crop.min(axis=2) < 35)
    occupied_rows = np.any(white, axis=1)
    changes = np.flatnonzero(np.diff(np.pad(occupied_rows.astype(np.int8), (1, 1))))
    bands = list(zip(changes[::2], changes[1::2]))
    if not bands:
        raise ValueError(f"No heading pixels for {map_id} {label['Text']}")
    top, bottom = max(bands, key=lambda band: int(white[band[0]:band[1]].sum()))
    ys, xs = np.nonzero(white[top:bottom])
    label.update(X=columns.start + int(xs.min()), Y=rows.start + int(top),
                 Width=int(xs.max() - xs.min()) + 1, Height=int(bottom - top))
    return label


def adjacency(installation, map_id, rooms):
    graph = defaultdict(set)
    for region in {r["RegionCode"] for r in rooms}:
        code = region.lower()
        paths = [installation / "world" / code / f"world_{code}.txt"]
        if map_id != "Vanilla":
            paths.extend(installation / "mods/moreslugcats" / prefix / code / f"world_{code}.txt"
                         for prefix in ("world", "modify/world"))
        for room, links in world_connections(paths, TIMELINES[map_id]).items():
            for link in links:
                graph[room].add(link)
                graph[link].add(room)
    return graph


def line_segments(pixels, occupied):
    neutral = ((pixels.max(axis=2).astype(int) - pixels.min(axis=2) < 35)
               & (pixels.min(axis=2) > 65) & (occupied == 0)).astype(np.uint8)
    segments = []
    for vertical in (False, True):
        lines = cv2.morphologyEx(neutral, cv2.MORPH_OPEN, np.ones((25, 1) if vertical else (1, 25), np.uint8))
        count, _, stats, _ = cv2.connectedComponentsWithStats(lines, 8)
        for x, y, width, height, area in stats[1:]:
            length, thickness = (height, width) if vertical else (width, height)
            if thickness > 10 or length < 30:
                continue
            segments.append((int(x + width // 2), int(y), int(y + height - 1), True) if vertical
                            else (int(y + height // 2), int(x), int(x + width - 1), False))
    merged = []
    for axis, start, end, vertical in sorted(segments, key=lambda s: (s[3], s[0], s[1])):
        match = next((i for i, (a, b, c, v) in enumerate(merged)
                      if v == vertical and abs(a - axis) <= 2 and start <= c + 160 and end >= b - 160), None)
        if match is None:
            merged.append((axis, start, end, vertical))
        else:
            a, b, c, v = merged[match]
            merged[match] = (a, min(b, start), max(c, end), v)
    return merged


def project(point, segment):
    axis, start, end, vertical = segment
    value = int(np.clip(point[1 if vertical else 0], start, end))
    return (axis, value) if vertical else (value, axis)


def line_graph(segments, anchors):
    nodes = [set() for _ in segments]
    for i, (axis, start, end, vertical) in enumerate(segments):
        nodes[i].update([(axis, start), (axis, end)] if vertical else [(start, axis), (end, axis)])
        for j, (other_axis, other_start, other_end, other_vertical) in enumerate(segments[:i]):
            if vertical == other_vertical:
                continue
            if start - 8 <= other_axis <= end + 8 and other_start - 8 <= axis <= other_end + 8:
                point = (axis, other_axis) if vertical else (other_axis, axis)
                nodes[i].add(point)
                nodes[j].add(point)
    projections = {}
    for name, room in anchors.items():
        point = (room["X"], room["Y"])
        choices = []
        for i, segment in enumerate(segments):
            position = project(point, segment)
            distance = min(math.hypot(max(rect["X"] - position[0], 0, position[0] - rect["X"] - rect["Width"]),
                                      max(rect["Y"] - position[1], 0, position[1] - rect["Y"] - rect["Height"]))
                           for rect in room_rectangles(room))
            choices.append((distance, i, position))
        choices.sort()
        projections[name] = choices[:10]
        for distance, i, position in choices[:10]:
            if distance < 250:
                nodes[i].add(position)
    edges = defaultdict(dict)
    for points in nodes:
        ordered = sorted(points)
        for a, b in zip(ordered, ordered[1:]):
            edges[a][b] = edges[b][a] = math.dist(a, b)
    return edges, projections


def shortest_path(graph, starts, ends):
    targets = {p: d for d, _, p in ends if d < 250}
    queue, distances, previous = [], {}, {}
    for d, _, p in starts:
        if d < 250:
            distances[p] = d * 10
            heapq.heappush(queue, (d * 10, p))
    best, last = float("inf"), None
    while queue:
        cost, point = heapq.heappop(queue)
        if cost != distances.get(point) or cost > best:
            continue
        if point in targets and cost + targets[point] * 10 < best:
            best, last = cost + targets[point] * 10, point
        for neighbor, length in graph[point].items():
            next_cost = cost + length
            if next_cost < distances.get(neighbor, float("inf")):
                distances[neighbor] = next_cost
                previous[neighbor] = point
                heapq.heappush(queue, (next_cost, neighbor))
    if last is None:
        return []
    result = [last]
    while last in previous:
        last = previous[last]
        result.append(last)
    return result[::-1]


def gate_routes(pixels, occupied, graph, rooms):
    by_name = {r["RoomId"].upper(): r for r in rooms}
    gates = {name: sorted(neighbors) for name, neighbors in graph.items() if name.startswith("GATE_")}
    anchors = {name: r for name, r in by_name.items()
               if name in gates or any(name in neighbors for neighbors in gates.values())}
    segments = line_segments(pixels, occupied)
    edges, projections = line_graph(segments, anchors)
    routes = []
    for gate, neighbors in sorted(gates.items()):
        sides = [name for name in neighbors if name in anchors]
        if gate not in projections or len(sides) < 2:
            routes.append({"Gate": gate, "Rooms": sides, "Path": [], "Problem": "Missing mapped gate or side"})
            continue
        other = max(sides, key=lambda name: math.dist((anchors[name]["X"], anchors[name]["Y"]), (anchors[gate]["X"], anchors[gate]["Y"])))
        start = projections[gate][0]
        segment = segments[start[1]]
        path = shortest_path(edges, [start], projections[other])
        if len(path) < 2 and segment[2] - segment[1] < 150:
            axis, left, right, vertical = segment
            path = [(axis, left), (axis, right)] if vertical else [(left, axis), (right, axis)]
        if len(path) < 2:
            choices = []
            for a in sides:
                for b in sides:
                    if a >= b or a.split('_')[0] == b.split('_')[0]:
                        continue
                    candidate = shortest_path(edges, projections[a][:2], projections[b][:2])
                    if len(candidate) > 1:
                        choices.append(candidate)
            if choices:
                path = min(choices, key=lambda points: sum(math.dist(a, b) for a, b in zip(points, points[1:])))
        routes.append({"Gate": gate, "Rooms": sides, "Path": path,
                       "Problem": None if len(path) > 1 else "No artwork route"})
    return routes


def conditional_artwork_rooms(map_id, rooms):
    catalog = json.loads((SAVES / "RoomMapCatalog.json").read_text())
    groups = []
    if map_id in ("Downpour", "Rivulet"):
        groups.append(("Saint", "MS_HEART", ("MS_ARTERY12", "MS_CAPI04")))
    if map_id in ("Artificer", "Spearmaster", "Rivulet"):
        groups.append(("Downpour", "SH_A22", ("SH_GOR01", "SH_GOR02")))
    result = []
    names = {r["RoomId"].upper() for r in rooms}
    for source_map, anchor, selected in groups:
        source = catalog[source_map]
        source_anchor = next(r for r in source if r["RoomId"].upper() == anchor)
        target_anchor = next(r for r in rooms if r["RoomId"].upper() == anchor)
        dx, dy = target_anchor["X"] - source_anchor["X"], target_anchor["Y"] - source_anchor["Y"]
        result.extend({**r, "X": r["X"] + dx, "Y": r["Y"] + dy, "ConditionalArtwork": True,
                       "Bounds": [{**rect, "X": rect["X"] + dx, "Y": rect["Y"] + dy} for rect in r["Bounds"]]}
                      for r in source if r["RoomId"].upper() in selected and r["RoomId"].upper() not in names)
    return result


def straight_pipe_routes(pixels, occupied, rooms, graph):
    by_name = {r["RoomId"].upper(): r for r in rooms}
    white = (pixels.min(axis=2) > 65) & (pixels.max(axis=2).astype(int) - pixels.min(axis=2) < 35)
    result = []
    for a in sorted(by_name):
        if a.startswith("GATE_"):
            continue
        for b in sorted(graph[a]):
            if b <= a or b not in by_name or b.startswith("GATE_"):
                continue
            for ra in room_rectangles(by_name[a]):
                for rb in room_rectangles(by_name[b]):
                    for vertical in (False, True):
                        axis, size, along, length = ("X", "Width", "Y", "Height") if vertical else ("Y", "Height", "X", "Width")
                        low = math.ceil(max(ra[axis], rb[axis]))
                        high = math.floor(min(ra[axis] + ra[size], rb[axis] + rb[size]))
                        first, second = sorted((ra, rb), key=lambda rect: rect[along])
                        start = math.ceil(first[along] + first[length])
                        end = math.floor(second[along])
                        if high <= low or not 25 <= end - start <= 1200:
                            continue
                        rect = {"X": low if vertical else start, "Y": start if vertical else low,
                                "Width": high - low if vertical else end - start, "Height": end - start if vertical else high - low}
                        bounds = rectangle_slice(rect, occupied.shape)
                        available = occupied[bounds] == 0
                        counts = np.sum(white[bounds] & available, axis=0 if vertical else 1)
                        possible = np.sum(available, axis=0 if vertical else 1)
                        valid = (possible >= (end - start) / 4) & (counts >= 10) & (counts / np.maximum(1, possible) >= 0.4)
                        if not np.any(valid):
                            continue
                        index = int(np.argmax(np.where(valid, counts, -1)))
                        coordinate = low + index
                        path = [[coordinate, start], [coordinate, end]] if vertical else [[start, coordinate], [end, coordinate]]
                        result.append({"Gate": f"{a}_{b}", "Kind": "pipe", "Rooms": [a, b], "Path": path, "LineWidth": 3, "Problem": None})
    return result


def special_routes(map_id, rooms):
    routes = []
    if map_id in ("Downpour", "Rivulet", "Saint"):
        anchor = next(r for r in rooms if r["RoomId"] == "MS_HEART")
        dx, dy = anchor["X"] - 10751, anchor["Y"] - 3491
        routes.append({"Gate": "MS_BITTERSTART_MS_HEART", "Kind": "pipe", "Rooms": ["MS_BITTERSTART", "MS_HEART"],
                       "Path": [[x + dx, y + dy] for x, y in [(11080, 3138), (11080, 3309), (10750, 3309), (10750, 3391)]],
                       "Labels": [{"X": 10758 + dx, "Y": 3314 + dy, "Width": 155, "Height": 39}], "Problem": None})
    if map_id == "Saint":
        routes.append({"Gate": "SB_D06_HR_C01", "Kind": "pipe", "Rooms": ["SB_D06", "HR_C01"],
                       "Path": [[2227, 3786], [2227, 4490], [1065, 4490], [1065, 4452]], "Problem": None})
    return routes


def gate_legends(pixels, routes):
    ink = (pixels.max(axis=2) > 30).astype(np.uint8)
    count, components, stats, _ = cv2.connectedComponentsWithStats(ink, 8)
    template = np.zeros((13, 22), np.uint8)
    for row in range(13):
        template[row, 0 if 4 <= row <= 8 else 14:min(22, 17 + min(row, 12 - row))] = 1
    arrows = []
    for index in range(1, count):
        x, y, width, height, area = stats[index].tolist()
        if not (8 <= width <= 30 and 6 <= height <= 20 and 1.4 <= width / height <= 2):
            continue
        sample = cv2.resize((components[y:y + height, x:x + width] == index).astype(np.uint8), (22, 13), interpolation=cv2.INTER_NEAREST)
        score = max(np.count_nonzero(sample & target) / np.count_nonzero(sample | target) for target in (template, template[:, ::-1]))
        if score > 0.8:
            arrows.append((x, y, width, height))
    legends = []
    for a in arrows:
        for b in arrows:
            if not (a[0] + a[2] < b[0] < a[0] + 150 and abs(a[1] - b[1]) <= 2):
                continue
            scale = (a[2] + b[2]) / 44
            middle = round((a[0] + a[2] + b[0]) / 2)
            top = min(a[1], b[1])
            height = round(64 * scale)
            divider = pixels[top:top + height, middle].min(axis=1)
            if len(divider) != height or np.mean(divider > 150) < 0.7:
                continue
            point = (middle, top + height)
            choices = []
            for route in routes:
                if route.get("Kind") == "pipe":
                    continue
                for start, end in zip(route["Path"], route["Path"][1:]):
                    dx, dy = end[0] - start[0], end[1] - start[1]
                    amount = np.clip(((point[0] - start[0]) * dx + (point[1] - start[1]) * dy) / max(1, dx * dx + dy * dy), 0, 1)
                    distance = math.dist(point, (start[0] + amount * dx, start[1] + amount * dy))
                    choices.append((distance, route["Gate"]))
            if not choices:
                continue
            distance, gate = min(choices)
            legends.append({"X": a[0] - round(12 * scale), "Y": top - 2,
                            "Width": b[0] + b[2] - a[0] + round(24 * scale), "Height": height + 4,
                            "Gate": gate, "Distance": round(float(distance), 1)})
    return legends


def compact_rectangles(mask):
    rectangles, active = [], {}
    for y, row in enumerate(mask):
        changes = np.flatnonzero(np.diff(np.pad(row.astype(np.int8), (1, 1))))
        runs = {(int(a), int(b - a)) for a, b in zip(changes[::2], changes[1::2])}
        for key in active.keys() - runs:
            rectangles.append(active[key])
        active = {key: [key[0], active[key][1], key[1], active[key][3] + 1] if key in active
                  else [key[0], y, key[1], 1] for key in runs}
    rectangles.extend(active.values())
    return sorted(rectangles, key=lambda r: (r[1], r[0]))


def artwork_features(pixels, occupied, rooms, graph, regions, labels, routes, legends):
    remaining = (pixels.max(axis=2) > 5).astype(np.uint8) & (1 - occupied)
    ids = np.zeros(remaining.shape, np.uint16)
    features = []
    by_key = {}

    def feature(kind, names=(), region=None, text=None):
        names = sorted(set(names))
        key = (kind, tuple(names), region, text if kind == "label" else None)
        if key not in by_key:
            by_key[key] = len(features) + 1
            features.append({"Kind": kind, "Rooms": names, "Region": region, "Text": text, "Rectangles": []})
        return by_key[key]

    def take_rectangle(label, value):
        bounds = rectangle_slice(label, ids.shape, 2)
        crop = ids[bounds]
        crop[remaining[bounds] != 0] = value
        remaining[bounds] = 0

    for label in regions:
        take_rectangle(label, feature("region", region=label["Region"], text=label["Text"]))
    for room in rooms:
        if room.get("ConditionalArtwork"):
            value = feature("room", [room["RoomId"]])
            for rect in room["Bounds"]:
                bounds = rectangle_slice(rect, ids.shape)
                ids[bounds][pixels[bounds].max(axis=2) > 5] = value
    for route in routes:
        for label in route.get("Labels", []):
            take_rectangle(label, feature(route["Kind"], route["Rooms"], text=route["Gate"]))
    for legend in legends:
        if legend["Distance"] <= 150:
            route = next(route for route in routes if route["Gate"] == legend["Gate"])
            take_rectangle(legend, feature("gate", [route["Gate"], *route["Rooms"]], text=route["Gate"]))

    room_bounds, room_names = [], []
    for room in rooms:
        for rect in room_rectangles(room):
            room_bounds.append([rect["X"], rect["Y"], rect["X"] + rect["Width"], rect["Y"] + rect["Height"]])
            room_names.append(room["RoomId"].upper())
    room_bounds = np.array(room_bounds)

    def nearest_rooms(points, limit=4):
        dx = np.maximum(np.maximum(room_bounds[:, 0, None] - points[:, 0], 0), points[:, 0] - room_bounds[:, 2, None])
        dy = np.maximum(np.maximum(room_bounds[:, 1, None] - points[:, 1], 0), points[:, 1] - room_bounds[:, 3, None])
        distances = np.min(dx * dx + dy * dy, axis=1)
        results = {}
        for i in np.argsort(distances):
            results.setdefault(room_names[i], math.sqrt(float(distances[i])))
            if len(results) >= limit:
                break
        return results

    for label in sorted(labels, key=lambda row: (-row["Lines"], -row["Confidence"])):
        if label["Confidence"] < 65 or not 5 <= label["Height"] <= 65 or label["Width"] > 700:
            continue
        if sum(c.isalpha() for c in label["Text"]) < 4:
            continue
        bounds = rectangle_slice(label, ids.shape)
        if np.any(ids[bounds]) or remaining[bounds].sum() < 8:
            continue
        point = np.array([[label["X"] + label["Width"] / 2, label["Y"] + label["Height"] / 2]])
        nearest = nearest_rooms(point, 1)
        name = next(iter(nearest))
        if nearest[name] <= 150:
            take_rectangle(label, feature("label", [name], text=label["Text"]))

    gate_mask = np.zeros(ids.shape, np.uint8)
    gate_ids = np.zeros(ids.shape, np.uint16)
    for route in routes:
        if len(route["Path"]) < 2:
            continue
        names = route["Rooms"] if route.get("Kind") == "pipe" else [route["Gate"], *route["Rooms"]]
        value = feature(route.get("Kind", "gate"), names, text=route["Gate"])
        path = np.array(route["Path"], np.int32).reshape((-1, 1, 2))
        thickness = route.get("LineWidth", 9)
        left = max(0, int(path[:, 0, 0].min()) - thickness)
        top = max(0, int(path[:, 0, 1].min()) - thickness)
        right = min(ids.shape[1], int(path[:, 0, 0].max()) + thickness + 1)
        bottom = min(ids.shape[0], int(path[:, 0, 1].max()) + thickness + 1)
        line = np.zeros((bottom - top, right - left), np.uint8)
        cv2.polylines(line, [path - np.array([left, top])], False, 1, thickness)
        crop = gate_ids[top:bottom, left:right]
        previous = crop.copy()
        for prior in np.unique(previous[line != 0]):
            merged = value if prior == 0 else feature("pipe", [*names, *features[int(prior) - 1]["Rooms"]])
            if prior == value:
                merged = value
            crop[(line != 0) & (previous == prior)] = merged
        gate_mask[top:bottom, left:right] |= line
    label_ids = np.array([False, *(entry["Kind"] == "label" for entry in features)])
    neutral = (pixels.min(axis=2) > 65) & (pixels.max(axis=2).astype(int) - pixels.min(axis=2) < 35)
    selected = (gate_mask != 0) & ((remaining != 0) | (label_ids[ids] & neutral))
    ids[selected] = gate_ids[selected]
    remaining[selected] = 0
    distance, nearest = cv2.distanceTransformWithLabels(1 - gate_mask, cv2.DIST_L2, 5, labelType=cv2.DIST_LABEL_PIXEL)
    lookup = np.zeros(int(nearest.max()) + 1, np.uint16)
    lookup[nearest[gate_mask != 0]] = gate_ids[gate_mask != 0]

    grouped = cv2.morphologyEx(remaining, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    count, components, stats, _ = cv2.connectedComponentsWithStats(grouped, 8)
    uncertain = []
    for component in range(1, count):
        x, y, width, height, area = stats[component].tolist()
        crop = (components[y:y + height, x:x + width] == component) & (remaining[y:y + height, x:x + width] != 0)
        ys, xs = np.nonzero(crop)
        if not len(xs):
            continue
        points = np.column_stack((xs + x, ys + y))
        gate_distances = distance[points[:, 1], points[:, 0]]
        closest = int(gate_distances.argmin())
        gate_distance = gate_distances[closest]
        gate_value = lookup[nearest[points[closest, 1], points[closest, 0]]]
        colors = pixels[points[:, 1], points[:, 0]].astype(int)
        neutral = float(np.mean((colors.max(axis=1) - colors.min(axis=1) < 40) & (colors.max(axis=1) > 100)))
        candidates = nearest_rooms(points[::max(1, len(points) // 24)], 6)
        owner, owner_distance = next(iter(candidates.items()))
        if gate_value and (gate_distance <= 5 or features[int(gate_value) - 1]["Kind"] == "gate"
                           and gate_distance <= 100 and width <= 65 and height <= 80
                           and (neutral > 0.7 or width <= 30 and height <= 25) and owner_distance > 20):
            value = int(gate_value)
        else:
            pairs = [(a, b) for a, da in candidates.items() for b, db in candidates.items()
                     if a < b and da <= 16 and db <= 16 and b in graph[a]]
            if pairs:
                if len(pairs) == 1:
                    value = feature("pipe", pairs[0])
                else:
                    distances = {}
                    for name in {name for pair in pairs for name in pair}:
                        bounds = room_bounds[np.array(room_names) == name]
                        dx = np.maximum(np.maximum(bounds[:, 0, None] - points[:, 0], 0), points[:, 0] - bounds[:, 2, None])
                        dy = np.maximum(np.maximum(bounds[:, 1, None] - points[:, 1], 0), points[:, 1] - bounds[:, 3, None])
                        distances[name] = np.sqrt(np.min(dx * dx + dy * dy, axis=0))
                    scores = np.array([distances[a] + distances[b] for a, b in pairs])
                    memberships = np.sum((scores <= scores.min(axis=0) + 2).astype(np.int64)
                                         * (1 << np.arange(len(pairs)))[:, None], axis=0)
                    for membership in np.unique(memberships):
                        names = {name for index, pair in enumerate(pairs) if membership & (1 << index) for name in pair}
                        selected = memberships == membership
                        ids[points[selected, 1], points[selected, 0]] = feature("pipe", names)
                    continue
            else:
                gates = [route for route in routes if route["Gate"] in graph[owner] and route.get("Kind") != "pipe" and route["Path"]]
                if gates and owner_distance <= 16 and max(width, height) > 100:
                    route = gates[0]
                    value = feature("gate", [route["Gate"], *route["Rooms"]], text=route["Gate"])
                else:
                    value = feature("detail", [owner])
                if features[value - 1]["Kind"] == "detail" and (owner_distance > 100 or max(width, height) > 180):
                    uncertain.append({"Bounds": [x, y, width, height], "Room": owner, "Distance": round(owner_distance, 1), "Pixels": len(xs)})
        ids[y:y + height, x:x + width][crop] = value

    all_y, all_x = np.nonzero(ids)
    values = ids[all_y, all_x]
    order = np.argsort(values)
    boundaries = np.searchsorted(values[order], np.arange(len(features) + 2))
    for value, entry in enumerate(features, 1):
        selected = order[boundaries[value]:boundaries[value + 1]]
        if not len(selected):
            continue
        ys, xs = all_y[selected], all_x[selected]
        left, top, right, bottom = int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1
        rectangles = compact_rectangles(ids[top:bottom, left:right] == value)
        entry["Rectangles"] = [[x + left, y + top, w, h] for x, y, w, h in rectangles]
    return [entry for entry in features if entry["Rectangles"]], ids, uncertain


def extract(map_id, output, installation, tesseract):
    rooms = json.loads((SAVES / "RoomMapCatalog.json").read_text())[map_id]
    rooms.extend(conditional_artwork_rooms(map_id, rooms))
    pixels = np.array(Image.open(ROOT / "art/maps" / f"World_Map_({map_id}).png").convert("RGB"))
    occupied = np.zeros(pixels.shape[:2], np.uint8)
    for room in rooms:
        for rect in room_rectangles(room):
            occupied[rectangle_slice(rect, occupied.shape)] = 1
    artwork = (pixels.max(axis=2) > 5).astype(np.uint8)
    remaining = artwork & (1 - occupied)
    labels = read_labels(pixels, map_id, output, tesseract)
    (output / f"{map_id}-labels.json").write_text(json.dumps(labels, indent=2))
    regions = []
    for code, title in (REGIONS | SAINT_REGIONS if map_id == "Saint" else REGIONS).items():
        if code not in {r["RegionCode"] for r in rooms}:
            continue
        matches = [label for label in labels if label["Height"] < 100 and title_words(label, title)
                   and max(word["Height"] for word in title_words(label, title)) >= 24]
        if matches:
            label = max(matches, key=lambda row: row["Confidence"])
            words = title_words(label, title)
            if words:
                x, y = min(w["X"] for w in words), min(w["Y"] for w in words)
                label = {**label, "Text": title, "X": x, "Y": y,
                         "Width": max(w["X"] + w["Width"] for w in words) - x,
                         "Height": max(w["Y"] + w["Height"] for w in words) - y}
            label = tighten_heading(label, pixels, map_id)
            regions.append({**label, "Region": code})
            remaining[rectangle_slice(label, remaining.shape, 2)] = 0

    count, components, stats, centers = cv2.connectedComponentsWithStats(remaining, 8)
    graph = adjacency(installation, map_id, rooms)
    if map_id in ("Downpour", "Rivulet", "Saint"):
        for a, b in [("MS_MEM06", "MS_ARTERY12"), ("MS_ARTERY12", "MS_CAPI04"), ("MS_CAPI04", "MS_BITTERACCESS")]:
            graph[a].add(b)
            graph[b].add(a)
    if map_id in ("Downpour", "Artificer", "Spearmaster", "Rivulet"):
        for a, b in [("SH_B05", "SH_GOR01"), ("SH_GOR01", "SH_GOR02")]:
            graph[a].add(b)
            graph[b].add(a)
    routes = gate_routes(pixels, occupied, graph, rooms)
    routes.extend(special_routes(map_id, rooms))
    routes.extend(straight_pipe_routes(pixels, occupied, rooms, graph))
    legends = gate_legends(pixels, routes)
    expected = {route["Gate"] for route in routes if route["Problem"] is None and route.get("Kind") != "pipe"}
    matched = {legend["Gate"] for legend in legends if legend["Distance"] <= 150}
    if expected != matched:
        raise ValueError(f"Gate legends do not match routes in {map_id}: {expected ^ matched}")
    features, feature_ids, uncertain = artwork_features(pixels, occupied, rooms, graph, regions, labels, routes, legends)
    (output / f"{map_id}Artwork.json.gz").write_bytes(gzip.compress(json.dumps(features, separators=(",", ":")).encode(), mtime=0))
    colors = np.random.default_rng(481).integers(50, 256, (int(feature_ids.max()) + 1, 3), dtype=np.uint8)
    colors[0] = 0
    Image.fromarray(colors[feature_ids]).save(output / f"{map_id}-features.png")
    long_components = []
    for index in range(1, count):
        x, y, width, height, area = stats[index].tolist()
        if max(width, height) < 100:
            continue
        ys, xs = np.nonzero(components[y:y + height, x:x + width] == index)
        points = np.column_stack((xs + x, ys + y))
        sample = points[::max(1, len(points) // 1000)]
        contacts = []
        for room in rooms:
            best = float("inf")
            for rect in room_rectangles(room):
                dx = np.maximum(np.maximum(rect["X"] - sample[:, 0], 0), sample[:, 0] - rect["X"] - rect["Width"])
                dy = np.maximum(np.maximum(rect["Y"] - sample[:, 1], 0), sample[:, 1] - rect["Y"] - rect["Height"])
                best = min(best, float(np.min(dx * dx + dy * dy)))
            if best <= 20 * 20:
                contacts.append((round(math.sqrt(best), 1), room["RoomId"].upper()))
        names = {name for _, name in contacts}
        edges = sorted({tuple(sorted((name, other))) for name in names for other in graph[name] if other in names})
        long_components.append({"Id": index, "Bounds": [x, y, width, height], "Area": area,
                                "Contacts": sorted(contacts), "Edges": edges})
    data = {"Map": map_id, "Regions": regions, "Gates": routes, "Legends": legends, "Features": len(features), "Uncertain": uncertain, "Components": count - 1,
            "ResidualPixels": int(remaining.sum()), "LongComponents": long_components}
    (output / f"{map_id}-audit.json").write_text(json.dumps(data, indent=2))
    preview = Image.fromarray(pixels)
    draw = ImageDraw.Draw(preview)
    for region in regions:
        x, y, w, h = region["X"], region["Y"], region["Width"], region["Height"]
        draw.rectangle((x - 3, y - 3, x + w + 3, y + h + 3), outline="lime", width=3)
    for index, route in enumerate(routes):
        if len(route["Path"]) > 1:
            draw.line([tuple(point) for point in route["Path"]], fill=(255, 60 + index * 19 % 196, 160), width=6)
            draw.text(tuple(route["Path"][0]), route["Gate"], fill="white")
    for component in long_components:
        x, y, w, h = component["Bounds"]
        draw.rectangle((x, y, x + w, y + h), outline="cyan", width=2)
        draw.text((x, y - 12), str(component["Id"]), fill="yellow")
    preview.save(output / f"{map_id}-audit.png")
    print(map_id, "regions", [(r["Region"], r["Text"]) for r in regions], "components", count - 1,
          "long", len(long_components), "residual pixels", int(remaining.sum()), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--maps", nargs="+", default=list(TIMELINES))
    parser.add_argument("--output", type=Path, default=ROOT / "bin/spoiler-maps")
    parser.add_argument("--installation", type=Path, default=Path(r"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\StreamingAssets"))
    parser.add_argument("--tesseract", type=Path, default=Path(r"C:\Program Files\Tesseract-OCR\tesseract.exe"))
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    for map_id in args.maps:
        extract(map_id, args.output, args.installation, args.tesseract)
