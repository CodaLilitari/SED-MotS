"""Independent geometry counterexamples for the PLAY fixture's runtime premise."""
import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from new_playable_fixture import assert_corridor_topology


def corridor():
    summary = {'items': [{'counts': {'sectors': 4, 'surfaces': 24}}]}
    faces, vertices = [], []

    def page(items):
        return {'offset': 0, 'nextOffset': None, 'total': len(items),
                'sourceTotal': len(items), 'returnedCount': len(items), 'items': items}

    for s in range(4):
        points = [[x, y, z] for x in (-.5, .5) for y in (s-.5, s+.5) for z in (-.2, .8)]
        vertices.append(page([{'index': i, 'position': p} for i, p in enumerate(points)]))
        # Deliberately different face/vertex order from the authoring seed.
        loops = ((0,1,3,2),(4,6,7,5),(0,4,5,1),(2,3,7,6),(0,2,6,4),(1,5,7,3))
        normals = ((1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1))
        rows = []
        for i, (loop, normal) in enumerate(zip(loops, normals)):
            target = s-1 if i == 2 and s else s+1 if i == 3 and s < 3 else -1
            peer = (3 if i == 2 else 2) if target >= 0 else -1
            rows.append({'index': i, 'sector': s, 'normal': list(normal),
                         'adjoinSector': target, 'adjoinSurface': peer,
                         'corners': [{'vertex': v} for v in loop]})
        faces.append(page(rows))
    return summary, faces, vertices


class PlayableTopologyTests(unittest.TestCase):
    def test_independent_corridor_accepts_and_roundtrip_evidence_is_stable(self):
        data = corridor()
        result = assert_corridor_topology(*data)
        self.assertEqual(result, assert_corridor_topology(*copy.deepcopy(data)))
        self.assertEqual(result['sectorCount'], 4)
        self.assertEqual(len(result['directedPortals']), 6)

    def test_extra_fifth_room_refused(self):
        data = corridor(); data[0]['items'][0]['counts']['sectors'] = 5
        with self.assertRaisesRegex(AssertionError, 'exactly four'): assert_corridor_topology(*data)

    def test_bypass_portal_refused(self):
        data = corridor(); data[1][0]['items'][0].update(adjoinSector=2, adjoinSurface=0)
        with self.assertRaisesRegex(AssertionError, 'bypass'): assert_corridor_topology(*data)

    def test_skipped_neighbor_refused(self):
        data = corridor(); data[1][0]['items'][3]['adjoinSector'] = 2
        with self.assertRaisesRegex(AssertionError, 'neighbor'): assert_corridor_topology(*data)

    def test_nonreciprocal_portal_refused(self):
        data = corridor(); data[1][1]['items'][2]['adjoinSurface'] = 0
        with self.assertRaisesRegex(AssertionError, 'Nonreciprocal'): assert_corridor_topology(*data)

    def test_duplicate_portal_face_refused(self):
        data = corridor(); data[1][0]['items'][0] = copy.deepcopy(data[1][0]['items'][3])
        data[1][0]['items'][0]['index'] = 0
        data[1][1]['items'][2]['adjoinSurface'] = 0
        with self.assertRaisesRegex(AssertionError, 'duplicate'): assert_corridor_topology(*data)

    def test_narrowed_opening_refused(self):
        data = corridor(); data[2][0]['items'][3]['position'][0] = -.25
        with self.assertRaisesRegex(AssertionError, 'corner'): assert_corridor_topology(*data)

    def test_crossed_polygon_refused(self):
        data = corridor(); corners = data[1][0]['items'][0]['corners']
        corners[1], corners[2] = corners[2], corners[1]
        with self.assertRaisesRegex(AssertionError, 'Crossed'): assert_corridor_topology(*data)

    def test_partial_readback_refused(self):
        data = corridor(); data[1][0]['nextOffset'] = 6
        with self.assertRaises(AssertionError): assert_corridor_topology(*data)


if __name__ == '__main__':
    unittest.main()
