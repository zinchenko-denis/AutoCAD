"""Compile and package the narrow AutoCAD Core probe; never claims a native run.

Use dotnet on Windows CI, or --references with official extracted NuGet
references and Roslyn/Mono on Linux. Autodesk runtime DLLs are never bundled.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
PROJECT = HERE / "AutoCAD.CoreProbe.csproj"
ASSEMBLY = "AutoCAD.CoreProbe.dll"
BUNDLE = "FacadeCoreProbe.bundle"


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def source_files():
    sources = set(HERE.glob("*.cs"))
    for entry in ET.parse(PROJECT).getroot().iter("Compile"):
        include = entry.get("Include")
        if include:
            if any(c in include for c in "*$;"):
                raise ValueError("Computed/wildcard Compile Include needs explicit support")
            sources.add((HERE / include.replace("\\", "/")).resolve())
    if not sources or not all(p.is_file() for p in sources):
        raise ValueError("Missing C# sources")
    return sorted(sources)


def package_xml():
    root = ET.Element("ApplicationPackage", {
        "SchemaVersion": "1.0", "AppVersion": "0.1.0",
        "ProductCode": "{F8174A1C-669F-473B-A944-54A2B336EB72}",
        "Name": "FacadeCoreProbe", "Description": "AutoCAD Core diagnostic probe",
        "Author": "AutoCAD facade project"})
    components = ET.SubElement(root, "Components")
    ET.SubElement(components, "RuntimeRequirements", {
        "OS": "Win64", "Platform": "AutoCAD", "SeriesMin": "R24.3", "SeriesMax": "R24.3"})
    component = ET.SubElement(components, "ComponentEntry", {
        "AppName": "FacadeCoreProbe", "ModuleName": "./Contents/" + ASSEMBLY,
        "AppDescription": "Narrow dynamic block probe, not ATTILE acceptance",
        "LoadOnCommandInvocation": "True", "LoadOnAutoCADStartup": "True"})
    commands = ET.SubElement(component, "Commands", {"GroupName": "FacadeCoreQA"})
    for name in ("ATPROBE", "ATREOPENVERIFY"):
        ET.SubElement(commands, "Command", {"Global": name, "Local": name})
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def make_bundle(dll, manifest, destination):
    entries = {
        BUNDLE + "/PackageContents.xml": package_xml(),
        BUNDLE + "/Contents/" + ASSEMBLY: Path(dll).read_bytes(),
        BUNDLE + "/Contents/build-manifest.json":
            (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
    }
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(entries.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    with zipfile.ZipFile(destination) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(entries):
            raise ValueError("Bundle verification failed")


def build(args):
    out = args.out.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Output directory must be new or empty; stale DLLs are not accepted")
    out.mkdir(parents=True, exist_ok=True)
    target = out / "compiled"
    target.mkdir()
    sources = source_files()
    if args.references:
        refs = args.references.resolve()
        compiler = refs / "microsoft.net.compilers.toolset/tasks/net472/csc.exe"
        framework = refs / "microsoft.netframework.referenceassemblies.net48/build/.NETFramework/v4.8"
        libraries = [framework / (s + ".dll") for s in (
            "mscorlib", "System", "System.Core", "System.Web.Extensions")]
        libraries += [refs / "autocad.net.core/lib/net47/AcCoreMgd.dll",
                      refs / "autocad.net.model/lib/net47/AcDbMgd.dll"]
        if not compiler.is_file() or not all(p.is_file() for p in libraries) or not shutil.which("mono"):
            raise ValueError("Missing Roslyn/Mono or official Core/net48 references")
        command = ["mono", str(compiler), "-nologo", "-noconfig", "-nostdlib+",
                   "-langversion:latest", "-target:library", "-platform:x64",
                   "-out:" + str(target / ASSEMBLY)]
        command += ["-r:" + str(p) for p in libraries] + [str(p) for p in sources]
        method = "Roslyn/Mono, official net48 and AutoCAD Core/Model references"
    else:
        if not shutil.which("dotnet"):
            raise ValueError("dotnet unavailable: use official --references with Roslyn/Mono")
        command = ["dotnet", "build", str(PROJECT), "-c", "Release", "-o", str(target)]
        method = "dotnet build AutoCAD.CoreProbe.csproj"
        libraries = []
    completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    (out / "compile.log").write_text(completed.stdout + completed.stderr, encoding="utf-8")
    if completed.returncode:
        print(completed.stdout + completed.stderr)
        raise ValueError("Core probe compilation failed; no AppBundle produced")
    dll = target / ASSEMBLY
    if not dll.is_file():
        raise ValueError("Compiler reported success but probe DLL is missing")
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    relevant_paths = [str(p.relative_to(ROOT)) for p in sources] + [str(PROJECT.relative_to(ROOT)), str(Path(__file__).resolve().relative_to(ROOT))]
    dirty = subprocess.run(["git", "status", "--porcelain", "--", *relevant_paths],
                           cwd=ROOT, capture_output=True, text=True, check=True).stdout
    manifest = {
        "schema_version": 1, "scope": "CORE_DYNAMIC_BLOCK_PROBE_ONLY",
        "native_execution": "NOT_RUN", "source_commit": head,
        "source_files_clean_at_build": not bool(dirty), "compile_method": method,
        "assembly_name": ASSEMBLY, "assembly_sha256": sha256(dll),
        "target_engine": "Autodesk.AutoCAD+24_3", "target_framework": "net48",
        "source_sha256": {str(p.relative_to(ROOT)): sha256(p) for p in sources},
        "project_sha256": sha256(PROJECT), "builder_sha256": sha256(__file__),
        "reference_sha256": {p.name: sha256(p) for p in libraries},
        "not_covered": ["ATTILE user command", "UI", "COM", "node author", "installation"],
    }
    (out / "build-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    bundle = out / "FacadeCoreProbe.zip"
    make_bundle(dll, manifest, bundle)
    summary = {"status": "BUILD_PASS", "native_execution": "NOT_RUN", "bundle": str(bundle),
               "bundle_sha256": sha256(bundle), "assembly_sha256": manifest["assembly_sha256"]}
    (out / "package-result.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--references", type=Path, help="Extracted official NuGet references for Roslyn/Mono")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(build(args), ensure_ascii=False))
        return 0
    except (ValueError, OSError, subprocess.SubprocessError, ET.ParseError) as error:
        print(json.dumps({"status": "BUILD_FAIL", "native_execution": "NOT_RUN", "reason": str(error)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    sys.exit(main())
