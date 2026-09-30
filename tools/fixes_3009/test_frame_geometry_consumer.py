"""Execute actual AFrame freshness/selection consumers with controlled guard outcomes.

The separate geometry-helper tests cover fingerprint correctness. This checks
whether FrameCommand refuses those outcomes, supplies only verified parts, and
requires all recorded sources before replacing an earlier combined frame.
"""
import argparse
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
METHODS = ("TryReadVerifiedLayout", "TryGetVerifiedZoneParts", "IsLayoutSelectionComplete", "IsSupportedRawContour")


def extract(source, name):
    match = re.search(r"(?m)^        internal static bool " + re.escape(name) + r"\(", source)
    if not match:
        raise RuntimeError("consumer method not found: " + name)
    start = match.start()
    brace = source.index("{", start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not shutil.which("mcs") or not shutil.which("mono"):
        print("BLOCKED: mono/mcs unavailable")
        return 2
    source = (ROOT / "AFrame/src/AFramePlugin/FrameCommand.cs").read_text(encoding="utf-8")
    # Small wiring gate complements actual execution of the consumers.
    assert source.index("TryReadVerifiedLayout(tr, db, ent") < source.index('var jx = Get(m, "joints_x")')
    assert source.index("IsLayoutSelectionComplete(layoutSources") < source.index("// ── 5. движок")
    assert source.index("TryGetVerifiedZoneParts(geometryTr") < source.index("zonesPayload.Add(zrec)")
    raw_gate = source.index("if (!IsSupportedRawContour(pts, bulges, out rawReason))")
    raw_write = source.index('polyData[h] = new Dictionary<string, object>')
    assert raw_gate < raw_write < source.index("// ── 5. движок")
    assert "return;" in source[raw_gate:raw_write]
    template = Path(__file__).with_suffix(".cs").read_text(encoding="utf-8")
    out = args.out or Path(tempfile.mkdtemp(prefix="frame_geometry_consumer_"))
    out.mkdir(parents=True, exist_ok=True)
    cs, exe = out / "FrameGeometryConsumer.cs", out / "FrameGeometryConsumer.exe"
    cs.write_text(template.replace("__ACTUAL_METHODS__", "\n\n".join(extract(source, name) for name in METHODS)), encoding="utf-8")
    subprocess.run(["mcs", "-out:" + str(exe), "-r:System.Web.Extensions.dll", str(cs)], check=True)
    return subprocess.run(["mono", str(exe)]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
