import json
import itertools
import pathlib
import sys
import urllib.request


ROOT = pathlib.Path(__file__).resolve().parents[1]
SAVES = ROOT / "src" / "RainWorldCompanion.Core" / "Saves"
GAME_WORLD = pathlib.Path(r"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\StreamingAssets\world")
OUTPUT = SAVES / "RoomMapCatalog.json"
REFERENCE = "https://henpemaz.github.io/Rain-World-Interactive-Map/tiles/vanilla/{}/region.json"
MAP_SIZES = {
    "Vanilla": (6166, 4509),
    "Downpour": (11600, 5000),
    "Artificer": (8110, 6200),
    "Spearmaster": (8770, 5000),
    "Rivulet": (9420, 5000),
    "Saint": (9370, 4550),
}
INLIER_DISTANCE = 100
MAX_INLIER_RESIDUAL = 100


def solve(matrix, values):
    size = len(values)
    for pivot in range(size):
        best = max(range(pivot, size), key=lambda row: abs(matrix[row][pivot]))
        matrix[pivot], matrix[best] = matrix[best], matrix[pivot]
        values[pivot], values[best] = values[best], values[pivot]
        divisor = matrix[pivot][pivot]
        if abs(divisor) < 1e-9:
            return None
        for column in range(pivot, size):
            matrix[pivot][column] /= divisor
        values[pivot] /= divisor
        for row in range(size):
            if row == pivot:
                continue
            factor = matrix[row][pivot]
            for column in range(pivot, size):
                matrix[row][column] -= factor * matrix[pivot][column]
            values[row] -= factor * values[pivot]
    return values


def fit_affine(samples):
    normal = [[0.0] * 3 for _ in range(3)]
    out_x = [0.0] * 3
    out_y = [0.0] * 3
    for source_x, source_y, target_x, target_y in samples:
        row = [source_x, source_y, 1.0]
        for left in range(3):
            out_x[left] += row[left] * target_x
            out_y[left] += row[left] * target_y
            for right in range(3):
                normal[left][right] += row[left] * row[right]
    x_coefficients = solve([row[:] for row in normal], out_x)
    y_coefficients = solve([row[:] for row in normal], out_y)
    if x_coefficients is None or y_coefficients is None:
        return None
    return x_coefficients, y_coefficients


def transform(transform, point):
    x_coefficients, y_coefficients = transform
    source_x, source_y = point
    return (
        x_coefficients[0] * source_x + x_coefficients[1] * source_y + x_coefficients[2],
        y_coefficients[0] * source_x + y_coefficients[1] * source_y + y_coefficients[2],
    )


def valid_room_ids():
    room_ids = {}
    for room_path in GAME_WORLD.glob("*-rooms/*.txt"):
        stem = room_path.stem
        if stem.endswith("_settings") or stem == "new project":
            continue
        room_ids.setdefault(stem.upper(), stem)
    return room_ids


def load_region(code):
    with urllib.request.urlopen(REFERENCE.format(code), timeout=30) as response:
        return json.load(response)


def room_bounds(feature):
    coordinates = feature["geometry"]["coordinates"]
    points = [point for ring in coordinates for point in ring]
    xs = [point[0] for point in points]
    ys = [point[1] for point in points]
    return min(xs), min(ys), max(xs), max(ys)


def distance(first, second):
    return ((first[0] - second[0]) ** 2 + (first[1] - second[1]) ** 2) ** 0.5


def affine_residuals(affine, samples):
    return [distance(transform(affine, (source_x, source_y)), (target_x, target_y))
            for source_x, source_y, target_x, target_y in samples]


def stable_three_anchor_fit(affine):
    x_coefficients, y_coefficients = affine
    determinant = abs(x_coefficients[0] * y_coefficients[1] - x_coefficients[1] * y_coefficients[0])
    x_scale = (x_coefficients[0] ** 2 + y_coefficients[0] ** 2) ** 0.5
    y_scale = (x_coefficients[1] ** 2 + y_coefficients[1] ** 2) ** 0.5
    return 0.0002 <= determinant <= 0.005 and 0.01 <= x_scale <= 0.1 and 0.01 <= y_scale <= 0.1


