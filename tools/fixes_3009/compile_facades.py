"""Compile only the three facade plugins against real net48/AutoCAD references.

This produces validation DLLs in a temporary/output directory. It neither
packages nor installs a bundle and is not a substitute for an AutoCAD run.
With dotnet available the existing project files are authoritative. The
Roslyn/Mono route is useful in Linux environments without the dotnet SDK.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
PROJECTS = {"AFacadesPlugin": "Facades", "ACladPlugin": "AClad", "AFramePlugin": "AFrame"}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--source", type=Path, default=ROOT)
    ap.add_argument("--references", type=Path,
                    help="Extracted NuGet package directory for Roslyn/Mono compilation")
    ap.add_argument("--out", type=Path)
    args = ap.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="facades_compile_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    results = []
    for name, module in PROJECTS.items():
        source = args.source / module / "src" / name
        target = out / name
        target.mkdir(exist_ok=True)
        if args.references:
            refs = args.references.resolve()
            compiler = refs / "microsoft.net.compilers.toolset/tasks/net472/csc.exe"
            framework = refs / "microsoft.netframework.referenceassemblies.net48/build/.NETFramework/v4.8"
            libraries = [framework / (stem + ".dll") for stem in (
                "mscorlib", "System", "System.Core", "Microsoft.CSharp", "System.Xml",
                "System.Web.Extensions", "System.Windows.Forms", "System.Drawing",
                "System.IO.Compression", "System.IO.Compression.FileSystem",
                "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml")]
            for package in ("autocad.net", "autocad.net.core", "autocad.net.model"):
                libraries.extend(sorted((refs / package / "lib/net47").glob("*.dll")))
            if not compiler.exists() or not all(f.exists() for f in libraries) or not shutil.which("mono"):
                raise SystemExit("Missing Roslyn/Mono/reference assemblies; no compilation claimed")
            sources = sorted(p for p in source.rglob("*.cs") if not {"obj", "bin"}.intersection(p.parts))
            command = ["mono", str(compiler), "-nologo", "-noconfig", "-nostdlib+",
                       "-langversion:latest", "-target:library", "-platform:x64",
                       "-out:" + str(target / (name + ".dll"))]
            command += ["-r:" + str(p) for p in libraries] + [str(p) for p in sources]
            method = "Roslyn 4.8 with net48/AutoCAD 24.0.0 reference assemblies"
        else:
            if not shutil.which("dotnet"):
                raise SystemExit("dotnet is unavailable; use --references with Roslyn/Mono")
            command = ["dotnet", "build", str(source / (name + ".csproj")),
                       "-c", "Release", "-o", str(target)]
            sources = sorted(p for p in source.rglob("*.cs") if not {"obj", "bin"}.intersection(p.parts))
            method = "dotnet build of existing project"
        p = subprocess.run(command, capture_output=True, text=True, cwd=args.source)
        (out / (name + ".log")).write_text(p.stdout + p.stderr, encoding="utf-8")
        results.append({"plugin": name, "returncode": p.returncode, "method": method,
                        "source_sha256": {str(f.relative_to(args.source)): hashlib.sha256(f.read_bytes()).hexdigest()
                                          for f in sources}})
        print(name, "PASS" if p.returncode == 0 else "FAIL", flush=True)
        if p.returncode:
            print(p.stdout + p.stderr)
    (out / "manifest.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    return int(any(r["returncode"] for r in results))


if __name__ == "__main__":
    sys.exit(main())
