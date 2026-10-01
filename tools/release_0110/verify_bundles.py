"""Verify downloaded latest/build-N bundles without executing their DLL/EXE.

Requires Python 3.12, PyInstaller 6.22.3 and dnfile. All source comparisons use
an explicit git commit, so concurrent working-tree development cannot affect
this release receipt. Inputs must come from this repository's trusted release;
PyInstaller metadata is parsed, never imported or executed.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PurePosixPath
import struct
import subprocess
import sys
import tempfile
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BUNDLES = {
    'ATableSpec': ('ATableSpec', 'AtSpecPlugin.dll', 'dxf_spec.exe'),
    'ABlockGen': ('ABlockGen', 'ABlockGenPlugin.dll', 'vitrage_engine.exe'),
    'AFacades': ('Facades', 'AFacadesPlugin.dll', 'facades_engine.exe'),
    'AClad': ('AClad', 'ACladPlugin.dll', 'clad_engine.exe'),
    'AFrame': ('AFrame', 'AFramePlugin.dll', 'frame_engine.exe'),
}
REQUIRED_MODULES = {
    'AFacades': ['facade_zones'],
    'AClad': ['cladding_plan', 'polygon_clip', 'tile_pattern'],
    'AFrame': ['frame_plan', 'frame_calc', 'frame_rules', 'frame_topology',
               'frame_spatial', 'frame_connections', 'frame_catalog'],
}
REQUIRED_TYPES = {
    'AFacades': ['ConnectionTableData', 'QuantityTableIdentity', 'QuantityConnectionPassport'],
    'AClad': ['QuantityConnectionPassport'],
    'AFrame': ['FrameQuantities', 'QuantityConnectionPassport'],
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args])


def source_bytes(ref, path):
    return git('show', ref + ':' + path)


def checked_name(name):
    value = name.replace('\\', '/')
    path = PurePosixPath(value)
    require(not path.is_absolute() and '..' not in path.parts and ':' not in value,
            'Unsafe ZIP entry: ' + repr(name))
    require(value and not value.startswith('/'), 'Empty/absolute ZIP entry')
    return value


def pe_header(data, name):
    require(len(data) >= 128 and data[:2] == b'MZ', name + ': DOS header missing')
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    require(offset + 24 <= len(data) and data[offset:offset + 4] == b'PE\0\0',
            name + ': PE signature missing')
    machine = struct.unpack_from('<H', data, offset + 4)[0]
    require(machine == 0x8664, name + ': expected AMD64 image')
    return {'machine': 'AMD64', 'sha256': sha(data), 'bytes': len(data)}


def frozen_modules(data, bundle, entrypoint):
    from PyInstaller.archive.readers import CArchiveReader
    with tempfile.TemporaryDirectory(prefix='bundle_inspect_') as directory:
        path = Path(directory) / entrypoint
        path.write_bytes(data)
        archive = CArchiveReader(str(path))
        require(entrypoint[:-4] in archive.toc, bundle + ': frozen entrypoint missing')
        pyz_names = [name for name, entry in archive.toc.items() if entry[-1] == 'z']
        require(len(pyz_names) == 1, bundle + ': expected one embedded PYZ')
        pyz = archive.open_embedded_archive(pyz_names[0])
        found = {}
        for name in REQUIRED_MODULES.get(bundle, []):
            require(name in pyz.toc, bundle + ': missing frozen module ' + name)
            # raw=True decompresses bytecode but does not unmarshal/execute code.
            raw = pyz.extract(name, raw=True)
            require(raw, bundle + ': empty frozen module ' + name)
            found[name] = {'bytecode_sha256': sha(raw), 'bytes': len(raw)}
        return {'entrypoint': entrypoint[:-4], 'pyz_modules': len(pyz.toc),
                'required_modules': found, 'execution_performed': False}


def managed_types(data, bundle):
    import dnfile
    pe = dnfile.dnPE(data=data)
    require(pe.net and pe.net.mdtables, bundle + ': CLR metadata missing')
    types = {str(row.TypeName) for row in pe.net.mdtables.TypeDef}
    required = REQUIRED_TYPES.get(bundle, [])
    require(set(required).issubset(types), bundle + ': missing CLR types ' + str(set(required) - types))
    return {'type_count': len(types), 'required_types_found': required,
            'execution_performed': False}


def verify_bundle(args, bundle):
    folder, dll, engine = BUNDLES[bundle]
    archive_name = bundle + '.bundle.zip'
    paths = [args.downloads / tag / archive_name for tag in ('latest', 'build-' + str(args.build))]
    archives = [path.read_bytes() for path in paths]
    require(archives[0] == archives[1], bundle + ': latest differs from immutable release')
    archive_sha = sha(archives[0])
    for release_key in ('latest_release', 'immutable_release'):
        matches = [asset for asset in args.publication[release_key]['assets'] if asset['name'] == archive_name]
        require(len(matches) == 1, release_key + ': missing/duplicate asset ' + archive_name)
        require(matches[0]['size'] == len(archives[0]), release_key + ': asset size mismatch')
        if matches[0].get('digest'):
            require(matches[0]['digest'] == 'sha256:' + archive_sha, release_key + ': API digest mismatch')
    prefix = bundle + '.bundle/'
    source_prefix = folder + '/bundle/'
    source_paths = git('ls-tree', '-r', '--name-only', args.source_ref, '--', source_prefix).decode().splitlines()
    expected_source = {path[len(source_prefix):]: path for path in source_paths}
    if bundle == 'AFrame':
        expected_source[prefix + 'Contents/engine/systems.json'] = 'AFrame/engine/systems.json'
    if bundle == 'ATableSpec':
        expected_source[prefix + 'Contents/engine/mapping.yaml'] = 'ATableSpec/engine/mapping.yaml'
    dll_name, exe_name = prefix + 'Contents/net48/' + dll, prefix + 'Contents/engine/' + engine
    info_name = prefix + 'Contents/net48/build-info.json'
    expected = set(expected_source) | {dll_name, exe_name, info_name}
    with zipfile.ZipFile(paths[0]) as z:
        require(z.testzip() is None, bundle + ': CRC failure')
        entries = [(checked_name(item.filename), item) for item in z.infolist() if not item.is_dir()]
        names = [name for name, _ in entries]
        require(len({name.casefold() for name in names}) == len(names), bundle + ': duplicate ZIP paths')
        require(all(name.startswith(prefix) for name in names), bundle + ': wrong archive root')
        require(set(names) == expected, bundle + ': unexpected/missing files: ' + str(set(names) ^ expected))
        by_name = {name: item for name, item in entries}
        source_hashes = {}
        for name, source in expected_source.items():
            actual, original = z.read(by_name[name]), source_bytes(args.source_ref, source)
            exact = actual == original
            text_asset = Path(source).suffix.lower() in ('.xml', '.json', '.yaml')
            line_endings_only = text_asset and actual.replace(b'\r\n', b'\n') == original.replace(b'\r\n', b'\n')
            require(exact or line_endings_only, bundle + ': changed source asset ' + source)
            source_hashes[source] = {'git_blob_sha256': sha(original), 'archive_sha256': sha(actual),
                                     'comparison': 'exact_bytes' if exact else 'LF_CRLF_only'}
        info = json.loads(z.read(by_name[info_name]).decode('utf-8-sig'))
        wanted = {'bundle': bundle, 'build': str(args.build), 'attempt': '1', 'sha': args.sha,
                  'ref': args.ref, 'python': '3.12.10', 'pyinstaller': '6.22.3'}
        for key, value in wanted.items():
            require(str(info.get(key)) == value, bundle + ': wrong build-info ' + key)
        require(info.get('built_utc'), bundle + ': absent build timestamp')
        xml = ET.fromstring(z.read(by_name[prefix + 'PackageContents.xml']))
        for entry in xml.findall('.//ComponentEntry'):
            module = prefix + entry.attrib['ModuleName'].removeprefix('./')
            require(module in by_name, bundle + ': declared DLL absent')
        dll_data, exe_data = z.read(by_name[dll_name]), z.read(by_name[exe_name])
        dll_report = pe_header(dll_data, dll_name)
        dll_report['metadata'] = managed_types(dll_data, bundle)
        engine_report = pe_header(exe_data, exe_name)
        engine_report['frozen_archive'] = frozen_modules(exe_data, bundle, engine)
        return {'asset': archive_name, 'archive_sha256': archive_sha, 'bytes': len(archives[0]),
                'latest_equals_build_bytes': True, 'zip_crc': 'PASS', 'file_count': len(names),
                'exact_expected_file_set': True, 'build_info': info, 'source_asset_sha256': source_hashes,
                'dll': dll_report, 'engine': engine_report,
                'urls': {tag: 'https://github.com/zinchenko-denis/AutoCAD/releases/download/' + tag + '/' + archive_name
                         for tag in ('latest', 'build-' + str(args.build))}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--downloads', type=Path, required=True)
    parser.add_argument('--build', type=int, required=True)
    parser.add_argument('--sha', required=True)
    parser.add_argument('--source-ref', required=True)
    parser.add_argument('--ref', default='build-trigger')
    parser.add_argument('--publication-evidence', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    result = {'schema': 'facade_release_verification/1', 'status': 'FAIL',
              'observed_at_utc': datetime.now(timezone.utc).isoformat(), 'build': args.build,
              'release_sha': args.sha, 'source_ref': args.source_ref,
              'live_autocad_checked': False, 'windows_exe_executed': False,
              'dll_loaded_in_autocad': False, 'engineering_approval': False,
              'scope': 'Downloaded release bytes, source assets, PE/CLR/PyInstaller structure; no runtime acceptance.',
              'verifier_sha256': sha(Path(__file__).read_bytes()), 'bundles': {}}
    try:
        publication = json.loads(args.publication_evidence.read_text(encoding='utf-8'))
        for key in ('build_workflow', 'check_workflow'):
            run = publication[key]
            require(run['status'] == 'completed' and run['conclusion'] == 'success', key + ' is not green')
            require(run['head_sha'] == args.sha, key + ' belongs to another commit')
        require(publication['build_workflow']['run_number'] == args.build, 'Wrong build run number')
        require(publication['build_tag_sha'] == args.sha, 'Immutable tag SHA mismatch')
        require(publication['main_before'] == publication['main_after'], 'main changed during release')
        require(git('rev-parse', args.source_ref).decode().strip() == args.sha, 'source-ref must resolve to release SHA')
        if args.ref == 'build-trigger':
            base = publication['authorized_source_commit']
            changed = git('diff', '--name-only', base, args.sha).decode().splitlines()
            require(changed == ['.github/workflows/build.yml'], 'Release trigger changed product files')
            old = source_bytes(base, '.github/workflows/build.yml')
            new = source_bytes(args.sha, '.github/workflows/build.yml')
            require(new == old.replace(b'branches: [ main ]', b'branches: [ main, build-trigger ]', 1),
                    'Unexpected release trigger patch')
            result['trigger_patch_only'] = True
        result['publication'] = publication
        args.publication = publication
        for bundle in BUNDLES:
            result['bundles'][bundle] = verify_bundle(args, bundle)
            print(bundle + ': PASS (latest = build-' + str(args.build) + ', assets, PE, CLR, frozen modules)')
        result['status'] = 'PASS'
    except Exception as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
        print(result['error'], file=sys.stderr)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return 0 if result['status'] == 'PASS' else 1


if __name__ == '__main__':
    raise SystemExit(main())
