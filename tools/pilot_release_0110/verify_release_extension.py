"""Inspect post-build104 pilot additions in trusted downloaded release archives.

Run the unchanged tools/release_0110/verify_bundles.py first. This bounded
extension binds to that receipt, inspects CLR metadata and embedded PYZ, and
never loads a DLL or executes frozen Python. Requires dnfile and PyInstaller.
"""
import argparse
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import re
import struct
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
BASE_PATH = ROOT / 'tools/release_0110/verify_bundles.py'
spec = importlib.util.spec_from_file_location('release_base', BASE_PATH)
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
require = base.require
NEW_MODULES = ('frame_solution_selection', 'frame_solution_catalog')
NEW_COMMANDS = {'ATFPROJECT', 'ATFZONEPARAMS', 'ATFNODE'}
COMMON_TYPES = {'FacadeSafety.FacadeProjectParameterStore'}
FRAME_TYPES = {'AFramePlugin.' + name for name in (
    'FrameSolutionSelection', 'FrameProjectCommand', 'FrameProjectParameters',
    'FrameZoneParameters', 'FrameProjectForm', 'FrameZoneParametersForm',
    'FrameParameterResolver', 'FrameNodeCommand', 'FrameNodeForm',
    'FrameNodeGeometry', 'FrameNodeGeometryResult', 'FrameNodeSnapshot',
    'FrameNodeRenderer', 'FrameNodeStore', 'FrameMountingAssessment',
    'FrameMountingAssessmentResult', 'FrameMountingNominalChain')}


def compressed_uint(data, offset):
    """ECMA-335 compressed unsigned integer, used by signatures/SerString."""
    require(offset < len(data), 'Truncated compressed integer')
    first = data[offset]
    size, mask = (1, 0x7f) if first < 0x80 else (2, 0x3f) if first < 0xc0 else (4, 0x1f)
    require(first < 0xe0 and offset + size <= len(data), 'Invalid compressed integer')
    value = first & mask
    for byte in data[offset + 1:offset + size]:
        value = (value << 8) | byte
    return value, offset + size


def command_attribute(pe, attribute):
    """Decode only the actual (string, CommandFlags) constructor in this pilot."""
    constructor = attribute.Type.row
    if not hasattr(constructor, 'Class'):
        return None
    owner = constructor.Class.row
    if (str(getattr(owner, 'TypeNamespace', '')), str(getattr(owner, 'TypeName', ''))) != (
            'Autodesk.AutoCAD.Runtime', 'CommandMethodAttribute'):
        return None
    signature = constructor.Signature.value
    require(str(constructor.Name) == '.ctor' and signature[:5] == bytes.fromhex('2002010e11'),
            'Unsupported CommandMethodAttribute constructor')
    coded, end = compressed_uint(signature, 5)
    require(end == len(signature) and coded & 3 == 1, 'Expected CommandFlags TypeRef')
    require(0 < coded >> 2 <= len(pe.net.mdtables.TypeRef.rows), 'Invalid CommandFlags reference')
    flags_type = pe.net.mdtables.TypeRef.rows[(coded >> 2) - 1]
    require(str(flags_type.TypeNamespace) == 'Autodesk.AutoCAD.Runtime' and
            str(flags_type.TypeName) == 'CommandFlags', 'Wrong command enum type')
    data = attribute.Value.value
    require(data[:2] == b'\x01\x00', 'Wrong custom attribute prolog')
    length, start = compressed_uint(data, 2)
    end = start + length
    require(end + 6 == len(data) and data[end + 4:] == b'\0\0',
            'Truncated command attribute or unsupported named arguments')
    return data[start:end].decode('utf-8'), struct.unpack_from('<I', data, end)[0]


def managed_manifest(data, bundle):
    import dnfile
    pe = dnfile.dnPE(data=data)
    require(pe.net and pe.net.mdtables, bundle + ': CLR metadata missing')
    types = {str(row.TypeNamespace) + '.' + str(row.TypeName) for row in pe.net.mdtables.TypeDef}
    required = COMMON_TYPES | (FRAME_TYPES if bundle == 'AFrame' else set())
    require(required <= types, bundle + ': absent pilot CLR types ' + str(sorted(required - types)))
    method_owners = {method.row_index: str(row.TypeNamespace) + '.' + str(row.TypeName)
                     for row in pe.net.mdtables.TypeDef for method in row.MethodList}
    commands = []
    for attribute in pe.net.mdtables.CustomAttribute:
        decoded = command_attribute(pe, attribute)
        if decoded is None:
            continue
        require(attribute.Parent.table.name == 'MethodDef', 'Command attached to non-method')
        method = attribute.Parent.row
        require(method.Rva and method.Flags.mdPublic, 'Command method has no public implementation')
        commands.append({'global': decoded[0], 'flags': decoded[1],
                         'type': method_owners[attribute.Parent.row_index], 'method': str(method.Name)})
    require(len({c['global'] for c in commands}) == len(commands), 'Duplicate command attributes')
    return {'required_types_found': sorted(required), 'type_count': len(types),
            'commands': sorted(commands, key=lambda c: c['global']), 'execution_performed': False}


