#!/usr/bin/env python3
"""Linux download for SoloStone, from a stage folder (installer/stage.py).

The Linux launcher is a native program; the game is still the Windows client, which it runs under Wine in a network
namespace. This builds that launcher and puts beside it the same payload that build.ps1 puts beside the Windows one:
the loader files listed in loader-inputs.json (hash-checked again), the plugin and engine, client.config,
doorstop_config.ini and payload.json. It adds DXVK, which runs the game's Direct3D 11 on Vulkan, from the release
pinned in linux-inputs.json, and fonts/LegacyRuntime.ttf, Liberation Sans renamed so that the mod's labels have text under Wine (rename_font).
Then the licence texts and SOURCE.txt, and a tarball of the whole folder.

    python3 installer/stage.py --out <stage folder> --version <version>
    python3 installer/package_linux.py --staged <stage folder> --version <version> --out <folder> [--dotnet <dotnet>] [--cache <folder>]

Leaves <out>/SoloStone-linux-<version>/ and <out>/SoloStone-linux-<version>.tar.gz. No Windows, no private keys.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import gzip
import io
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

sys.dont_write_bytecode = True

INSTALLER = Path(__file__).resolve().parent
PRODUCT = INSTALLER.parent
LEGAL = ['NOTICE', 'LICENSE', 'LICENSE-ADDITIONAL-PERMISSION.md', 'SOURCE.txt', 'LICENSES']
# The same text build.ps1 writes. Doorstop loads BepInEx's preloader, with the full Mono class libraries first on the search path.
DOORSTOP_CONFIG = ('[General]\r\nenabled=true\r\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\nredirect_output_log=false\r\n'
                   'ignore_disable_switch=false\r\n\r\n[UnityMono]\r\ndll_search_path_override=BepInEx\\unstripped_corlib\r\n')


# A release version: it names the output folder, which an earlier run's copy is deleted from.
VERSION = re.compile(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?')
# Written into the folder this script makes, so a second run only ever deletes a folder it made.
MARKER = '.package-linux'


def fail(message):
    print('PACKAGE REFUSED: ' + message, file=sys.stderr)
    sys.exit(1)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def regular(path):
    if path.is_symlink() or not path.is_file():
        fail('not a regular file: %s' % path)
    return path


def build_launcher(dotnet, app):
    """Self-contained, one file: a player needs no .NET installed."""
    command = [dotnet, 'publish', str(INSTALLER / 'SoloStone.Linux' / 'SoloStone.Linux.csproj'), '-c', 'Release', '-r', 'linux-x64',
               '--self-contained', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none',
               '-p:NuGetAudit=false', '-o', str(app), '-nologo', '-v', 'q']
    result = subprocess.run(command, cwd=PRODUCT, capture_output=True, text=True)
    if result.returncode != 0:
        fail('the Linux launcher did not build:\n' + result.stdout + result.stderr)
    regular(app / 'solostone')


def payload(staged, app, revision):
    loader_list = json.loads((INSTALLER / 'loader-inputs.json').read_text(encoding='utf-8'))
    if loader_list.get('schema') != 1:
        fail('loader-inputs.json has an unknown schema')
    listed = {entry['path']: entry['sha256'].upper() for entry in loader_list['files']}
    loader = staged / 'loader'
    present = {p.relative_to(loader).as_posix() for p in loader.rglob('*')
               if (p.is_file() or p.is_symlink()) and p.name != '.solostone-loader'}  # loader_inputs.py's own marker
    if present != set(listed):
        fail('the stage loader folder differs from loader-inputs.json: %s' % sorted(present ^ set(listed)))
    target = app / 'payload'
    for path, expected in listed.items():
        source = regular(loader / path)
        if sha256(source) != expected:
            fail('loader file differs from loader-inputs.json: ' + path)
        (target / path).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target / path)
    plugins = target / 'BepInEx' / 'plugins'
    plugins.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(regular(staged / 'plugin' / 'SoloStone.Plugin.dll'), plugins / 'SoloStone.Plugin.dll')
    shutil.copyfile(regular(staged / 'plugin' / 'SabberStoneCore.dll'), plugins / 'SabberStoneCore.dll')
    shutil.copyfile(regular(PRODUCT / 'client.config'), target / 'client.config')
    (target / 'doorstop_config.ini').write_bytes(DOORSTOP_CONFIG.encode('ascii'))
    pins = {p.relative_to(target).as_posix(): sha256(p) for p in sorted(target.rglob('*')) if p.is_file()}
    record = {'schema': 1, 'bepInExVersion': loader_list['bepInExVersion'], 'doorstopVersion': loader_list['doorstopVersion'],
              'sourceRevision': revision, 'engineSha256': pins['BepInEx/plugins/SabberStoneCore.dll'],
              'pluginSha256': pins['BepInEx/plugins/SoloStone.Plugin.dll'], 'files': pins}
    (app / 'payload.json').write_text(json.dumps(record, indent=1) + '\n', encoding='ascii')


def pinned(key):
    inputs = json.loads((INSTALLER / 'linux-inputs.json').read_text(encoding='utf-8'))
    if inputs.get('schema') != 1:
        fail('linux-inputs.json has an unknown schema')
    return inputs[key]


def download(source, cache):
    """The archive at source['url'], checked against source['archiveSha256']; kept in cache when one is given."""
    name = source['url'].rsplit('/', 1)[-1]
    cached = Path(cache) / (source['archiveSha256'] + '-' + name) if cache else None
    if cached and cached.is_file():
        data = cached.read_bytes()
    else:
        try:
            with urllib.request.urlopen(urllib.request.Request(source['url'], headers={'User-Agent': 'SoloStone-build'}), timeout=120) as response:
                data = response.read()
        except OSError as error:
            fail('%s: download failed (%s)' % (source['url'], error))
    if hashlib.sha256(data).hexdigest() != source['archiveSha256']:
        fail('%s: hash differs from linux-inputs.json' % source['url'])
    if cached and not cached.is_file():
        cached.parent.mkdir(parents=True, exist_ok=True)
        cached.write_bytes(data)
    return data


def dxvk(app, cache):
    """DXVK's 64-bit d3d11.dll and dxgi.dll, from the archive pinned in linux-inputs.json."""
    source = pinned('dxvk')
    data = download(source, cache)
    target = app / 'dxvk'
    target.mkdir()
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
        top = 'dxvk-' + source['version'] + '/'
        for path in source['files']:
            member = archive.getmember(top + path)
            if not member.isfile():
                fail('DXVK archive entry is not a file: ' + path)
            (target / path.rsplit('/', 1)[-1]).write_bytes(archive.extractfile(member).read())


