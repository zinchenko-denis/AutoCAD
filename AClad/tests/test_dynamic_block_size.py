"""Execute actual ATTILE MakeDynRef and size guards with CAD property doubles.

Checks the ordering of writes and final reads, not AutoCAD's native dynamic
block evaluator. The historical Cassette 2 DWG stays in the private repository.
"""
import argparse
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe


def method(source, name):
    match = re.search(r"(?m)^        (?:private|internal) static [^\n]+ " + name + r"\(", source)
    if not match:
        raise ValueError("Actual method missing: " + name)
    start = match.start()
    brace = source.index("{", start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: .NET compiler/runtime unavailable; no native AutoCAD execution claimed")
        return 2
    out = args.out or Path(tempfile.mkdtemp(prefix="dynamic_block_size_"))
    out.mkdir(parents=True, exist_ok=True)
    clad = (ROOT / "AClad/src/ACladPlugin/CladCommand.cs").read_text(encoding="utf-8")
    tile = (ROOT / "AClad/src/ACladPlugin/TilePatternCommand.cs").read_text(encoding="utf-8")
    names = ["TrySetNum", "DynSizeMatches", "RequirePlacement"]
    if "internal static string DynSizeDescription(" in clad:
        names.append("DynSizeDescription")
    source = Path(__file__).with_suffix(".cs").read_text(encoding="utf-8")
    source = source.replace("__CLAD_METHODS__", "\n".join(method(clad, name) for name in names))
    source = source.replace("__MAKE_DYN_REF__", method(tile, "MakeDynRef"))
    source = source.replace("__PROTO_SIZE_KEY__", method(tile, "PrototypeSizeKey"))
    cs, exe = out / "DynamicBlockSizeCheck.cs", out / "DynamicBlockSizeCheck.exe"
    cs.write_text(source, encoding="utf-8")
    compiled = compile_probe([cs, ROOT / "AClad/src/ACladPlugin/LayoutSafety.cs"], [], exe)
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    return subprocess.run(command(exe)).returncode


if __name__ == "__main__":
    raise SystemExit(main())
