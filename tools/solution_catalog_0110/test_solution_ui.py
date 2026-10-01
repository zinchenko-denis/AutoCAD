"""Actual WinForms selection dialog: explicit choices, cancellation and rendered layout.

Windows uses its desktop. Linux needs Xvfb; unavailable native display is BLOCKED,
never a successful GUI test. Does not open or claim to test AutoCAD.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe


def main(probe=None, runner_path=None, source_names=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='solution_ui_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    probe = Path(probe) if probe is not None else Path(__file__).with_name('SolutionUiProbe.cs')
    files = [probe]
    names = source_names if source_names is not None else (
        'FrameForm.cs', 'FrameSettings.cs', 'FrameSolutionSelection.cs', 'FrameSolutionForm.cs',
        'FrameProjectParameters.cs', 'FrameProjectForms.cs', 'FrameBoundedForm.cs')
    files += [ROOT / 'AFrame/src/AFramePlugin' / name for name in names]
    tracked = files + [Path(__file__).resolve()] + ([Path(runner_path)] if runner_path is not None else [])
    manifest = {'status': 'BLOCKED', 'live_autocad_checked': False,
                'source_sha256': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked}}

    def finish(status, reason=None):
        manifest['status'] = status
        if reason:
            manifest['reason'] = reason
        (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(probe.stem + ':', status, reason or '')
        return 0 if status == 'PASS' else 2 if status == 'BLOCKED' else 1

    if not available():
        return finish('BLOCKED', 'Native .NET compiler/runtime unavailable')
    exe = out / (probe.stem + '.exe')
    compiled = compile_probe(files, ['System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions'], exe)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return finish('FAIL', 'Actual WinForms probe compilation failed')
    env = os.environ.copy()
    server = None
    server_log = None
    try:
        if os.name != 'nt':
            runtime = os.environ.get('FACADE_UI_RUNTIME')
            xvfb = shutil.which('Xvfb')
            if runtime:
                base = Path(runtime)
                xvfb = str(base / 'usr/bin/Xvfb')
                env['LD_LIBRARY_PATH'] = ':'.join([str(base / 'usr/lib/x86_64-linux-gnu'), str(base / 'usr/lib'), env.get('LD_LIBRARY_PATH', '')])
            if not xvfb or not Path(xvfb).is_file():
                return finish('BLOCKED', 'Xvfb unavailable; compilation succeeded, no GUI execution claimed')
            server_log = (out / 'xvfb.log').open('w', encoding='utf-8')
            server = subprocess.Popen([xvfb, '-displayfd', '1', '-screen', '0', '1600x1200x24', '-nolisten', 'tcp', '-ac'],
                                      env=env, stdout=subprocess.PIPE, stderr=server_log, text=True)
            readable, _, _ = select.select([server.stdout], [], [], 10)
            display = server.stdout.readline().strip() if readable else ''
            if not display.isdigit():
                return finish('BLOCKED', 'Xvfb could not create a native display; see xvfb.log. No GUI execution claimed')
            env['DISPLAY'] = ':' + display
        run = subprocess.run(command(exe) + [str(out)], env=env, capture_output=True, text=True, timeout=120)
        (out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
        print(run.stdout + run.stderr)
        if run.returncode:
            return finish('FAIL', 'Actual WinForms scenario failed; see run.log')
        details = json.loads((out / 'ui_checks.json').read_text(encoding='utf-8'))
        manifest['ui'] = details
        manifest['rendered_sha256'] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(out.glob('*.png'))}
        return finish(details['status'])
    except subprocess.TimeoutExpired:
        return finish('FAIL', 'Actual WinForms scenario timed out')
    finally:
        if server is not None:
            server.terminate()
            try:
                server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
        if server_log is not None:
            server_log.close()


if __name__ == '__main__':
    raise SystemExit(main())
