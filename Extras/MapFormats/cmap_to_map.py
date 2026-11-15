"""Recover an editable MapEditor .map from a compiled MatrixGame .cmap.

Requires a legacy .map as a schema template. Never overwrites existing files.
Unrecoverable editor-only information is listed in a JSON report.
"""

import argparse
from collections import deque
import json
import math
import re
import shutil
import struct
from pathlib import Path


FMT = {0: 'i', 1: 'I', 2: 'B', 3: 'f', 4: 'd', 5: 'H'}
SIZE = {0: 4, 1: 4, 2: 1, 3: 4, 4: 8, 5: 2}
SCALE = 20.0


def wide(data, pos):
    end = pos
    while end + 1 < len(data) and data[end:end + 2] != b'\0\0':
        end += 2
    if end + 1 >= len(data):
        raise ValueError('Unterminated UTF-16 string')
    return data[pos:end].decode('utf-16le'), end + 2


def wbytes(value):
    return (str(value) + '\0').encode('utf-16le')


def load(path):
    data = Path(path).read_bytes()
    if data[:8] != b'STRG\0\0\0\0':
        raise ValueError(f'{path}: expected uncompressed STRG v0')
    pos = 8
    count = struct.unpack_from('<I', data, pos)[0]
    pos += 4
    records = []
    for _ in range(count):
        name, pos = wide(data, pos)
        ncols = struct.unpack_from('<I', data, pos)[0]
        pos += 4
        cols = []
        for _ in range(ncols):
            col, pos = wide(data, pos)
            kind, length = struct.unpack_from('<II', data, pos)
            pos += 8
            if kind not in FMT or pos + length > len(data):
                raise ValueError(f'{path}: invalid column {name}/{col}')
            cols.append([col, kind, data[pos:pos + length]])
            pos += length
        records.append([name, cols])
    if pos != len(data):
        raise ValueError(f'{path}: trailing bytes')
    return records


def dump(records, path):
    raw = bytearray(b'STRG\0\0\0\0')
    raw += struct.pack('<I', len(records))
    for table, columns in records:
        raw += wbytes(table)
        raw += struct.pack('<I', len(columns))
        for name, kind, buf in columns:
            raw += wbytes(name)
            raw += struct.pack('<II', kind, len(buf))
            raw += buf
    Path(path).write_bytes(raw)


def get_col(records, table, column):
    for t, cols in records:
        if t == table:
            for c in cols:
                if c[0] == column:
                    return c
    raise KeyError(f'{table}/{column}')


def arrays(col):
    _, kind, buf = col
    table, count, size = struct.unpack_from('<III', buf)
    if size != SIZE[kind]:
        raise ValueError(f'invalid element size in {col[0]}')
    result = []
    for i in range(count):
        offset, length, capacity = struct.unpack_from('<III', buf, table + i * 12)
        end = offset + length * size
        if end > len(buf) or capacity < length:
            raise ValueError(f'invalid array in {col[0]}')
        result.append(buf[offset:end])
    return result


def unpack(col):
    kind = col[1]
    result = []
    for raw in arrays(col):
        if kind == 5:
            result.append(raw.decode('utf-16le').rstrip('\0'))
        elif kind == 2 and len(raw) > 1:
            result.append(raw)
        else:
            n = len(raw) // SIZE[kind]
            vals = struct.unpack('<' + FMT[kind] * n, raw)
            result.append(vals[0] if n == 1 else vals)
    return result