def fit_with_quality(samples):
    if len(samples) == 3:
        affine = fit_affine(samples)
        if affine is None or not stable_three_anchor_fit(affine):
            return None, "rejected-three-anchor", None
        residuals = affine_residuals(affine, samples)
        return affine, "reference-affine-three-anchor", (samples, residuals)
    if len(samples) < 4:
        return None, "rejected-insufficient-anchors", None
    candidates = []
    for triple in itertools.combinations(samples, 3):
        affine = fit_affine(triple)
        if affine is None:
            continue
        residuals = affine_residuals(affine, samples)
        inliers = [sample for sample, residual in zip(samples, residuals) if residual <= INLIER_DISTANCE]
        candidates.append((len(inliers), sum(residuals), inliers))
    if not candidates:
        return None, "rejected-singular", None
    _, _, inliers = max(candidates, key=lambda candidate: (candidate[0], -candidate[1]))
    if len(inliers) < 4:
        return None, "rejected-outliers", None
    affine = fit_affine(inliers)
    if affine is None or max(affine_residuals(affine, inliers)) > MAX_INLIER_RESIDUAL:
        return None, "rejected-residual", None
    return affine, "reference-affine", (inliers, affine_residuals(affine, inliers))


def main():
    game_rooms = valid_room_ids()
    regions = {}
    catalog = {}
    for map_id, size in MAP_SIZES.items():
        dens = json.loads((SAVES / f"{map_id}Dens.json").read_text(encoding="utf-8-sig"))
        entries = [
            {
                "RoomId": den["RoomId"],
                "RegionCode": den["RegionCode"],
                "X": den["X"],
                "Y": den["Y"],
                "Bounds": [],
                "MatchKind": "den-anchor",
            }
            for den in dens
        ]
        by_region = {}
        for den in dens:
            by_region.setdefault(den["RegionCode"].upper(), []).append(den)
        for code, anchors in by_region.items():
            if code not in regions:
                try:
                    data = load_region(code)
                except Exception as error:
                    print(f"Skipping {code}: {error}", file=sys.stderr)
                    regions[code] = {}
                else:
                    regions[code] = {
                        feature["properties"]["name"].upper(): feature
                        for feature in data.get("room_features", [])
                    }
            features = regions[code]
            samples = []
            for anchor in anchors:
                feature = features.get(anchor["RoomId"].upper())
                if feature is None:
                    continue
                left, top, right, bottom = room_bounds(feature)
                samples.append(((left + right) / 2, (top + bottom) / 2, anchor["X"], anchor["Y"]))
            affine, match_kind, quality = fit_with_quality(samples)
            if affine is None:
                print(f"{map_id} {code}: {match_kind} ({len(samples)} anchors)", file=sys.stderr)
                continue
            inliers, residuals = quality
            excluded = len(samples) - len(inliers)
            print(f"{map_id} {code}: {match_kind}, {len(inliers)}/{len(samples)} anchors, "
                  f"fit residual mean {sum(residuals) / len(residuals):.1f}px, "
                  f"max {max(residuals):.1f}px, excluded {excluded}")
            width, height = size
            for room_id, feature in features.items():
                if room_id not in game_rooms or room_id in {entry["RoomId"].upper() for entry in entries}:
                    continue
                canonical_room_id = game_rooms[room_id]
                left, top, right, bottom = room_bounds(feature)
                transformed = [
                    transform(affine, point)
                    for point in ((left, top), (left, bottom), (right, top), (right, bottom))
                ]
                min_x = min(point[0] for point in transformed)
                min_y = min(point[1] for point in transformed)
                max_x = max(point[0] for point in transformed)
                max_y = max(point[1] for point in transformed)
                if min_x < 0 or min_y < 0 or max_x > width or max_y > height:
                    continue
                entries.append(
                    {
                        "RoomId": canonical_room_id,
                        "RegionCode": code,
                        "X": round((min_x + max_x) / 2, 2),
                        "Y": round((min_y + max_y) / 2, 2),
                        "Bounds": [{"X": round(min_x, 2), "Y": round(min_y, 2), "Width": round(max_x - min_x, 2), "Height": round(max_y - min_y, 2)}],
                        "MatchKind": match_kind,
                    }
                )
        catalog[map_id] = sorted(entries, key=lambda entry: (entry["RegionCode"], entry["RoomId"]))
    override_path = SAVES / "RoomMapOverrides.json"
    if override_path.exists():
        for map_id, overrides in json.loads(override_path.read_text(encoding="utf-8")).items():
            by_id = {room["RoomId"].upper(): room for room in catalog[map_id]}
            by_id.update({room["RoomId"].upper(): room for room in overrides})
            catalog[map_id] = sorted(by_id.values(), key=lambda room: (room["RegionCode"], room["RoomId"]))
    omissions = json.loads((SAVES / "RoomMapOmissions.json").read_text(encoding="utf-8"))
    for map_id, names in omissions.items():
        excluded = {name.upper() for name in names}
        catalog[map_id] = [room for room in catalog[map_id] if room["RoomId"].upper() not in excluded]
    OUTPUT.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
