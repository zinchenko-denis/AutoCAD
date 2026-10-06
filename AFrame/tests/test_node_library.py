"""Run actual library commands/adapter with CAD doubles; no AutoCAD host claim."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='node_library_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if not available():
        raise SystemExit('C# runtime/compiler missing; node library checks not run')
    executable = out / 'node_library.exe'
    sources = [ROOT / 'AFrame/tests/test_node_library.cs']
    sources += [ROOT / ('AFrame/src/AFramePlugin/FrameNodeLibrary' + suffix + '.cs')
                for suffix in ('Core', 'Cad', 'Command', 'HostCheck')]
    compiled = compile_probe(sources, ['System.Core'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return compiled.returncode
    result = subprocess.run(command(executable), capture_output=True, text=True)
    (out / 'result.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    print(result.stdout + result.stderr, end='')
    return result.returncode


if __name__ == '__main__':
    sys.exit(main())
