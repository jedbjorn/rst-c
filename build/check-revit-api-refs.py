#!/usr/bin/env python3
"""SC-013 N1 / decision #26: check staged PE AssemblyRef rows, independent of MSBuild syntax.

Stdlib only; ECMA-335 II.24.2/II.22 describe the metadata streams and table layouts.
Package floors come from check-revit-api-floor.py. See assembly_floor for the versions
observed in the actual floor packages. AssemblyRef has no file/package version: this
check cannot distinguish updates that retain the same AssemblyVersion. The project
package check remains necessary even when the compiled references pass.

Usage: python3 build/check-revit-api-refs.py build/R25 [build/R26 build/R27]
       python3 build/check-revit-api-refs.py --self-test
"""

import runpy
import struct
import sys
from pathlib import Path

FLOOR = runpy.run_path(str(Path(__file__).with_name("check-revit-api-floor.py")))["FLOOR"]

# Row schemas through AssemblyRef (0x23). Integers are fixed byte counts, heap names
# select 2/4-byte indexes, tN indexes table N, and other names are coded indexes.
SCHEMAS = [
    (2, 's', 'g', 'g', 'g'), ("ResolutionScope", 's', 's'),
    (4, 's', 's', 'TypeDefOrRef', 't4', 't6'), ('t4',),
    (2, 's', 'b'), ('t6',), (4, 2, 2, 's', 'b', 't8'), ('t8',),
    (2, 2, 's'), ('t2', 'TypeDefOrRef'), ('MemberRefParent', 's', 'b'),
    (2, 'HasConstant', 'b'), ('HasCustomAttribute', 'CustomAttributeType', 'b'),
    ('HasFieldMarshal', 'b'), (2, 'HasDeclSecurity', 'b'), (2, 4, 't2'),
    (4, 't4'), ('b',), ('t2', 't20'), ('t20',), (2, 's', 'TypeDefOrRef'),
    ('t2', 't23'), ('t23',), (2, 's', 'b'), (2, 't6', 'HasSemantics'),
    ('t2', 'MethodDefOrRef', 'MethodDefOrRef'), ('s',), ('b',),
    (2, 'MemberForwarded', 's', 't26'), (4, 't4'), (4, 4), (4,),
    (4, 2, 2, 2, 2, 4, 'b', 's', 's'), (4,), (4, 4, 4),
    (2, 2, 2, 2, 4, 'b', 's', 's', 'b'),
]
CODED = {
    'ResolutionScope': (2, (0, 26, 35, 1)),
    'TypeDefOrRef': (2, (2, 1, 27)),
    'MemberRefParent': (3, (2, 1, 26, 6, 27)),
    'HasConstant': (2, (4, 8, 23)),
    'HasCustomAttribute': (5, (6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43)),
    'CustomAttributeType': (3, (6, 10)),
    'HasFieldMarshal': (1, (4, 8)),
    'HasDeclSecurity': (2, (2, 6, 32)),
    'HasSemantics': (1, (20, 23)),
    'MethodDefOrRef': (1, (6, 10)),
    'MemberForwarded': (1, (4, 6)),
}


