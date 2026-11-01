# Tests for MAT inspection and normalisation with SED's rules (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Vectors come from SED's ``dotnet/tests/Sed.Core.Tests/TrueColorMatTests.cs`` plus the retail mip fallback rule."""
import struct
import unittest
from dataclasses import replace

import fixtures
from fixtures import ARGB8888, INDEXED, LEGACY16, RGB555, RGB565, Layout, Texture, mat_bytes, u16, u32
from jk_level_importer import matfile
from jk_level_importer.matfile import MSG_MIP_DIMENSIONS, MSG_TRUNCATED_MIP


def sith_decode(data: bytes):
    """Pure-Python copy of Blender-Sith's 32-bit ``_decode_rgba_pixel_data`` (before its row flip)."""
    fields = struct.unpack_from("<14I", data, 20)
    _, bpp, rb, gb, bb, rs, gs, bs, rr, gr, br, ab, as_, ar = fields
    w, h = struct.unpack_from("<ii", data, 76 + 40)
    values = struct.unpack_from("<%dI" % (w * h), data, 76 + 40 + 24)

    def mask(bits):
        return 0xFFFFFFFF >> (32 - bits) if bits else 0
    out = []
    for v in values:
        alpha = ((v >> as_) & mask(ab)) << ar if ab > 0 else 255
        out += [((v >> rs) & mask(rb)) << rr, ((v >> gs) & mask(gb)) << gr, ((v >> bs) & mask(bb)) << br, alpha]
    return bytes(out)


def rgba(data: bytes) -> bytes:
    return matfile.decode_rgba(data)


