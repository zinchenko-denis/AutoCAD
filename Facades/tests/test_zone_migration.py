"""Reproduce legacy refusal; test explicit adoption with real engine and C# guards.

Small CAD doubles test decisions and ownership, not AutoCAD Undo/file runtime.
Run from repo: python3 Facades/tests/test_zone_migration.py
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command as run_command, compile_probe


def method(text, signature):
    """Use the existing metadata helpers unchanged; replace only external engine seam."""
    start = text.index(signature)
    brace = text.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def command_sources(temp):
    base = (ROOT / "tools/fixes_3009/test_zone_geometry_cad_doubles.cs").read_text(encoding="utf-8")
    transaction = method(base, "public class Transaction : IDisposable")
    base = base.replace(transaction, "").replace("public class Database\n", "public partial class Database\n")
    base_path = temp / "CadDoubles.cs"
    base_path.write_text(base, encoding="utf-8")
    command = (ROOT / "Facades/src/AFacadesPlugin/ZoneCommand.cs").read_text(encoding="utf-8")
    signatures = [
        "internal static void StoreZoneData(Transaction tr, Entity ent,\n                                           string json)",
        "internal static void StoreZoneData(Transaction tr, Entity ent,\n                                           string json, string key)",
        "internal static void StoreZoneData(Transaction tr, Entity ent,\n                                           string json, string key, IEnumerable<ObjectId> refs)",
        "internal static string ReadZoneData(Transaction tr, Entity ent)",
        "internal static string ReadZoneData(Transaction tr, Entity ent,\n                                            string key)",
        "internal static string SafeStr(object o)",
    ]
    helpers = temp / "ZoneMetadataHelpers.cs"
    helpers.write_text("""using System; using System.Collections.Generic; using System.Globalization;
using System.Text; using Autodesk.AutoCAD.DatabaseServices;
namespace AFacadesPlugin { internal static class ZoneCommand {
internal const string XKey = "ATFZONE";
internal static Func<string,string> Engine;
internal static string CallEngine(string executable, string request) { return Engine(request); }
""" + "\n".join(method(command, sig) for sig in signatures) + "\n} }", encoding="utf-8")
    return [str(base_path), str(helpers)]


def fixtures():
    sys.path.insert(0, str(ROOT / "Facades/engine"))
    from facades_engine import op_zones

    def rect(x, y, w, h):
        return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]

    old_request = {"op": "zones", "units": "mm", "cladding": "porcelain", "zone_prefix": "Ф-",
                   "opening_kinds": {"2": "door"}, "contours": [
                       {"id": "1", "pts": rect(0, 0, 6000, 6000)},
                       {"id": "2", "pts": rect(1000, 1500, 1200, 1500)}]}
    current = json.loads(json.dumps(old_request))
    current["contours"][1]["pts"] = rect(1000, 1500, 1800, 1500)
    current["zone_prefix"] = "migration-"
    merged_old = json.loads(json.dumps(old_request))
    merged_old["contours"].append({"id": "3", "pts": rect(8000, 0, 4000, 4000)})
    merged_old["merge"] = True
    merged_current = json.loads(json.dumps(merged_old))
    merged_current["zone_prefix"] = "migration-"
    return {"old": op_zones(old_request), "fresh": op_zones(current), "request": current,
            "merged_old": op_zones(merged_old), "merged_fresh": op_zones(merged_current)}


def main():
    if not available():
        raise SystemExit("mono/mcs or Windows dotnet is required; checks were not run")
    with tempfile.TemporaryDirectory(prefix="zone_migration_") as temp:
        temp = Path(temp)
        fixture = temp / "fixture.json"
        fixture.write_text(json.dumps(fixtures(), ensure_ascii=False), encoding="utf-8")
        executable = temp / "ZoneMigrationCheck.exe"
        sources = ["Facades/tests/ZoneMigrationCheck.cs", "Facades/src/AFacadesPlugin/ZoneMigration.cs",
                   "Facades/tests/ZoneMigrationCommandDoubles.cs", "Facades/src/AFacadesPlugin/ZoneMigrationCommand.cs",
                   "Common/GeometryFingerprint.cs", "Common/ZoneGeometryGuard.cs", "Common/LayoutGeometryGuard.cs"]
        built = compile_probe([ROOT / p for p in sources] + command_sources(temp), ["System.Web.Extensions"], executable)
        if built.returncode:
            raise SystemExit(built.stdout + built.stderr)
        subprocess.run(run_command(executable) + [str(fixture)], check=True)


if __name__ == "__main__":
    main()