class Metadata:
    def __init__(self, path):
        self.data = Path(path).read_bytes()
        if self.data[:2] != b'MZ':
            raise ValueError('not a PE file')
        pe = self.number(0x3c, 4)
        if self.data[pe:pe + 4] != b'PE\0\0':
            raise ValueError('invalid PE signature')
        count = self.number(pe + 6, 2)
        opt = pe + 24
        magic = self.number(opt, 2)
        if magic not in (0x10b, 0x20b):
            raise ValueError('unknown PE optional header')
        sections = opt + self.number(pe + 20, 2)

        def offset(rva):
            for i in range(count):
                section = sections + i * 40
                size, address, raw_size, raw = struct.unpack_from('<IIII', self.data, section + 8)
                if address <= rva < address + max(size, raw_size):
                    if rva - address >= raw_size:
                        raise ValueError('RVA has no file data')
                    return raw + rva - address
            raise ValueError(f'RVA {rva:#x} outside PE sections')

        directory = opt + (96 if magic == 0x10b else 112)
        cli = offset(self.number(directory + 14 * 8, 4))
        root = offset(self.number(cli + 8, 4))
        if self.data[root:root + 4] != b'BSJB':
            raise ValueError('invalid CLI metadata signature')
        pos = root + 16 + self.number(root + 12, 4)
        pos = (pos + 3) & ~3
        streams = self.number(pos + 2, 2)
        pos += 4
        self.streams = {}
        for _ in range(streams):
            start, size = struct.unpack_from('<II', self.data, pos)
            end = self.data.index(0, pos + 8)
            name = self.data[pos + 8:end].decode('ascii')
            self.streams[name] = (root + start, size)
            self.slice(root + start, size)
            pos = (end + 4) & ~3
        table, self.table_size = self.streams.get('#~', self.streams.get('#-', (0, 0)))
        if not table:
            raise ValueError('no metadata tables stream')
        self.table_end = table + self.table_size
        self.heap_sizes = self.number(table + 6, 1)
        valid = self.number(table + 8, 8)
        self.rows = [0] * 64
        pos = table + 24
        for i in range(64):
            if valid & (1 << i):
                self.rows[i] = self.number(pos, 4)
                pos += 4
        if self.heap_sizes & 0x40:
            pos += 4  # ExtraData follows the row counts in extended metadata headers.
        self.tables = []
        for i, schema in enumerate(SCHEMAS):
            size = sum(self.width(field) for field in schema)
            self.tables.append((pos, size))
            pos += size * self.rows[i]
        if pos > self.table_end:
            raise ValueError('metadata rows exceed tables stream')

    def slice(self, pos, size):
        if pos < 0 or pos + size > len(self.data):
            raise ValueError('truncated PE/metadata')
        return self.data[pos:pos + size]

    def number(self, pos, size):
        return int.from_bytes(self.slice(pos, size), 'little')

    def width(self, field):
        if isinstance(field, int):
            return field
        if field in ('s', 'g', 'b'):
            return 4 if self.heap_sizes & {'s': 1, 'g': 2, 'b': 4}[field] else 2
        if field.startswith('t'):
            return 4 if self.rows[int(field[1:])] >= 65536 else 2
        bits, tables = CODED[field]
        return 4 if max(self.rows[t] for t in tables) >= (1 << (16 - bits)) else 2

    def string(self, index):
        start, size = self.streams['#Strings']
        if index >= size:
            raise ValueError('string index outside heap')
        end = self.data.index(0, start + index, start + size)
        return self.data[start + index:end].decode('utf-8')

    def assemblies(self, table=35):
        """Yield (name, version, row offset); table 32 reads a package's own AssemblyVersion."""
        start, size = self.tables[table]
        for i in range(self.rows[table]):
            row = start + i * size
            version_at = row + (4 if table == 32 else 0)
            name_at = row + (16 if table == 32 else 12) + self.width('b')
            yield self.string(self.number(name_at, self.width('s'))), struct.unpack_from('<HHHH', self.data, version_at), row


def assembly_floor(year, name):
    # Observed AssemblyVersion in nice3point.revit.api.{revitapi,revitapiui,adwindows}
    # at FLOOR[year]. RevitAPI/UI agree; AdWindows uses its own version series.
    # In particular, package 2025.0.2 carries 25.0.0.0 (file version 25.0.2.419),
    # not 25.0.2.0. That file version is unavailable in an AssemblyRef. Re-read
    # these Assembly tables when raising FLOOR; do not infer identity from a package version.
    versions = {
        2025: ((25, 0, 0, 0), (5, 1, 2, 1)),
        2026: ((26, 0, 4, 0), (5, 2, 0, 2)),
        2027: ((27, 0, 0, 0), (5, 4, 2, 2)),
    }
    return versions[year][1 if name == 'AdWindows' else 0]