class TrueColourVectors(unittest.TestCase):
    def test_rgb565_engine_rounding(self):
        data = mat_bytes(RGB565, 0, Texture(2, 2, 0, [u16(0xF800, 0x07E0, 0x001F, 0x8410)]))
        info = matfile.inspect(data)
        self.assertEqual((info.kind, info.bpp, info.has_color_key), ("truecolor", 16, False))
        self.assertEqual(rgba(data), bytes([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 132, 130, 132, 255]))

    def test_masked_fifteen_bit_keeps_mask_in_bit_fifteen(self):
        for flags, alpha in ((1, 0), (0, 255)):
            data = mat_bytes(RGB555, 0, Texture(2, 1, flags, [u16(0x7C00, 0x83E0)]))
            self.assertEqual(rgba(data), bytes([255, 0, 0, alpha, 0, 255, 0, 255]))
            self.assertEqual(matfile.inspect(data).has_alpha, flags == 1)

    def test_missing_channel_fields_follow_alpha_flag(self):
        self.assertEqual(rgba(mat_bytes(LEGACY16, 0, Texture(2, 1, 0, [u16(0xF800, 0x001F)]))),
                         bytes([255, 0, 0, 255, 0, 0, 255, 255]))
        self.assertEqual(rgba(mat_bytes(LEGACY16, 0, Texture(2, 1, 1, [u16(0xFC00, 0x001F)]))),
                         bytes([255, 0, 0, 255, 0, 0, 255, 0]))

    def test_thirty_two_bit_alpha_declared_or_opaque(self):
        self.assertEqual(rgba(mat_bytes(ARGB8888, 0, Texture(1, 1, 0, [u32(0x80112233)]))), bytes([0x11, 0x22, 0x33, 0x80]))
        opaque = replace(ARGB8888, a_bits=0, a_shift=0)
        self.assertEqual(rgba(mat_bytes(opaque, 0, Texture(1, 1, 0, [u32(0x00112233)]))), bytes([0x11, 0x22, 0x33, 0xFF]))

    def test_thirty_two_bit_needs_valid_layout(self):
        info = matfile.inspect(mat_bytes(Layout(2, 32), 0, Texture(1, 1, 0, [u32(0)])))
        self.assertIn("no RGB channel layout", info.error)
        for bad in (replace(ARGB8888, g_shift=12), replace(ARGB8888, a_shift=16), replace(ARGB8888, r_bits=9)):
            self.assertFalse(matfile.inspect(mat_bytes(bad, 0, Texture(1, 1, 0, [u32(0)]))).ok)

    def test_sixteen_bit_declared_layout_must_be_complete(self):
        for r_bits, g_bits, b_bits, r_shift, g_shift, b_shift in ((5, 6, 0, 11, 5, 0), (5, 0, 0, 11, 0, 0),
                                                                 (5, 6, 5, 11, 8, 0), (5, 6, 5, 12, 5, 0),
                                                                 (-5, 6, 5, 11, 5, 0), (9, 4, 3, 7, 3, 0)):
            layout = Layout(1, 16, r_bits, g_bits, b_bits, r_shift, g_shift, b_shift)
            for flags in (0, 1):
                info = matfile.inspect(mat_bytes(layout, 0, Texture(1, 1, flags, [u16(0)])))
                self.assertIn("invalid or overlapping RGB channel layout", info.error)

    def test_declared_sixteen_bit_alpha_takes_precedence(self):
        argb1555 = Layout(2, 16, 5, 5, 5, 10, 5, 0, 1, 15)
        self.assertEqual(rgba(mat_bytes(argb1555, 0, Texture(2, 1, 0, [u16(0xFC00, 0x03E0)]))),
                         bytes([255, 0, 0, 255, 0, 255, 0, 0]))
        argb4444 = Layout(2, 16, 4, 4, 4, 8, 4, 0, 4, 12)
        self.assertEqual(rgba(mat_bytes(argb4444, 0, Texture(1, 1, 1, [u16(0x8F71)]))), bytes([0xFF, 0x77, 0x11, 0x88]))
        self.assertEqual(rgba(mat_bytes(RGB565, 0, Texture(1, 1, 1, [u16(0xFFFF)]))), bytes([255, 255, 255, 255]))

    def test_true_colour_needs_rgb_mode_and_supported_depth(self):
        for mode, bpp in ((0, 16), (3, 16), (-1, 32), (1, 24), (2, 15)):
            info = matfile.inspect(mat_bytes(replace(RGB565, color_mode=mode, bpp=bpp), 0, Texture(2, 1, 0, [bytes(8)])))
            self.assertFalse(info.ok, (mode, bpp))

    def test_first_cel_skips_earlier_chains_at_their_depth(self):
        first = Texture(2, 2, 0, [u32(0x11111111, 0x22222222, 0x33333333, 0x44444444), u32(0x55555555)])
        data = mat_bytes(ARGB8888, 1, first, Texture(1, 1, 0, [u32(0xFF0000FF)]))
        info = matfile.inspect(data)
        self.assertEqual((info.width, info.selected), (1, 1))
        self.assertEqual(rgba(data), bytes([0, 0, 0xFF, 0xFF]))
        first16 = Texture(4, 4, 0, [u16(*[0x07E0] * 16), u16(0, 0, 0, 0), u16(0)])
        data16 = mat_bytes(RGB565, 1, first16, Texture(2, 1, 0, [u16(0xF800, 0x001F)]))
        self.assertEqual(rgba(data16), bytes([255, 0, 0, 255, 0, 0, 255, 255]))
        self.assertEqual(matfile.inspect(data16[:-1]).error, MSG_TRUNCATED_MIP)
        self.assertEqual(matfile.inspect(data16[:116 + 24 + 21 + 24 + 2]).error, "Truncated MAT texture header.")

    def test_indexed_ignores_uninitialised_channel_fields(self):
        layout = Layout(0, 8, 9961472, 64, 9961484, 2688, 0, 64, 9961472, 64)
        data = mat_bytes(layout, 0, Texture(2, 1, 0, [bytes([3, 4])]))
        info = matfile.inspect(data)
        self.assertEqual(info.kind, "indexed")
        normalized = matfile.normalize(data)
        self.assertEqual(normalized[-2:], bytes([3, 4]))
        self.assertEqual(struct.unpack_from("<ii", normalized, 20), (0, 8))
        self.assertEqual(normalized[28:76], bytes(48))


