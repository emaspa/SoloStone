#!/usr/bin/env python3
"""Checks for package_linux.py (font rename, output folder, archive): python3 -I installer/test_package_linux.py"""
import struct
import sys
import tarfile
import tempfile
import time
import unittest
from unittest import mock
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import package_linux as pl  # noqa: E402


def checksum(block):
    block += b'\0' * (-len(block) % 4)
    return sum(struct.unpack('>%dI' % (len(block) // 4), block)) & 0xFFFFFFFF


def font(tables):
    """A TrueType file from {tag: bytes}, tables in tag order, checksums left zero: rename_font recomputes them."""
    count = len(tables)
    data = struct.pack('>IHHHH', 0x00010000, count, 0, 0, 0)
    offset = 12 + 16 * count
    body = b''
    for tag in sorted(tables):
        data += struct.pack('>4sIII', tag, 0, offset + len(body), len(tables[tag]))
        body += tables[tag] + b'\0' * (-len(tables[tag]) % 4)
    return data + body


def name_table(records):
    header = struct.pack('>HHH', 0, len(records), 6 + 12 * len(records))
    strings = b''
    for platform, encoding, language, name_id, text in records:
        header += struct.pack('>HHHHHH', platform, encoding, language, name_id, len(text), len(strings))
        strings += text
    return header + strings


def tables_of(data):
    count = struct.unpack_from('>H', data, 4)[0]
    found = {}
    for index in range(count):
        tag, check, offset, length = struct.unpack_from('>4sIII', data, 12 + 16 * index)
        found[tag] = (check, data[offset:offset + length])
    return found


def names_of(table):
    _, count, storage = struct.unpack_from('>HHH', table, 0)
    found = {}
    for index in range(count):
        platform, _, _, name_id, length, offset = struct.unpack_from('>HHHHHH', table, 6 + 12 * index)
        text = table[storage + offset:storage + offset + length]
        found[(platform, name_id)] = text.decode('utf-16-be' if platform == 3 else 'mac_roman')
    return found


class RenameFont(unittest.TestCase):
    def setUp(self):
        records = []
        for platform, encoding, language, codec in ((1, 0, 0, 'mac_roman'), (3, 1, 1033, 'utf-16-be')):
            for name_id, text in ((0, 'Copyright Example'), (1, 'Original Sans'), (2, 'Regular'), (4, 'Original Sans'), (6, 'OriginalSans'), (7, 'Original is a trademark')):
                records.append((platform, encoding, language, name_id, text.encode(codec)))
        self.glyphs = bytes(range(37))
        self.source = font({b'head': bytes(54), b'name': name_table(records), b'glyf': self.glyphs})
        self.renamed = pl.rename_font(self.source, {1: 'New', 4: 'New', 6: 'New-Regular'}, {7})

    def test_names_replaced_dropped_and_kept(self):
        names = names_of(tables_of(self.renamed)[b'name'][1])
        for platform in (1, 3):
            self.assertEqual(names[(platform, 1)], 'New')
            self.assertEqual(names[(platform, 4)], 'New')
            self.assertEqual(names[(platform, 6)], 'New-Regular')
            self.assertEqual(names[(platform, 0)], 'Copyright Example')
            self.assertEqual(names[(platform, 2)], 'Regular')
            self.assertNotIn((platform, 7), names)

    def test_other_tables_keep_their_bytes(self):
        self.assertEqual(tables_of(self.renamed)[b'glyf'][1], self.glyphs)

    def test_checksums(self):
        for tag, (check, block) in tables_of(self.renamed).items():
            if tag == b'head':
                block = block[:8] + b'\0\0\0\0' + block[12:]
            self.assertEqual(check, checksum(block), tag)
        self.assertEqual(checksum(self.renamed), 0xB1B0AFBA)

    def test_refuses_what_it_cannot_read(self):
        with self.assertRaises(SystemExit):
            pl.rename_font(b'OTTO' + self.source[4:], {}, set())

    def test_shipped_names_avoid_the_reserved_ones(self):
        for text in pl.FONT_NAMES.values():
            for reserved in ('Liberation', 'Arimo', 'Tinos', 'Cousine'):
                self.assertNotIn(reserved.lower(), text.lower())
        self.assertEqual(pl.FONT_NAMES[1], 'LegacyRuntime')



class OutputFolder(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.out = Path(self.temp.name) / 'out'
        self.out.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def test_versions(self):
        for good in ('0.2.0', '10.0.402', '1.2.3-test.1', '1.2.3-rc-2'):
            self.assertTrue(pl.VERSION.fullmatch(good), good)
        for bad in ('0.1.0/../../x', '../x', '0.2', '1.2.3-', '1.2.3-a..b', '1.2.3/x', '1.2.3\n', ''):
            self.assertFalse(pl.VERSION.fullmatch(bad), bad)
            with self.assertRaises(SystemExit):
                pl.output_folder(self.out, bad)

    def test_replaces_only_its_own_folder(self):
        app = pl.output_folder(self.out, '1.0.0')
        (app / 'old').write_text('x')
        app = pl.output_folder(self.out, '1.0.0')
        self.assertEqual(sorted(entry.name for entry in app.iterdir()), [pl.MARKER])
        stranger = self.out / 'SoloStone-linux-2.0.0'
        stranger.mkdir()
        (stranger / 'keep').write_text('x')
        with self.assertRaises(SystemExit):
            pl.output_folder(self.out, '2.0.0')
        self.assertTrue((stranger / 'keep').exists())
        elsewhere = Path(self.temp.name) / 'elsewhere'
        elsewhere.mkdir()
        (elsewhere / pl.MARKER).write_bytes(b'')
        (self.out / 'SoloStone-linux-3.0.0').symlink_to(elsewhere)
        with self.assertRaises(SystemExit):
            pl.output_folder(self.out, '3.0.0')
        self.assertTrue((elsewhere / pl.MARKER).exists())

    def test_archive_is_reproducible_and_leaves_out_the_marker(self):
        app = pl.output_folder(self.out, '1.0.0')
        (app / 'solostone').write_text('launcher')
        first, second = self.out / 'a.tar.gz', self.out / 'b.tar.gz'
        with mock.patch('time.time', return_value=100.0):
            pl.tarball(app, first)
        with mock.patch('time.time', return_value=200.0):
            pl.tarball(app, second)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertEqual(first.read_bytes()[4:8], b'\0\0\0\0')
        with tarfile.open(first) as tar:
            self.assertEqual(tar.getnames(), ['SoloStone-linux-1.0.0', 'SoloStone-linux-1.0.0/solostone'])


if __name__ == '__main__':
    unittest.main()