def check(directory):
    year = 2000 + int(directory.name.removeprefix('R'))
    if directory.name != f'R{year - 2000}' or year not in FLOOR:
        raise ValueError(f'{directory}: expected a staged R25/R26/R27 directory with a recorded floor')
    print(f'{directory}: package floor {".".join(map(str, FLOOR[year]))} (decision #26)')
    failed = False
    for rel in ('app/RST.Engine.dll', 'addins/RST.Bootstrap.dll'):
        path = directory / rel
        refs = list(Metadata(path).assemblies())
        for required in ('RevitAPI', 'RevitAPIUI'):
            if not any(name == required for name, _, _ in refs):
                raise ValueError(f'{path}: missing {required} AssemblyRef')
        for name, version, _ in refs:
            if name not in ('RevitAPI', 'RevitAPIUI', 'AdWindows'):
                continue
            floor = assembly_floor(year, name)
            ok = version <= floor
            failed |= not ok
            print(f'{"OK" if ok else "FAIL"}: {path}: {name} {".".join(map(str, version))} '
                  f'{"<=" if ok else ">"} floor {".".join(map(str, floor))}')
    return failed


def self_test():
    import contextlib
    import io
    from unittest.mock import patch

    def fixture(refs, extra):
        # Minimal PE/CLI image: one section, #~ and #Strings, AssemblyRef rows only.
        strings = bytearray(b'\0')
        rows = bytearray()
        for name, version in refs:
            rows += struct.pack('<HHHHIHHHH', *version, 0, 0, len(strings), 0, 0)
            strings += name.encode('ascii') + b'\0'
        tables = struct.pack('<IBBBBQQI', 0, 2, 0, 0x40 if extra else 0, 1, 1 << 35, 0, len(refs))
        if extra:
            tables += struct.pack('<I', 0x12345678)
        tables += rows
        root = bytearray(b'BSJB' + struct.pack('<HHII', 1, 1, 0, 4) + b'v4\0\0')
        root += struct.pack('<HH', 0, 2)
        root += struct.pack('<II', 56, len(tables)) + b'#~\0\0'
        root += struct.pack('<II', 56 + len(tables), len(strings)) + b'#Strings\0\0\0\0'
        root += tables + strings
        data = bytearray(0x248) + root
        data[:2] = b'MZ'
        struct.pack_into('<I', data, 0x3c, 0x80)
        data[0x80:0x84] = b'PE\0\0'
        struct.pack_into('<H', data, 0x86, 1)
        struct.pack_into('<H', data, 0x94, 224)
        struct.pack_into('<H', data, 0x98, 0x10b)
        struct.pack_into('<II', data, 0x98 + 96 + 14 * 8, 0x2000, 72)
        struct.pack_into('<IIII', data, 0x178 + 8, len(data) - 0x200, 0x2000, len(data) - 0x200, 0x200)
        struct.pack_into('<II', data, 0x208, 0x2048, len(root))
        return bytes(data)

    floor_refs = [('RevitAPI', (26, 0, 4, 0)), ('RevitAPIUI', (26, 0, 4, 0))]
    cases = 0
    for extra in (False, True):
        for refs, failed in ((floor_refs, False),
                             ([('RevitAPI', (26, 1, 0, 0))] + floor_refs, True)):
            with patch.object(Path, 'read_bytes', return_value=fixture(refs, extra)):
                assert [(name, version) for name, version, _ in Metadata('fixture').assemblies()] == refs
                with contextlib.redirect_stdout(io.StringIO()):
                    assert check(Path('R26')) == failed, (extra, refs)
            cases += 1
    print(f'Self-test passed ({cases} cases)')
    return 0


def main():
    if sys.argv[1:] == ['--self-test']:
        return self_test()
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    failed = False
    for arg in sys.argv[1:]:
        try:
            failed |= check(Path(arg))
        except (OSError, ValueError, KeyError, struct.error) as ex:
            print(f'FAIL: {arg}: {ex}', file=sys.stderr)
            failed = True
    return int(failed)


if __name__ == '__main__':
    sys.exit(main())