class MipRule(unittest.TestCase):
    def test_complete_mip_zero_with_missing_later_mips_shows_mip_zero(self):
        # DBZ zgrass1 pattern: 256x256 16-bit, four mips, the file ends right after mip 0.
        full = mat_bytes(RGB565, 0, Texture(256, 256, 0, [u16(*[0xF800] * 65536)]))
        data = bytearray(full)
        struct.pack_into("<i", data, 76 + 40 + 20, 4)
        info = matfile.inspect(bytes(data))
        self.assertTrue(info.ok)
        self.assertEqual(info.mip_fallback_note,
                         "Retail mip fallback: mip level 1 of 4 levels (128x128) needs 32768 bytes but 0 remain; "
                         "showing complete mip level 0, as retail JK does on faces nearer than the "
                         "level's MipMap Distances; retail faces far enough to use mip level 1 or later show data "
                         "the file never filled.")
        self.assertEqual(rgba(bytes(data))[:4], bytes([255, 0, 0, 255]))

    def test_sixteen_bit_vector_from_sed_now_falls_back(self):
        sixteen = mat_bytes(RGB565, 0, Texture(2, 2, 0, [u16(0xF800, 0x07E0, 0x001F, 0xFFFF), u16(0)]))
        self.assertIsNone(matfile.inspect(sixteen).mip_fallback_note)
        info = matfile.inspect(sixteen[:-1])
        self.assertEqual(info.mip_fallback_note,
                         "Retail mip fallback: mip level 1 of 2 levels (1x1) needs 2 bytes but 1 remain; "
                         "showing complete mip level 0, as retail JK does on faces nearer than the "
                         "level's MipMap Distances; retail faces far enough to use mip level 1 or later show data "
                         "the file never filled.")
        self.assertEqual(rgba(sixteen[:-1]), bytes([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]))
        thirty_two = mat_bytes(ARGB8888, 0, Texture(2, 1, 0, [u32(0xFF102030, 0x80405060)]))
        self.assertEqual(rgba(thirty_two), bytes([0x10, 0x20, 0x30, 0xFF, 0x40, 0x50, 0x60, 0x80]))
        self.assertEqual(matfile.inspect(thirty_two[:-4]).error, MSG_TRUNCATED_MIP)
        self.assertEqual(matfile.inspect(thirty_two[:-1]).error, MSG_TRUNCATED_MIP)

    def test_partial_later_mip_and_eight_and_thirty_two_bit(self):
        eight = mat_bytes(INDEXED, 0, Texture(4, 2, 1, [bytes([0, 7, 7, 0, 1, 2, 3, 4]), bytes([9, 9])]))
        info = matfile.inspect(eight[:-1])
        self.assertEqual(info.mip_fallback_note,
                         "Retail mip fallback: mip level 1 of 2 levels (2x1) needs 2 bytes but 1 remain; "
                         "showing complete mip level 0, as retail JK does on faces nearer than the "
                         "level's MipMap Distances; retail faces far enough to use mip level 1 or later show data "
                         "the file never filled.")
        self.assertTrue(info.has_color_key)
        three = mat_bytes(ARGB8888, 0, Texture(4, 4, 0, [u32(*[0x80112233] * 16), u32(*[0] * 4), u32(0)]))
        info = matfile.inspect(three[:-6])
        self.assertIn("mip level 1 of 3 levels (2x2) needs 16 bytes but 14 remain", info.mip_fallback_note)
        info = matfile.inspect(three[:-4])
        self.assertIn("mip level 2 of 3 levels (1x1) needs 4 bytes but 0 remain", info.mip_fallback_note)

    def test_short_mip_zero_or_earlier_texture_is_refused(self):
        data = mat_bytes(INDEXED, 0, Texture(4, 2, 0, [bytes(8), bytes(2)]))
        self.assertEqual(matfile.inspect(data[:76 + 40 + 24 + 7]).error, MSG_TRUNCATED_MIP)
        earlier = mat_bytes(INDEXED, 1, Texture(8, 8, 0, [bytes(64), bytes(16)]), Texture(1, 1, 0, [bytes(1)]))
        self.assertTrue(matfile.inspect(earlier).ok)
        # The earlier texture's mip 1 is short: its chain cannot locate the selected texture.
        self.assertEqual(matfile.inspect(earlier[:76 + 40 + 24 + 64 + 10]).error, MSG_TRUNCATED_MIP)

    def test_mip_count_beyond_dimensions_is_refused_even_when_truncated(self):
        data = bytearray(mat_bytes(INDEXED, 0, Texture(2, 2, 0, [bytes(4)])))
        struct.pack_into("<i", data, 76 + 40 + 20, 3)       # 2x2 cannot have three mips
        self.assertEqual(matfile.inspect(bytes(data)).error, MSG_MIP_DIMENSIONS)
        self.assertEqual(matfile.inspect(bytes(data) + bytes(1)).error, MSG_MIP_DIMENSIONS)

    def test_header_refusals(self):
        self.assertEqual(matfile.inspect(b"MAX " + bytes(80)).error, "Not a MAT file (bad magic)")
        bad_version = bytearray(mat_bytes(INDEXED, 0, Texture(1, 1, 0, [b"\0"])))
        struct.pack_into("<i", bad_version, 4, 0x31)
        self.assertEqual(matfile.inspect(bytes(bad_version)).error, "Unsupported MAT version 0x31")
        self.assertEqual(matfile.inspect(mat_bytes(INDEXED, 2, Texture(1, 1, 0, [b"\0"]))).error,
                         "MAT first cel references an out-of-range texture.")
        mip_count = bytearray(mat_bytes(INDEXED, 0, Texture(1, 1, 0, [b"\0"])))
        struct.pack_into("<i", mip_count, 76 + 40 + 20, 16)
        self.assertEqual(matfile.inspect(bytes(mip_count)).error, "MAT mip count is out of range.")


