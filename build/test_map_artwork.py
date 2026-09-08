import unittest
from collections import defaultdict

import cv2
import numpy as np

from ExtractMapArtwork import artwork_features, tighten_heading


class MapArtworkTests(unittest.TestCase):
    def test_label_box_does_not_claim_a_gate_connection(self):
        pixels = np.zeros((100, 100, 3), np.uint8)
        occupied = np.zeros((100, 100), np.uint8)
        cv2.line(pixels, (10, 50), (90, 50), (255, 255, 255))
        pixels[55:60, 20:60] = (220, 180, 20)
        rooms = [{"RoomId": "SU_A", "X": 15, "Y": 75,
                  "Bounds": [{"X": 10, "Y": 70, "Width": 10, "Height": 10}]}]
        route = {"Gate": "GATE_SU_HI", "Rooms": ["SU_A", "HI_B"], "Path": [(10, 50), (90, 50)], "LineWidth": 1}
        label = {"Text": "THE PRECIPICE", "X": 10, "Y": 45, "Width": 60, "Height": 20,
                 "Confidence": 99, "Lines": 1}
        features, _, _ = artwork_features(pixels, occupied, rooms, defaultdict(set), [], [label], [route], [])
        for x in range(10, 91):
            entry = next(f for f in features if any(rx <= x < rx+w and ry <= 50 < ry+h for rx,ry,w,h in f["Rectangles"]))
            self.assertEqual({"GATE_SU_HI", "SU_A", "HI_B"}, set(entry["Rooms"]))
        self.assertTrue(any(f['Kind'] == 'label' and f['Text'] == 'THE PRECIPICE' for f in features))

    def test_crossing_routes_reveal_the_intersection_from_either_connection(self):
        pixels = np.zeros((100, 100, 3), np.uint8)
        occupied = np.zeros((100, 100), np.uint8)
        cv2.line(pixels, (10, 50), (90, 50), (255, 255, 255))
        cv2.line(pixels, (50, 10), (50, 90), (255, 255, 255))
        rooms = [{"RoomId": "SU_A", "X": 1, "Y": 1,
                  "Bounds": [{"X": 0, "Y": 0, "Width": 2, "Height": 2}]}]
        routes = [
            {"Gate": "GATE_SU_HI", "Rooms": ["SU_A", "HI_B"], "Path": [(10, 50), (90, 50)], "LineWidth": 1},
            {"Gate": "SU_C_SU_D", "Kind": "pipe", "Rooms": ["SU_C", "SU_D"], "Path": [(50, 10), (50, 90)], "LineWidth": 1},
        ]
        for ordered in (routes, routes[::-1]):
            features, ids, _ = artwork_features(pixels, occupied, rooms, defaultdict(set), [], [], ordered, [])
            def owners_at(x, y):
                return {name for feature in features for rx, ry, width, height in feature["Rectangles"]
                        if rx <= x < rx + width and ry <= y < ry + height for name in feature["Rooms"]}
            self.assertEqual({"GATE_SU_HI", "SU_A", "HI_B", "SU_C", "SU_D"}, owners_at(50, 50))
            self.assertEqual({"GATE_SU_HI", "SU_A", "HI_B"}, owners_at(20, 50))
            self.assertEqual({"SU_C", "SU_D"}, owners_at(50, 20))

    def test_connected_pipe_branches_keep_separate_room_owners(self):
        pixels = np.zeros((100, 100, 3), np.uint8)
        occupied = np.zeros((100, 100), np.uint8)
        rooms = []
        for name, x, y in [("SU_A", 5, 45), ("SU_B", 80, 5), ("SU_C", 80, 85)]:
            rooms.append({"RoomId": name, "X": x + 5, "Y": y + 5,
                          "Bounds": [{"X": x, "Y": y, "Width": 10, "Height": 10}]})
            occupied[y:y + 10, x:x + 10] = 1
        for a, b in [((15, 50), (50, 50)), ((50, 10), (50, 90)), ((50, 10), (80, 10)), ((50, 90), (80, 90))]:
            cv2.line(pixels, a, b, (255, 255, 255))
        graph = defaultdict(set, {"SU_A": {"SU_B", "SU_C"}, "SU_B": {"SU_A"}, "SU_C": {"SU_A"}})
        features, _, _ = artwork_features(pixels, occupied, rooms, graph, [], [], [], [])

        def owners_at(x, y):
            return {name for feature in features for rx, ry, width, height in feature["Rectangles"]
                    if rx <= x < rx + width and ry <= y < ry + height for name in feature["Rooms"]}

        self.assertEqual({"SU_A", "SU_B"}, owners_at(65, 10))
        self.assertEqual({"SU_A", "SU_C"}, owners_at(65, 90))
        self.assertEqual({"SU_A", "SU_B", "SU_C"}, owners_at(35, 50))

    def test_heading_crop_excludes_an_unrelated_symbol_below_the_text(self):
        pixels = np.zeros((80, 160, 3), np.uint8)
        pixels[5:35, 10:110] = 255
        pixels[45:48, 12:16] = 255
        label = {"Text": "OUTSKIRTS", "X": 5, "Y": 3, "Width": 120, "Height": 50}
        result = tighten_heading(label, pixels, "Downpour")
        self.assertEqual((10, 5, 100, 30), tuple(result[key] for key in ("X", "Y", "Width", "Height")))


if __name__ == "__main__":
    unittest.main()
