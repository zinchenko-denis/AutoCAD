"""Read-only supplement for the 15 October 2026 release; no DLL/EXE execution."""
import argparse
from datetime import datetime, timezone
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('base', ROOT / 'tools/release_0110/verify_bundles.py')
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
require = base.require
sha = base.sha


def latest_bytes(args, name):
    values = [(args.downloads / tag / name).read_bytes() for tag in ('latest', f'build-{args.build}')]
    require(values[0] == values[1], f'{name}: latest/build bytes differ')
    for key in ('latest_release', 'immutable_release'):
        assets = [a for a in args.publication[key]['assets'] if a['name'] == name]
        require(len(assets) == 1, f'{key}: missing/duplicate {name}')
        require(assets[0]['size'] == len(values[0]), f'{key}/{name}: size differs')
        if assets[0].get('digest'):
            require(assets[0]['digest'] == 'sha256:' + sha(values[0]), f'{key}/{name}: digest differs')
    return values[0]


def document(args, name):
    data = latest_bytes(args, name)
    original = base.source_bytes(args.source_sha, 'docs/user/' + name)
    exact = data == original
    line_endings_only = name.endswith('.txt') and data.replace(b'\r\n', b'\n') == original.replace(b'\r\n', b'\n')
    require(exact or line_endings_only, f'{name}: differs from source commit')
    result = {'sha256': sha(data), 'bytes': len(data), 'latest_equals_build': True,
              'source_comparison': 'exact_bytes' if exact else 'LF_CRLF_only',
              'source_sha256': sha(original)}
    if name.endswith('.docx'):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            require(archive.testzip() is None, name + ': CRC failed')
            names = [base.checked_name(i.filename) for i in archive.infolist()]
            require(len(set(n.casefold() for n in names)) == len(names), name + ': duplicate ZIP names')
            rels = ET.fromstring(archive.read('word/_rels/document.xml.rels'))
            expected = 'https://github.com/zinchenko-denis/AutoCAD/releases/tag/build-' + str(args.build)
            targets = [i.get('Target') for i in rels]
            require(expected in targets, name + ': current build link absent')
            document_xml = ET.fromstring(archive.read('word/document.xml'))
            text = ''.join(document_xml.itertext())
            require(str(args.build) in text, name + ': build number absent')
            result.update({'zip_crc': 'PASS', 'current_release_link': expected,
                           'plain_text_characters': len(text)})
    return result


def compiled_metadata(data, bundle):
    import dnfile
    pe = dnfile.dnPE(data=data)
    require(pe.net and pe.net.mdtables, bundle + ': no CLR metadata')
    types = {str(r.TypeNamespace) + '.' + str(r.TypeName): r for r in pe.net.mdtables.TypeDef}
    wanted = {bundle + 'Plugin.FacadesClassic': {'Init', 'OnIdle', 'CancelPending'}}
    if bundle == 'AFrame':
        wanted.update({'AFramePlugin.FrameSettings': {'EngineParams', 'AppendCornerDescription', 'HasUsableAxisInput'},
                       'AFramePlugin.FrameQuantities': {'ValidateCornerChannels', 'CornerChannelVertices'}})
    if bundle == 'AClad':
        wanted.update({'ACladPlugin.LayoutSafety': {'SetNumberChecked', 'SetNumberCheckedWithFailure'}})
    found = {}
    for type_name, methods in wanted.items():
        require(type_name in types, bundle + ': missing ' + type_name)
        method_rows = {str(m.row.Name): m.row for m in types[type_name].MethodList}
        require(methods <= method_rows.keys(), type_name + ': missing methods ' + str(methods - method_rows.keys()))
        require(all(method_rows[m].Rva > 0 for m in methods), type_name + ': absent method implementation')
        found[type_name] = sorted(methods)
    result = {'methods_with_implementation': found, 'dll_sha256': sha(data),
              'method_behavior_verified': False, 'dll_loaded': False}
    if bundle == 'AFrame':
        result['conditional_channel_constants_il'] = channel_constants(pe, types['AFramePlugin.FrameSettings'])
    return result