class Normalisation(unittest.TestCase):
    def assert_single(self, normalized):
        info = matfile.inspect(normalized)
        self.assertTrue(info.ok, info.error)
        self.assertEqual((info.cel_count, info.texture_count, info.mip_count, info.selected), (1, 1, 1, 0))
        self.assertIsNone(info.mip_fallback_note)
        return info

    def test_indexed_keeps_indices_and_keys_index_zero(self):
        source = mat_bytes(INDEXED, 1, Texture(2, 1, 0, [bytes([5, 6])]),
                           Texture(4, 2, 3, [bytes([0, 7, 7, 0, 1, 2, 3, 4]), bytes([9, 9])], color_key=9), cels=2)
        normalized = matfile.normalize(source[:-1])     # the second texture's mip 1 is short: fallback
        info = self.assert_single(normalized)
        self.assertEqual((info.width, info.height, info.kind), (4, 2, "indexed"))
        self.assertEqual(normalized[-8:], bytes([0, 7, 7, 0, 1, 2, 3, 4]))
        w, h, alpha, _, key, mips = struct.unpack_from("<6i", normalized, 76 + 40)
        self.assertEqual((w, h, alpha, key, mips), (4, 2, 1, 0, 1))    # flag bit 0 only, stored key 0

    def test_true_colour_becomes_32_bit_that_blender_sith_decodes_exactly(self):
        cases = [mat_bytes(RGB565, 0, Texture(2, 2, 0, [u16(0xF800, 0x07E0, 0x001F, 0x8410)])),
                 mat_bytes(LEGACY16, 0, Texture(2, 1, 1, [u16(0xFC00, 0x001F)])),
                 mat_bytes(RGB555, 0, Texture(2, 1, 1, [u16(0x7C00, 0x83E0)])),
                 mat_bytes(ARGB8888, 0, Texture(1, 1, 0, [u32(0x80112233)])),
                 mat_bytes(Layout(2, 16, 4, 4, 4, 8, 4, 0, 4, 12), 0, Texture(1, 1, 1, [u16(0x8F71)]))]
        for source in cases:
            with self.subTest(source=source[:40].hex()):
                expected = rgba(source)
                normalized = matfile.normalize(source)
                info = self.assert_single(normalized)
                self.assertEqual(info.bpp, 32)
                self.assertEqual(rgba(normalized), expected)
                self.assertEqual(sith_decode(normalized), expected)
                has_alpha = matfile.inspect(source).has_alpha
                self.assertEqual(struct.unpack_from("<i", normalized, 20)[0], 2 if has_alpha else 1)
                self.assertEqual(struct.unpack_from("<i", normalized, 64)[0], 8 if has_alpha else 0)

    def test_colour_mat_becomes_one_record(self):
        normalized = matfile.normalize(fixtures.color_mat_bytes(300))
        self.assertEqual(struct.unpack_from("<iiii", normalized, 4), (0x32, 0, 1, 0))
        self.assertEqual(struct.unpack_from("<i", normalized, 80)[0], 300 & 0xFF)
        info = matfile.inspect(fixtures.color_mat_bytes(7))
        self.assertEqual((info.kind, info.color_index, info.uv_size), ("color", 7, (64, 64)))

    def test_refused_mat_raises(self):
        with self.assertRaises(matfile.MatError):
            matfile.normalize(b"MAT " + bytes(10))


if __name__ == "__main__":
    unittest.main()