# Unity's built-in IMGUI font is found by family name, LegacyRuntime or else Arial, in the Windows Fonts folder; a Wine prefix has neither.
# Liberation Sans, renamed LegacyRuntime, fills the gap. Its licence (SIL OFL 1.1) reserves the names Liberation, Arimo, Tinos and Cousine for
# the original, so every name record that names the font is replaced, and the trademark record, which names Liberation, is dropped.
FONT_FAMILY = 'LegacyRuntime'
FONT_NAMES = {1: FONT_FAMILY, 3: 'SoloStone: ' + FONT_FAMILY + ' 2.1.5', 4: FONT_FAMILY, 6: FONT_FAMILY + '-Regular'}
FONT_DROPPED = {7, 16, 17, 18, 21, 22}


def rename_font(data, names, dropped):
    """The TrueType font data with its name table rebuilt: the records in names replaced, those in dropped removed, the rest kept.
    Table checksums and head.checkSumAdjustment are recomputed; every other table keeps its bytes."""
    def checksum(block):
        block += b'\0' * (-len(block) % 4)
        return sum(struct.unpack('>%dI' % (len(block) // 4), block)) & 0xFFFFFFFF
    version, count = struct.unpack_from('>IH', data, 0)
    if version not in (0x00010000, 0x74727565):
        fail('the pinned font is not TrueType')
    tables = {}
    order = []
    for index in range(count):
        tag, _, offset, length = struct.unpack_from('>4sIII', data, 12 + 16 * index)
        tables[tag] = bytearray(data[offset:offset + length])
        order.append(tag)
    if b'name' not in tables or b'head' not in tables:
        fail('the pinned font has no name or head table')
    table = bytes(tables[b'name'])
    form, records, storage = struct.unpack_from('>HHH', table, 0)
    if form != 0:
        fail('the pinned font has a name table this build does not read')
    kept = []
    for index in range(records):
        platform, encoding, language, name_id, length, offset = struct.unpack_from('>HHHHHH', table, 6 + 12 * index)
        if name_id in dropped:
            continue
        text = table[storage + offset:storage + offset + length]
        if name_id in names:
            if platform == 3 or platform == 0:
                text = names[name_id].encode('utf-16-be')
            elif platform == 1 and encoding == 0:
                text = names[name_id].encode('mac_roman')
            else:
                fail('the pinned font has a name record this build cannot rewrite (platform %d)' % platform)
        kept.append((platform, encoding, language, name_id, text))
    kept.sort(key=lambda record: record[:4])
    header = struct.pack('>HHH', 0, len(kept), 6 + 12 * len(kept))
    strings = b''
    for platform, encoding, language, name_id, text in kept:
        header += struct.pack('>HHHHHH', platform, encoding, language, name_id, len(text), len(strings))
        strings += text
    tables[b'name'] = bytearray(header + strings)
    tables[b'head'][8:12] = b'\0\0\0\0'
    directory = data[:12]
    offset = 12 + 16 * count
    body = b''
    head = 0
    for tag in sorted(order):
        block = bytes(tables[tag])
        if tag == b'head':
            head = offset + len(body)
        directory += struct.pack('>4sIII', tag, checksum(block), offset + len(body), len(block))
        body += block + b'\0' * (-len(block) % 4)
    font = bytearray(directory + body)
    struct.pack_into('>I', font, head + 8, (0xB1B0AFBA - checksum(bytes(font))) & 0xFFFFFFFF)
    return bytes(font)


def imgui_font(app, cache):
    """fonts/LegacyRuntime.ttf: Liberation Sans Regular from the archive pinned in linux-inputs.json, renamed."""
    source = pinned('font')
    with tarfile.open(fileobj=io.BytesIO(download(source, cache)), mode='r:gz') as archive:
        member = archive.getmember(source['file'])
        if not member.isfile():
            fail('font archive entry is not a file: ' + source['file'])
        data = archive.extractfile(member).read()
    target = app / 'fonts'
    target.mkdir()
    (target / (FONT_FAMILY + '.ttf')).write_bytes(rename_font(data, FONT_NAMES, FONT_DROPPED))


def icon(app):
    """The launcher icon's largest image, for the applications menu. Every image in launcher.ico is a stored PNG."""
    data = (INSTALLER / 'assets' / 'launcher.ico').read_bytes()
    best = None
    for index in range(struct.unpack_from('<H', data, 4)[0]):
        width, _, _, _, _, _, size, offset = struct.unpack_from('<BBBBHHII', data, 6 + 16 * index)
        if data[offset:offset + 8] == b'\x89PNG\r\n\x1a\n' and (best is None or (width or 256) > best[0]):
            best = (width or 256, data[offset:offset + size])
    if best is None:
        fail('launcher.ico holds no PNG image')
    (app / 'solostone.png').write_bytes(best[1])


def output_folder(out, version):
    """<out>/SoloStone-linux-<version>, empty, with the marker in it. Refuses a version that isn't one, and a folder there that
    this script didn't make."""
    if not VERSION.fullmatch(version):
        fail('--version is not a release version: ' + version)
    app = out / ('SoloStone-linux-' + version)
    if app.is_symlink() or (app.exists() and not (app / MARKER).is_file()):
        fail('%s exists and was not made by this script; move or delete it yourself' % app)
    if app.exists():
        shutil.rmtree(app)
    app.mkdir(parents=True)
    (app / MARKER).write_bytes(b'')
    return app


def tarball(folder, path):
    """Sorted names, fixed owner and times, and no time in the gzip header: the same inputs give the same bytes."""
    def clean(info):
        info.uid = info.gid = 0
        info.uname = info.gname = ''
        info.mtime = 0
        return info
    entries = sorted(entry for entry in folder.rglob('*') if entry != folder / MARKER)
    with open(path, 'wb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, mtime=0) as packed, \
            tarfile.open(fileobj=packed, mode='w', format=tarfile.PAX_FORMAT) as tar:
        tar.add(folder, arcname=folder.name, recursive=False, filter=clean)
        for entry in entries:
            tar.add(entry, arcname=folder.name + '/' + entry.relative_to(folder).as_posix(), recursive=False, filter=clean)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--staged', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--dotnet', default=shutil.which('dotnet') or 'dotnet')
    parser.add_argument('--cache', default='', help='keeps downloaded archives, named <sha256>-<file>, as loader_inputs.py does')
    args = parser.parse_args()
    staged, out = Path(args.staged).resolve(), Path(args.out).resolve()
    for needed in ('plugin/SoloStone.Plugin.dll', 'plugin/SabberStoneCore.dll', 'release.json', 'NOTICE.txt', 'loader', 'legal'):
        if not (staged / needed).exists():
            fail('the stage folder is missing ' + needed)
    release = json.loads((staged / 'release.json').read_text(encoding='ascii'))
    if release.get('schema') != 1 or release.get('version') != args.version:
        fail('release.json does not match --version')
    if out == PRODUCT or PRODUCT in out.parents:
        fail('choose an output folder outside the checkout')
    app = output_folder(out, args.version)
    build_launcher(args.dotnet, app)
    payload(staged, app, release['sourceRevision'])
    dxvk(app, args.cache or None)
    imgui_font(app, args.cache or None)
    icon(app)
    shutil.copyfile(staged / 'release.json', app / 'release.json')
    shutil.copyfile(staged / 'NOTICE.txt', app / 'NOTICE.txt')
    for name in LEGAL:
        source = staged / 'legal' / name
        if source.is_dir():
            shutil.copytree(source, app / name)
        else:
            shutil.copyfile(regular(source), app / name)
    archive = out / (app.name + '.tar.gz')
    tarball(app, archive)
    print('Linux download: %s (%s)' % (archive, sha256(archive)))


if __name__ == '__main__':
    main()