def frozen_pilot_modules(data, source_sha):
    from PyInstaller.archive.readers import CArchiveReader
    with tempfile.TemporaryDirectory(prefix='pilot_release_inspect_') as directory:
        path = Path(directory) / 'frame_engine.exe'
        path.write_bytes(data)
        archive = CArchiveReader(str(path))
        require('frame_engine' in archive.toc, 'Frozen frame_engine entrypoint missing')
        names = [name for name, entry in archive.toc.items() if entry[-1] == 'z']
        require(len(names) == 1, 'Expected exactly one PYZ')
        pyz = archive.open_embedded_archive(names[0])
        result = {}
        for name in NEW_MODULES:
            require(name in pyz.toc, 'Missing pilot PYZ module ' + name)
            raw = pyz.extract(name, raw=True)
            require(raw, 'Empty pilot PYZ module ' + name)
            source = 'AFrame/engine/' + name + '.py'
            result[name] = {'bytecode_sha256': base.sha(raw), 'bytes': len(raw),
                            'source_path': source, 'source_sha256': base.sha(base.source_bytes(source_sha, source))}
        return {'required_modules': result, 'pyz_modules': len(pyz.toc),
                'extraction': 'Standard reader decodes PYZ TOC with marshal; module payload raw=True, no module unmarshal/import/exec',
                'source_bytecode_equivalence_claimed': False, 'execution_performed': False}


def verify_bundle(args, bundle, receipt):
    folder, dll, engine = base.BUNDLES[bundle]
    name = bundle + '.bundle.zip'
    paths = [args.downloads / tag / name for tag in ('latest', 'build-' + str(args.build))]
    data, immutable = (path.read_bytes() for path in paths)
    require(data == immutable, bundle + ': latest/build archives differ')
    previous = receipt['bundles'][bundle]
    require(base.sha(data) == previous['archive_sha256'], bundle + ': archives differ from base receipt')
    prefix = bundle + '.bundle/'
    with zipfile.ZipFile(paths[0]) as z:
        entries = [base.checked_name(item.filename) for item in z.infolist() if not item.is_dir()]
        require(len({name.casefold() for name in entries}) == len(entries), 'Duplicate ZIP paths')
        info = json.loads(z.read(prefix + 'Contents/net48/build-info.json').decode('utf-8-sig'))
        require(info['sha'] == args.source_sha and str(info['build']) == str(args.build), 'Wrong build-info')
        dll_data = z.read(prefix + 'Contents/net48/' + dll)
        result = {'archive_sha256': base.sha(data), 'dll': base.pe_header(dll_data, dll),
                  'metadata': managed_manifest(dll_data, bundle)}
        xml_data = z.read(prefix + 'PackageContents.xml')
        source_xml = folder + '/bundle/' + prefix + 'PackageContents.xml'
        require(xml_data.replace(b'\r\n', b'\n') == base.source_bytes(args.source_sha, source_xml).replace(b'\r\n', b'\n'),
                'PackageContents.xml differs from source SHA')
        declarations = ET.fromstring(xml_data).findall('.//Commands/Command')
        xml_commands = [entry.attrib['Global'] for entry in declarations]
        require(len(set(xml_commands)) == len(xml_commands), 'Duplicate XML command')
        require(all(entry.attrib.get('Local') == entry.attrib['Global'] for entry in declarations), 'Unexpected local command alias')
        sources = base.git('ls-tree', '-r', '--name-only', args.source_sha, '--', folder + '/src').decode().splitlines()
        source_commands = {}
        for source in sources:
            if source.endswith('.cs'):
                for command in re.findall(r'\[CommandMethod\(\s*"([^"]+)"', base.source_bytes(args.source_sha, source).decode('utf-8-sig')):
                    require(command not in source_commands, 'Duplicate source command')
                    source_commands[command] = source
        actual_commands = {entry['global'] for entry in result['metadata']['commands']}
        require(actual_commands == set(xml_commands) == set(source_commands), bundle + ': CLR/XML/source commands differ')
        if bundle == 'AFrame':
            require(NEW_COMMANDS <= actual_commands, 'New pilot commands absent')
            exe_data = z.read(prefix + 'Contents/engine/' + engine)
            result['engine'] = base.pe_header(exe_data, engine)
            result['engine']['pilot_modules'] = frozen_pilot_modules(exe_data, args.source_sha)
        result['command_source_manifest'] = source_commands
        result['xml_commands'] = sorted(xml_commands)
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--downloads', type=Path, required=True)
    parser.add_argument('--build', type=int, required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--base-receipt', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    result = {'schema': 'pilot_release_extension/1', 'status': 'FAIL',
              'recorded_utc': datetime.now(timezone.utc).isoformat(), 'build': args.build,
              'source_sha': args.source_sha, 'verifier_sha256': base.sha(Path(__file__).read_bytes()),
              'base_verifier_sha256': base.sha(BASE_PATH.read_bytes()), 'bundles': {},
              'live_autocad_checked': False, 'windows_exe_executed': False,
              'dll_loaded_in_autocad': False, 'engineering_approval': False}
    try:
        receipt = json.loads(args.base_receipt.read_text(encoding='utf-8'))
        require(receipt['status'] == 'PASS' and receipt['build'] == args.build and
                receipt['release_sha'] == args.source_sha, 'Base receipt is not PASS for this build/SHA')
        require(receipt['verifier_sha256'] == result['base_verifier_sha256'], 'Base inspector differs from receipt')
        require(base.git('rev-parse', args.source_sha).decode().strip() == args.source_sha, 'Supply full existing source SHA')
        result['base_receipt_sha256'] = base.sha(args.base_receipt.read_bytes())
        for bundle in ('AFacades', 'AClad', 'AFrame'):
            result['bundles'][bundle] = verify_bundle(args, bundle, receipt)
            print(bundle + ': PASS (pilot CLR types, CLR/XML/source command manifest)')
        result['status'] = 'PASS'
    except Exception as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
        print(result['error'])
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return int(result['status'] != 'PASS')


if __name__ == '__main__':
    raise SystemExit(main())
