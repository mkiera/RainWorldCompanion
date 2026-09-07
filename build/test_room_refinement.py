from pathlib import Path
import tempfile
import unittest

import numpy as np

from MatchWorldRooms import world_connections
from RefineWorldRooms import local_translation, patch_consensus


class RoomRefinementTests(unittest.TestCase):
    def test_timeline_links_preserve_disconnected_slot_numbers(self):
        text = "\n".join([
            "CONDITIONAL LINKS", "Saint : A : 1 : C", "Saint : A : B : D",
            "Saint : HIDEROOM : B", "Saint : EXCLUSIVEROOM : C", "END CONDITIONAL LINKS",
            "ROOMS", "A : B, DISCONNECTED", "B : A", "C : A", "D : A", "END ROOMS"])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "world.txt"
            path.write_text(text)
            saint = world_connections([path], "saint")
            survivor = world_connections([path], "white")
        self.assertEqual(saint["A"], ["D", "C"])
        self.assertNotIn("B", saint)
        self.assertEqual(survivor["A"], ["B"])
        self.assertNotIn("C", survivor)

    def test_translation_rejects_changed_layout(self):
        sources = [np.array(p) for p in [(0, 0), (80, 0), (0, 80), (80, 80)]]
        offsets = [np.array(p) for p in [(100, 200), (100, 200), (400, 600), (400, 600)]]
        self.assertIsNone(local_translation(list(zip(sources, [s + d for s, d in zip(sources, offsets)]))))

    def test_translation_tolerates_one_outlier(self):
        sources = [np.array(p) for p in [(0, 0), (80, 0), (0, 80), (80, 80)]]
        targets = [s + (100, 200) for s in sources]
        targets[-1] += (50, 50)
        delta, count = local_translation(list(zip(sources, targets)))
        np.testing.assert_array_equal(delta, (100, 200))
        self.assertEqual(count, 3)

    def test_partial_match_survives_missing_room_section(self):
        rng = np.random.default_rng(31)
        mask = rng.integers(0, 2, (60, 60)).astype(np.float32)
        target = np.zeros((180, 200), np.float32)
        target[70:130, 80:140] = mask
        target[70:90, 80:140] = 0
        result = patch_consensus(target, mask)
        self.assertIsNotNone(result)
        self.assertEqual(result[:2], (80, 70))

    def test_partial_match_rejects_repeated_room_shape(self):
        rng = np.random.default_rng(31)
        mask = rng.integers(0, 2, (60, 60)).astype(np.float32)
        target = np.zeros((180, 200), np.float32)
        target[10:70, 10:70] = mask
        target[100:160, 120:180] = mask
        self.assertIsNone(patch_consensus(target, mask))


if __name__ == "__main__":
    unittest.main()
