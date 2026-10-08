"""Exercise the aggregate gate's source-integrity result in a temporary Git repo.

The individual test jobs are deliberately stubbed here. These tests verify the
runner's exit status and manifest when inputs change, not facade behaviour or
AutoCAD. The full facade jobs remain a separate mandatory execution.
"""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    'facade_check_runner', Path(__file__).with_name('run_checks.py'))
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class RunnerIntegrityTests(unittest.TestCase):
    def run_fixture(self, changed_path=None, delete=False):
        with tempfile.TemporaryDirectory(prefix='facade-source-integrity-') as directory:
            base = Path(directory)
            repo, out = base / 'repo', base / 'output'
            repo.mkdir()
            for name in ('product.cs', 'test_product.py', 'host.ps1', 'host.scr'):
                (repo / name).write_text('original', encoding='utf-8')
            subprocess.run(['git', 'init', '-q'], cwd=repo, check=True)
            # The PowerShell file intentionally remains untracked.
            subprocess.run(['git', 'add', 'product.cs', 'test_product.py', 'host.scr'],
                           cwd=repo, check=True)
            subprocess.run(['git', '-c', 'user.name=Runner test',
                            '-c', 'user.email=runner@test.invalid',
                            '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture'],
                           cwd=repo, check=True)
            real_run = subprocess.run
            calls = []

            def run_stub(arguments, **kwargs):
                if arguments[0] == 'git':
                    return real_run(arguments, **kwargs)
                calls.append(arguments)
                if len(calls) == 1 and changed_path:
                    if delete:
                        (repo / changed_path).unlink()
                    else:
                        (repo / changed_path).write_text('mutated', encoding='utf-8')
                return subprocess.CompletedProcess(
                    arguments, 0, stdout='Runner integrity stub; no product execution.\n',
                    stderr='')

            with patch.object(RUNNER, 'ROOT', repo), \
                    patch.object(subprocess, 'run', run_stub), \
                    patch.object(sys, 'argv', ['run_checks', '--out', str(out)]), \
                    contextlib.redirect_stdout(io.StringIO()):
                exit_code = RUNNER.main()
            manifest = json.loads((out / 'manifest.json').read_text(encoding='utf-8'))
            names = [row['name'] for row in manifest['results']]
            self.assertEqual(len(names), len(set(names)), 'Duplicate mandatory group')
            self.assertTrue({'bundle_versions', 'zone_migration',
                             'frame_typical_facades', 'frame_local_issues'} <= set(names))
            self.assertEqual(exit_code, 1 if changed_path else 0)
            self.assertEqual(manifest['source_stable'], changed_path is None)
            self.assertEqual(set(manifest['source_changes']),
                             {changed_path} if changed_path else set())
            self.assertIn('host.ps1', manifest['source_sha256_before'])
            self.assertIn('host.scr', manifest['source_sha256_before'])
            self.assertFalse(manifest['live_autocad_checked'])

    def test_stable_sources_can_pass(self):
        self.run_fixture()

    def test_product_edit_invalidates_the_run(self):
        self.run_fixture('product.cs')

    def test_test_edit_invalidates_the_run(self):
        self.run_fixture('test_product.py')

    def test_untracked_host_edit_invalidates_the_run(self):
        self.run_fixture('host.ps1')

    def test_autocad_script_edit_invalidates_the_run(self):
        self.run_fixture('host.scr')

    def test_deleted_source_invalidates_the_run(self):
        self.run_fixture('product.cs', delete=True)


if __name__ == '__main__':
    unittest.main(verbosity=2)
