"""Download public release assets and record HTTP/source/hash evidence."""
import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--build', type=int, required=True)
parser.add_argument('--out', type=Path, required=True)
args = parser.parse_args()
names = [name + '.bundle.zip' for name in ('ATableSpec', 'ABlockGen', 'AFacades', 'AClad', 'AFrame')]
names += ['Install_AutoCAD_2024.docx', 'Facades_User_Manual.docx', f'Fixes_2026-10-15_build{args.build}.txt']


def download(tag, name):
    url = f'https://github.com/zinchenko-denis/AutoCAD/releases/download/{tag}/{name}'
    path = args.out / tag / name
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.part')
    start = datetime.now(timezone.utc).isoformat()
    result = subprocess.run(['curl', '--fail', '--location', '--max-time', '180',
                             '--retry', '2', '--silent', '--show-error', '--output', str(temporary),
                             '--write-out', '%{http_code} %{size_download} %{content_type}', url],
                            text=True, capture_output=True)
    info = {'source_url': url, 'tag': tag, 'name': name, 'started_utc': start,
            'completed_utc': datetime.now(timezone.utc).isoformat(), 'curl_returncode': result.returncode}
    parts = result.stdout.strip().split(' ', 2)
    if len(parts) == 3:
        info.update({'http_status': int(parts[0]), 'http_bytes': int(float(parts[1])), 'content_type': parts[2]})
    if result.returncode == 0 and info.get('http_status') == 200:
        data = temporary.read_bytes()
        if len(data) != info['http_bytes']:
            info['error'] = 'Downloaded file length differs from HTTP report'
        else:
            temporary.replace(path)
            info.update({'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
                         'path': str(path), 'status': 'DOWNLOADED'})
    else:
        info['error'] = result.stderr.strip()
    if 'error' in info:
        info['status'] = 'FAIL'
    return info


result = {'build': args.build, 'status': 'FAIL', 'assets': []}
with ThreadPoolExecutor(max_workers=4) as pool:
    futures = [pool.submit(download, tag, name) for tag in (f'build-{args.build}', 'latest') for name in names]
    for future in as_completed(futures):
        item = future.result()
        result['assets'].append(item)
        print(item['tag'], item['name'], item['status'], item.get('http_status'), item.get('bytes'), flush=True)
result['assets'].sort(key=lambda item: (item['tag'], item['name']))
if all(item['status'] == 'DOWNLOADED' for item in result['assets']):
    result['status'] = 'DOWNLOADED'
args.out.mkdir(parents=True, exist_ok=True)
(args.out / 'download_evidence.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
raise SystemExit(result['status'] != 'DOWNLOADED')