def set_arrays(col, values):
    kind = col[1]
    chunks = []
    for value in values:
        if kind == 5:
            chunks.append(wbytes(value))
        elif isinstance(value, bytes):
            chunks.append(value)
        elif isinstance(value, (tuple, list)):
            chunks.append(struct.pack('<' + FMT[kind] * len(value), *value))
        else:
            chunks.append(struct.pack('<' + FMT[kind], value))
    buf = bytearray(b'\0' * 12)
    entries = []
    for chunk in chunks:
        entries.append((len(buf), len(chunk) // SIZE[kind], len(chunk) // SIZE[kind]))
        buf += chunk
    table = len(buf)
    for entry in entries:
        buf += struct.pack('<III', *entry)
    struct.pack_into('<III', buf, 0, table, len(chunks), SIZE[kind])
    col[2] = bytes(buf)


def set_column(records, table, name, values, single=False):
    set_arrays(get_col(records, table, name), [values] if single else values)


def single(records, table, name):
    col = get_col(records, table, name)
    chunks = arrays(col)
    if not chunks:
        return ()
    if col[1] == 5:
        return chunks[0].decode('utf-16le').rstrip('\0')
    n = len(chunks[0]) // SIZE[col[1]]
    return struct.unpack('<' + FMT[col[1]] * n, chunks[0])


def properties(records):
    return dict(zip(unpack(get_col(records, 'properties', 'Name')),
                    unpack(get_col(records, 'properties', 'Value'))))


def world(value):
    if not math.isfinite(value) or abs(value) > 10_000_000:
        return 0, 0.0
    cell = math.floor(value / SCALE)
    fraction = value / SCALE - cell
    return cell, fraction


def split_world(records, table, xname='X', yname='Y'):
    xs = single(records, table, xname)
    ys = single(records, table, yname)
    return [world(x) for x in xs], [world(y) for y in ys]


def read_catalog(path):
    if not path:
        return []
    content = Path(path).read_text(encoding='cp1251', errors='replace')
    match = re.search(r'(?m)^objects\s*\{', content)
    if not match:
        raise ValueError('Object catalog section not found')
    tail = content[match.end():].split('\n}', 1)[0]
    return [tuple(x.strip() for x in line.split('=', 1))
            for line in tail.splitlines() if '=' in line]


def terrain_candidates(path):
    if not path:
        return {}
    content = Path(path).read_text(encoding='cp1251', errors='replace')
    match = re.search(r'(?m)^terrain\s+-sort\s*\{', content)
    if not match:
        return {}
    depth = 1
    current = None
    images = {}
    for line in content[match.end():].splitlines():
        stripped = line.strip()
        if depth == 1 and stripped.endswith('{'):
            current = stripped[:-1].strip().split(' -sort', 1)[0]
        if current and 'Matrix\\Ter\\' in stripped and '=' in stripped:
            image = stripped.split('=', 1)[1].strip().casefold()
            choices = images.setdefault(image, [])
            if current not in choices:
                choices.append(current)
        depth += line.count('{') - line.count('}')
        if depth == 0:
            break
    return images


def terrain_types(path):
    if not path:
        return {}
    content = Path(path).read_text(encoding='cp1251', errors='replace')
    match = re.search(r'(?m)^terrain\s+-sort\s*\{', content)
    if not match:
        return {}
    result = {}
    depth = 1
    current = None
    for line in content[match.end():].splitlines():
        stripped = line.strip()
        if depth == 1 and stripped.endswith('{'):
            current = stripped[:-1].strip().split(' -sort', 1)[0]
            result[current] = 0
        elif depth == 2 and current and stripped.startswith('Type='):
            result[current] = int(stripped.split('=', 1)[1])
        depth += line.count('{') - line.count('}')
        if depth == 0:
            break
    return result


def recover_texture_grid(game, strings, candidates, sx, sy, heights, flags):
    """Sample compiled bottom triangles at terrain cell centers."""
    labels = [None] * (sx * sy)
    if not candidates:
        return labels, 0
    dim = int(properties(game).get('TexUnionDim', 16))
    if not 1 <= dim <= 32:
        return labels, 0
    atlases = [struct.unpack('<' + 'i' * (len(raw) // 4), raw)
               for raw in arrays(get_col(game, 'texunions', 'Data'))]
    bottom = [struct.unpack('<' + 'i' * (len(raw) // 4), raw)
              for raw in arrays(get_col(game, 'bottom', 'Data'))]
    for raw in arrays(get_col(game, 'groups', 'Data')):
        if len(raw) < 8:
            continue
        _, _, sections_count = struct.unpack_from('<HHI', raw)
        pos = 8
        sections = []
        for _ in range(sections_count):
            if pos + 8 > len(raw):
                raise ValueError('Truncated group section')
            atlas_id, size = struct.unpack_from('<II', raw, pos)
            pos += 8
            if pos + size > len(raw) or size % 6:
                raise ValueError('Invalid group index buffer')
            indices = struct.unpack_from('<' + 'H' * (size // 2), raw, pos)
            pos += size
            sections.append((atlas_id, indices))
        if pos + 4 > len(raw):
            raise ValueError('Truncated group vertices')
        vertex_bytes = struct.unpack_from('<I', raw, pos)[0]
        pos += 4
        if vertex_bytes % 8 or pos + vertex_bytes > len(raw):
            raise ValueError('Invalid group vertex buffer')
        verts = [struct.unpack_from('<HHHH', raw, pos + i * 8)
                 for i in range(vertex_bytes // 8)]
        for atlas_id, indices in sections:
            if atlas_id >= len(atlases):
                continue
            atlas = atlases[atlas_id]
            for i in range(0, len(indices), 3):
                if max(indices[i:i + 3]) >= len(verts):
                    raise ValueError('Group vertex index out of range')
                a, b, c = (verts[index] for index in indices[i:i + 3])
                denominator = ((b[1] - c[1]) * (a[0] - c[0]) +
                               (c[0] - b[0]) * (a[1] - c[1]))
                if denominator == 0:
                    continue
                minx, maxx = max(0, min(a[0], b[0], c[0]) - 1), min(sx, max(a[0], b[0], c[0]) + 1)
                miny, maxy = max(0, min(a[1], b[1], c[1]) - 1), min(sy, max(a[1], b[1], c[1]) + 1)
                for y in range(miny, maxy):
                    for x in range(minx, maxx):
                        cell = y * sx + x
                        if not flags[y * (sx + 1) + x] & 1:
                            continue
                        px, py = x + 0.5, y + 0.5
                        u = ((b[1] - c[1]) * (px - c[0]) +
                             (c[0] - b[0]) * (py - c[1])) / denominator
                        v = ((c[1] - a[1]) * (px - c[0]) +
                             (a[0] - c[0]) * (py - c[1])) / denominator
                        w = 1 - u - v
                        if min(u, v, w) < -1e-6:
                            continue
                        tx = u * a[2] + v * b[2] + w * c[2]
                        ty = u * a[3] + v * b[3] + w * c[3]
                        ix, iy = int(tx) // 64, int(ty) // 64
                        if not 0 <= ix < dim or not 0 <= iy < dim:
                            continue
                        atlas_cell = iy * dim + ix
                        if atlas_cell >= len(atlas):
                            continue
                        bottom_id = atlas[atlas_cell]
                        if not 0 <= bottom_id < len(bottom) or not bottom[bottom_id]:
                            continue
                        string_id = bottom[bottom_id][0]
                        if not 0 <= string_id < len(strings):
                            continue
                        choices = candidates.get(strings[string_id].casefold(), [])
                        if not choices:
                            continue
                        is_water_height = heights[y * (sx + 1) + x] < 0.0
                        selected = next((name for name in choices
                                         if ('sea' in name.casefold()) == is_water_height), choices[0])
                        labels[cell] = selected
    sampled = sum(name is not None for name in labels)
    # Fill cells skipped by the compiled mesh from neighboring known cells.
    queue = deque(i for i, name in enumerate(labels) if name is not None)
    while queue:
        i = queue.popleft()
        x, y = i % sx, i // sx
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if not 0 <= nx < sx or not 0 <= ny < sy:
                continue
            neighbor = ny * sx + nx
            if labels[neighbor] is None and flags[ny * (sx + 1) + nx] & 1:
                labels[neighbor] = labels[i]
                queue.append(neighbor)
    return labels, sampled


def effect_catalog(path):
    if not path:
        return {}
    content = Path(path).read_text(encoding='cp1251', errors='replace')
    match = re.search(r'(?m)^effects\s+-sort\s*\{', content)
    if not match:
        return {}
    result = {}
    for line in content[match.end():].split('}', 1)[0].splitlines():
        if '=' in line and '?' in line:
            name, data = line.strip().split('=', 1)
            result[data.split('?', 1)[1].casefold()] = name
    return result


def recover_object_type(desc, catalog, idx):
    parts = desc.split('*')
    if len(parts) < 3 or not parts[0].lower().startswith('matrix\\obj\\'):
        return f'Recovered,Type{idx}', desc
    model = parts[0] + parts[1] + '.vo'
    candidates = [(name, data) for name, data in catalog
                  if data.split('?', 1)[0].casefold() == model.casefold()]
    if candidates:
        candidates.sort(key=lambda item: -sum(
            1 for part in parts[2:5] if part and part.casefold() in item[1].casefold()))
        return candidates[0]
    tex = parts[2] if len(parts) > 2 else ''
    return f'Recovered,Type{idx}', f'{model}?Tex={tex}'


def convert(source, template, output, catalog_path=None, keep_source=False):
    game = load(source)
    editor = load(template)
    if not get_col(game, 'points', 'Data') or not get_col(editor, 'points', 'Z'):
        raise ValueError('Not a compiled/source map pair')
    props = properties(game)
    nonfinite = {}
    extreme = {}
    for table in ('objects', 'buildings', 'cannons', 'robots', 'effects'):
        for axis in ('X', 'Y'):
            bad = [i for i, value in enumerate(single(game, table, axis))
                   if not math.isfinite(value)]
            if bad:
                nonfinite[f'{table}/{axis}'] = bad
            too_large = [i for i, value in enumerate(single(game, table, axis))
                         if math.isfinite(value) and abs(value) > 10_000_000]
            if too_large:
                extreme[f'{table}/{axis}'] = too_large
    sx, sy = int(props['SizeInUnitsX']), int(props['SizeInUnitsY'])
    if sx <= 0 or sy <= 0 or sx * sy > 4_000_000:
        raise ValueError('Invalid map dimensions')
    pbuf = arrays(get_col(game, 'points', 'Data'))[0]
    if len(pbuf) != (sx + 1) * (sy + 1) * 12:
        raise ValueError('Wrong compiled point count')
    heights = [struct.unpack_from('<f', pbuf, i * 12 + 4)[0]
               for i in range((sx + 1) * (sy + 1))]
    flags = [pbuf[i * 12 + 11] for i in range((sx + 1) * (sy + 1))]
    set_column(editor, 'points', 'Z', heights, single=True)

    source_props = properties(editor)
    for key in source_props:
        if key in props:
            source_props[key] = props[key]
        elif key.endswith(('ColorR', 'ColorG', 'ColorB', 'ColorA')):
            base, channel = key.rsplit('Color', 1)
            color_key = base + 'Color'
            if color_key in props:
                color = int(props[color_key])
                shift = {'B': 0, 'G': 8, 'R': 16, 'A': 24}[channel]
                source_props[key] = str(((color >> shift) & 255) / 255.0)
    counts = {'TexturesCount': 'textures', 'TopTexCount': 'toptextures',
              'SurfacesCount': 'surfaces', 'LightsCount': 'lights',
              'EffectsCount': 'effects', 'ObjectsTypesCount': 'objtypes',
              'ObjectsCount': 'objects', 'BuildingsCount': 'buildings',
              'CannonsCount': 'cannons', 'RobotsCount': 'robots'}

    strings = unpack(get_col(game, 'strings', 'String'))
    terrain_by_image = terrain_candidates(catalog_path)
    type_by_terrain = terrain_types(catalog_path)
    terrain_names = []
    for resource in strings:
        for name in terrain_by_image.get(resource.casefold(), []):
            if name not in terrain_names:
                terrain_names.append(name)
    terrain_names.sort(key=lambda name: type_by_terrain.get(name, 0))
    if terrain_names:
        set_column(editor, 'textures', 'Name', terrain_names)
        set_column(editor, 'textures', 'Type',
                   [type_by_terrain.get(name, 0) for name in terrain_names], single=True)
    else:
        terrain_names = unpack(get_col(editor, 'textures', 'Name'))
    labels, textured_cells = recover_texture_grid(game, strings, terrain_by_image,
                                                   sx, sy, heights, flags)
    first_land = next((i for i, name in enumerate(terrain_names)
                       if type_by_terrain.get(name, 0) == 0), 0)
    units = []
    for y in range(sy):
        for x in range(sx):
            i = y * sx + x
            if flags[y * (sx + 1) + x] & 1:
                palette_index = (terrain_names.index(labels[i]) if labels[i] in terrain_names
                                 else first_land)
                units.append((2 * palette_index + 1) * 1024)
            else:
                units.append(0)
    set_column(editor, 'units', 'Unit', units, single=True)
    template_catalog = list(zip(unpack(get_col(editor, 'objtypes', 'Name')),
                                unpack(get_col(editor, 'objtypes', 'Data'))))
    catalog = template_catalog + read_catalog(catalog_path)
    used_types = sorted(set(single(game, 'objects', 'Type')))
    type_map = {}
    objnames, objdata = [], []
    for compiled_type in used_types:
        if not 0 <= compiled_type < len(strings):
            raise ValueError(f'Invalid object type {compiled_type}')
        type_map[compiled_type] = len(objnames)
        name, data = recover_object_type(strings[compiled_type], catalog, compiled_type)
        if name in objnames:
            name = f'{name}~Recovered{compiled_type}'
        objnames.append(name)
        objdata.append(data)
    set_column(editor, 'objtypes', 'Name', objnames)
    set_column(editor, 'objtypes', 'Data', objdata)

    xy, yy = split_world(game, 'objects')
    set_column(editor, 'objects', 'Index', [type_map[t] for t in single(game, 'objects', 'Type')], single=True)
    for name, vals in [('X', [v[0] for v in xy]), ('Y', [v[0] for v in yy]),
                       ('XF', [v[1] for v in xy]), ('YF', [v[1] for v in yy]),
                       ('Height', single(game, 'objects', 'Z')),
                       ('Angle', single(game, 'objects', 'Angle')),
                       ('AngleX', single(game, 'objects', 'AngleX')),
                       ('AngleY', single(game, 'objects', 'AngleY')),
                       ('Scale', single(game, 'objects', 'Scale'))]:
        set_column(editor, 'objects', name, vals, single=True)

    for table in ('buildings', 'cannons', 'robots'):
        xy, yy = split_world(game, table)
        set_column(editor, table, 'X', [v[0] for v in xy], single=True)
        set_column(editor, table, 'Y', [v[0] for v in yy], single=True)
        if table in ('cannons', 'robots'):
            set_column(editor, table, 'XF', [v[1] for v in xy], single=True)
            set_column(editor, table, 'YF', [v[1] for v in yy], single=True)
        names = ('Side', 'Kind', 'Shadow', 'ShadowSize', 'Angle', 'Prop', 'AddH') if table == 'cannons' else (
            ('Side', 'Kind', 'Shadow', 'ShadowSize', 'Angle') if table == 'buildings' else
            ('Side', 'Shadow', 'ShadowSize', 'Group'))
        for name in names:
            val = list(single(game, table, name))
            if name == 'Side':
                val = [-1 if v == 255 else v for v in val]
            set_column(editor, table, name, val, single=True)
        if table == 'robots':
            set_column(editor, table, 'Units', unpack(get_col(game, table, 'Units')))

    ex, ey = split_world(game, 'effects')
    set_column(editor, 'effects', 'X', [v[0] for v in ex], single=True)
    set_column(editor, 'effects', 'Y', [v[0] for v in ey], single=True)
    set_column(editor, 'effects', 'XF', [v[1] for v in ex], single=True)
    set_column(editor, 'effects', 'YF', [v[1] for v in ey], single=True)
    set_column(editor, 'effects', 'Height', single(game, 'effects', 'Z'), single=True)
    effect_names = []
    effects_by_data = effect_catalog(catalog_path)
    for t in single(game, 'effects', 'Type'):
        raw = strings[t] if 0 <= t < len(strings) else ''
        effect_names.append(effects_by_data.get(raw.casefold(),
                            {'1': 'Smoke01', '2': 'Fire01'}.get(raw.split(',', 1)[0], 'Sound01')))
    set_column(editor, 'effects', 'Name', effect_names)

    # Template decoration would belong to a different map. Clear it.
    for table in ('toptextures', 'surfaces', 'lights'):
        cols = next(cols for name, cols in editor if name == table)
        for col in cols:
            set_arrays(col, [] if col[1] == 5 else [()])
    for key, table in counts.items():
        if key in source_props:
            cols = next(cols for name, cols in editor if name == table)
            source_props[key] = str(len(unpack(cols[0])) if cols[0][1] == 5 else
                                    len(single(editor, table, cols[0][0])))
    set_column(editor, 'properties', 'Value', [source_props[k] for k in
               unpack(get_col(editor, 'properties', 'Name'))])

    report = {
        'source': str(source), 'template': str(template),
        'size': [sx, sy], 'exact': ['points/Z', 'buildings', 'cannons',
                                  'robots/Units', 'most object transforms'],
        'approximate': ['units/Unit: base texture sampled from compiled mesh; random variants lost',
                        'objects/Height: compiled Z includes terrain snap',
                        'objtypes: recovered from compiled descriptors and optional catalog',
                        'effects/Name: inferred from compiled effect descriptor'],
        'unavailable': ['original texture palette and terrain variants',
                        'toptextures', 'surfaces', 'lights',
                        'editable AI paths and compiler intermediate data'],
        'counts': {table: len(single(editor, table, next(cols[0][0] for t, cols in editor if t == table)))
                   for table in ('objects', 'buildings', 'cannons', 'robots')},
        'nonfinite_positions_replaced_with_zero': nonfinite,
        'extreme_positions_replaced_with_zero': extreme,
        'terrain_palette_candidates': terrain_names,
        'terrain_cells_sampled_from_mesh': textured_cells,
    }
    dump(editor, output)
    Path(str(output) + '.report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    if keep_source:
        shutil.copyfile(source, str(output) + '.original.cmap')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('cmap', type=Path)
    parser.add_argument('template_map', type=Path)
    parser.add_argument('output_map', type=Path)
    parser.add_argument('--catalog', type=Path, help='MapEditor/Editor/data.txt for object names')
    parser.add_argument('--keep-source', action='store_true')
    args = parser.parse_args()
    for path in (args.output_map, Path(str(args.output_map) + '.report.json'),
                 Path(str(args.output_map) + '.original.cmap')):
        if path.exists():
            parser.error(f'output already exists: {path}')
    print(json.dumps(convert(args.cmap, args.template_map, args.output_map,
                             args.catalog, args.keep_source), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