def channel_constants(pe, frame_settings):
    """Decode exact key/Double literal/set_Item sequences; never execute CLR."""
    from dncil.cil.body.reader import read_method_body_from_bytes
    from dncil.clr.token import StringToken, Token
    method = next(m.row for m in frame_settings.MethodList if str(m.row.Name) == 'EngineParams')
    body = read_method_body_from_bytes(pe.get_data(method.Rva))
    wanted = {'corner_channel_outer_overhang': 50.0, 'corner_channel_y_offset': 0.0}
    found = {}
    for index, instruction in enumerate(body.instructions):
        if instruction.opcode.name != 'ldstr' or not isinstance(instruction.operand, StringToken):
            continue
        key = str(pe.net.user_strings.get(instruction.operand.rid))
        if key not in wanted:
            continue
        require(key not in found, 'FrameSettings: duplicate channel key in IL')
        sequence = body.instructions[index:index + 4]
        require([item.opcode.name for item in sequence] == ['ldstr', 'ldc.r8', 'box', 'callvirt'],
                'FrameSettings: unexpected channel assignment IL')
        require(sequence[1].operand == wanted[key], 'FrameSettings: wrong channel Double literal')
        type_token, setter_token = sequence[2].operand, sequence[3].operand
        require(isinstance(type_token, Token) and isinstance(setter_token, Token), 'Invalid IL tokens')
        box_type = pe.net.mdtables.tables[type_token.table].rows[type_token.rid - 1]
        setter = pe.net.mdtables.tables[setter_token.table].rows[setter_token.rid - 1]
        require(str(box_type.TypeNamespace) == 'System' and str(box_type.TypeName) == 'Double',
                'FrameSettings: channel literal not boxed as Double')
        require(str(setter.Name) == 'set_Item', 'FrameSettings: channel literal not passed to indexed setter')
        found[key] = {'double_literal': sequence[1].operand, 'il_offset': instruction.offset,
                      'opcode_sequence': [item.opcode.name for item in sequence],
                      'boxed_type': 'System.Double', 'called_method': 'set_Item'}
    require(found.keys() == wanted.keys(), 'FrameSettings: required channel constants absent from IL')
    return {'method': 'AFramePlugin.FrameSettings.EngineParams', 'assignments': found,
            'branch_conditions_or_runtime_behavior_verified': False,
            'method_il_sha256': sha(body.get_bytes()), 'method_execution_performed': False}


def frozen_corners(data, source_sha):
    from PyInstaller.archive.readers import CArchiveReader
    with tempfile.TemporaryDirectory(prefix='corner_release_') as folder:
        path = Path(folder) / 'frame_engine.exe'
        path.write_bytes(data)
        archive = CArchiveReader(str(path))
        names = [name for name, info in archive.toc.items() if info[-1] == 'z']
        require(len(names) == 1, 'AFrame: expected one PYZ')
        pyz = archive.open_embedded_archive(names[0])
        require('frame_corners' in pyz.toc, 'AFrame: frame_corners missing from PYZ')
        raw = pyz.extract('frame_corners', raw=True)
        require(raw, 'AFrame: empty frame_corners payload')
        source = base.source_bytes(source_sha, 'AFrame/engine/frame_corners.py')
        return {'module': 'frame_corners', 'bytecode_sha256': sha(raw), 'bytes': len(raw),
                'source_sha256': sha(source), 'module_execution_performed': False,
                'source_bytecode_equivalence_claimed': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--downloads', type=Path, required=True)
    parser.add_argument('--build', type=int, required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--base-receipt', type=Path, required=True)
    parser.add_argument('--pilot-receipt', type=Path, required=True)
    parser.add_argument('--publication-evidence', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    result = {'schema': 'facade_release_1510_supplement/1', 'status': 'FAIL',
              'build': args.build, 'source_sha': args.source_sha,
              'observed_utc': datetime.now(timezone.utc).isoformat(),
              'verifier_sha256': sha(Path(__file__).read_bytes()),
              'live_autocad_checked': False, 'dll_loaded_in_autocad': False,
              'windows_exe_executed': False, 'engineering_approval': False}
    try:
        basic = json.loads(args.base_receipt.read_text())
        pilot = json.loads(args.pilot_receipt.read_text())
        require(basic['status'] == pilot['status'] == 'PASS', 'Prior receipts must be PASS')
        require(basic['build'] == pilot['build'] == args.build, 'Prior receipts have another build')
        require(basic['release_sha'] == pilot['source_sha'] == args.source_sha, 'Prior receipts have another SHA')
        args.publication = json.loads(args.publication_evidence.read_text())
        require(basic['publication'] == args.publication, 'Evidence differs from base receipt')
        result['input_receipt_sha256'] = {'base': sha(args.base_receipt.read_bytes()),
                                         'pilot': sha(args.pilot_receipt.read_bytes())}
        result['documents'] = {name: document(args, name) for name in (
            'Install_AutoCAD_2024.docx', 'Facades_User_Manual.docx', f'Fixes_2026-10-15_build{args.build}.txt')}
        result['bundles'] = {}
        for bundle in ('AFacades', 'AClad', 'AFrame'):
            data = latest_bytes(args, bundle + '.bundle.zip')
            require(sha(data) == basic['bundles'][bundle]['archive_sha256'], 'Archive differs from base receipt')
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                prefix = bundle + '.bundle/Contents/'
                item = compiled_metadata(archive.read(prefix + 'net48/' + base.BUNDLES[bundle][1]), bundle)
                if bundle == 'AFrame':
                    item['corners_module'] = frozen_corners(archive.read(prefix + 'engine/frame_engine.exe'), args.source_sha)
                result['bundles'][bundle] = item
        result['status'] = 'PASS'
    except Exception as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
    print(result['status'] + ': ' + result.get('error', 'release documents, current CLR metadata, frame_corners PYZ'))
    return result['status'] != 'PASS'


if __name__ == '__main__':
    raise SystemExit(main())
