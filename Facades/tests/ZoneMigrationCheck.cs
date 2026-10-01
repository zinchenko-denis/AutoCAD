using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFacadesPlugin;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

internal static class ZoneMigrationCheck
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static Dictionary<string, object> Fixture;
    private static int assertions;
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static object[] Items(object value) { return (object[])value; }
    private static object Clone(object value) { return Json.DeserializeObject(Json.Serialize(value)); }
    private static void Check(bool ok, string name) { assertions++; if (!ok) throw new InvalidOperationException(name); }
    private static void Refused(Action action, string name)
    {
        bool refused = false;
        try { action(); } catch (InvalidOperationException) { refused = true; }
        Check(refused, name);
    }
    private static Dictionary<string, object> Data(Dictionary<string, object> result)
    {
        var zone = Dict(Items(result["zones"])[0]);
        return new Dictionary<string, object> { { "zone_id", zone["zone_id"] }, { "cladding", zone["cladding"] }, { "report", zone["report"] } };
    }
    private static void Store(Transaction tr, Entity entity, string key, object data)
    {
        if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
        var ext = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite);
        var buffer = new ResultBuffer(new TypedValue((int)DxfCode.Text, Json.Serialize(data)));
        if (ext.Contains(key)) ((Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForWrite)).Data = buffer;
        else ext.SetAt(key, new Xrecord { Data = buffer });
    }
    public static int Main(string[] args)
    {
        try
        {
            Fixture = Dict(Json.DeserializeObject(File.ReadAllText(args[0])));
            var old = Dict(Fixture["old"]); var fresh = Dict(Fixture["fresh"]);
            var oldData = Data(old); var oldParts = Items(old["zones_full"]);
            var identity = ZoneMigration.ReadIdentity(oldData, oldParts, 1, 1);
            var db = new Database(); var tr = new Transaction(db);
            var contours = Items(Dict(Fixture["request"])["contours"]).Select(Dict).ToList();
            var sources = new List<Polyline>();
            foreach (var contour in contours)
            {
                var poly = db.Add(new Polyline());
                foreach (var value in Items(contour["pts"]))
                { var xy = Items(value); poly.AddVertexAt(poly.NumberOfVertices, new Point2d(Convert.ToDouble(xy[0]), Convert.ToDouble(xy[1])), 0, 0, 0); }
                Check(poly.Handle.ToString() == (string)contour["id"], "fixture handles"); sources.Add(poly);
            }
            var hatch = db.Add(new Hatch()); var mark = db.Add(new MText());
            for (int i = 0; i < sources.Count; i++)
            {
                var poly = sources[i]; var boundary = new HatchLoop { LoopType = i == 0 ? HatchLoopTypes.External : HatchLoopTypes.Default };
                for (int j = 0; j < poly.NumberOfVertices; j++) boundary.Polyline.Add(new BulgeVertex(poly.GetPoint2dAt(j), 0));
                hatch.Loops.Add(boundary); hatch.AssociatedIds.Add(new ObjectIdCollection(new[] { poly.ObjectId }));
            }
            Store(tr, hatch, "ATFZONE", oldData); Store(tr, mark, "ATFZONE", oldData);
            Store(tr, hatch, "ATTILE", new Dictionary<string, object> { { "legacy", true } });
            var before = ZoneGeometryGuard.Verify(tr, db, hatch, null);
            Check(!before.Ok && before.Reason.Contains("старая зона"), "reproduce legacy refusal before adoption");
            Check(before.Reason.Contains("ATFZONEACCEPT"), "actionable legacy message");
            var roles = new Dictionary<string, string> { { "1", "outer" }, { "2", "hole" } };
            var request = ZoneMigration.Request(identity, contours, roles);
            Check(Json.Serialize(request["contours"]) == Json.Serialize(contours), "current geometry sent to engine");
            Check((string)Dict(request["opening_kinds"])["2"] == "door", "opening kind preserved");
            List<Dictionary<string, object>> parts;
            var data = ZoneMigration.Result(identity, Dict(Clone(fresh)), out parts);
            Check((string)data["zone_id"] == (string)oldData["zone_id"], "zone identity preserved");
            Check((string)parts[0]["id"] == (string)Dict(oldParts[0])["id"], "part identity preserved");
            Check(Convert.ToDouble(Dict(data["report"])["area_net_m2"]) != Convert.ToDouble(Dict(oldData["report"])["area_net_m2"]), "old area is recalculated");
            var expectedReport = Dict(Clone(Data(fresh)["report"])); expectedReport["zone_id"] = identity.ZoneId;
            Check(Json.Serialize(data["report"]) == Json.Serialize(expectedReport), "fresh engine report and original zone id used");
            Store(tr, hatch, "ATFZONE", data); Store(tr, mark, "ATFZONE", data);
            var captured = ZoneGeometryGuard.Capture(tr, db, hatch, mark, data, parts,
                new List<ObjectId> { sources[0].ObjectId }, new List<ObjectId> { sources[1].ObjectId });
            Check(captured.Ok, "adoption captures current geometry: " + captured.Reason);
            var verified = ZoneGeometryGuard.Verify(tr, db, mark, parts.ToDictionary(p => (string)p["id"]));
            Check(verified.Ok && verified.HatchId == hatch.ObjectId, "mark retains hatch ownership");
            Check(!LayoutGeometryGuard.Verify(tr, db, hatch, "ATTILE").Ok, "old layout remains unverified");
            sources[1].Points[0] = new Point2d(1200, 1500);
            Check(!ZoneGeometryGuard.Verify(tr, db, hatch, null).Ok, "later source edit remains guarded");

            Refused(() => ZoneMigration.ReadIdentity(oldData, oldParts, 2, 1), "copied hatch rejected");
            Refused(() => ZoneMigration.ReadIdentity(oldData, oldParts, 1, 2), "copied mark rejected");
            Refused(() => ZoneMigration.ReadIdentity(oldData, new object[0], 1, 1), "missing sidecar zone rejected");
            Refused(() => ZoneMigration.Request(identity, contours, new Dictionary<string, string> { { "9", "outer" }, { "A", "hole" } }), "cloned source handles rejected");
            Refused(() => ZoneMigration.Request(identity, contours, new Dictionary<string, string> { { "1", "hole" }, { "2", "outer" } }), "source roles cannot be exchanged");
            var duplicateParts = new object[] { oldParts[0], oldParts[0] };
            Refused(() => ZoneMigration.ReadIdentity(oldData, duplicateParts, 1, 1), "duplicate parts rejected");
            var failed = Dict(Clone(fresh)); failed["failed"] = new object[] { new Dictionary<string, object>() };
            Refused(() => ZoneMigration.Result(identity, failed, out parts), "partial engine result rejected");

            var mergedOld = Dict(Fixture["merged_old"]); var mergedFresh = Dict(Clone(Fixture["merged_fresh"]));
            var mergedIdentity = ZoneMigration.ReadIdentity(Data(mergedOld), Items(mergedOld["zones_full"]), 1, 1);
            var oldBySource = Items(mergedOld["zones_full"]).Select(Dict).ToDictionary(p => (string)Dict(p["meta"])["outer_contour_id"], p => (string)p["id"]);
            Array.Reverse(Items(mergedFresh["zones_full"]));
            ZoneMigration.Result(mergedIdentity, mergedFresh, out parts);
            Check(parts.All(p => (string)p["id"] == oldBySource[(string)Dict(p["meta"])["outer_contour_id"]]), "merged identities survive engine order change");
            Check(parts.All(p => (string)Dict(p["meta"])["group"] == mergedIdentity.ZoneId), "merged owner group preserved");
            CommandCases(Path.GetDirectoryName(args[0]));
            Console.WriteLine("Legacy zone migration: PASS " + assertions + " checks; CAD doubles, live AutoCAD NOT_RUN.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    private sealed class CommandFixture
    {
        internal readonly Database Db = new Database();
        internal readonly Hatch Hatch;
        internal readonly MText Mark;
        internal readonly Autodesk.AutoCAD.ApplicationServices.Document Doc;
        internal readonly string Path, Before;
        internal int EngineCalls;
        internal CommandFixture(string directory, string name)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(directory, name));
            Db.Filename = System.IO.Path.Combine(directory, name, "pilot.dwg");
            Path = System.IO.Path.Combine(directory, name, "pilot_fzones.json");
            Before = Json.Serialize(Dict(Fixture["old"])["zones_full"]);
            File.WriteAllText(Path, Before);
            var sources = new List<Polyline>();
            foreach (var contour in Items(Dict(Fixture["request"])["contours"]).Select(Dict))
            {
                var poly = Db.Add(new Polyline());
                foreach (var value in Items(contour["pts"]))
                { var xy = Items(value); poly.AddVertexAt(poly.NumberOfVertices, new Point2d(Convert.ToDouble(xy[0]), Convert.ToDouble(xy[1])), 0, 0, 0); }
                sources.Add(poly);
            }
            Hatch = Db.Add(new Hatch()); Mark = Db.Add(new MText { Contents = "old label" });
            for (int i = 0; i < sources.Count; i++)
            {
                var loop = new HatchLoop { LoopType = i == 0 ? HatchLoopTypes.External : HatchLoopTypes.Default };
                foreach (var p in sources[i].Points) loop.Polyline.Add(new BulgeVertex(p, 0));
                Hatch.Loops.Add(loop); Hatch.AssociatedIds.Add(new ObjectIdCollection(new[] { sources[i].ObjectId }));
            }
            Db.Model = Db.Add(new BlockTableRecord());
            Db.Model.Ids.AddRange(new[] { sources[0].ObjectId, sources[1].ObjectId, Hatch.ObjectId, Mark.ObjectId });
            using (var tr = new Transaction(Db))
            {
                Store(tr, Hatch, "ATFZONE", Data(Dict(Fixture["old"])));
                Store(tr, Mark, "ATFZONE", Data(Dict(Fixture["old"])));
                Store(tr, Hatch, "ATTILE", new Dictionary<string, object> { { "old_output", true } });
                Store(tr, Hatch, "ATFRAME", new Dictionary<string, object> { { "old_output", true } });
                tr.Commit();
            }
            Doc = new Autodesk.AutoCAD.ApplicationServices.Document { Database = Db };
            Doc.Editor.Entity = new Autodesk.AutoCAD.EditorInput.PromptEntityResult { Status = Autodesk.AutoCAD.EditorInput.PromptStatus.OK, ObjectId = Hatch.ObjectId };
            Doc.Editor.Keywords = new Autodesk.AutoCAD.EditorInput.PromptResult { Status = Autodesk.AutoCAD.EditorInput.PromptStatus.OK, StringResult = "Принять" };
            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument = Doc;
            ZoneCommand.Engine = request =>
            {
                EngineCalls++;
                Check(Dict(Json.DeserializeObject(request)).ContainsKey("contours"), "actual command sends contours");
                return Json.Serialize(Fixture["fresh"]);
            };
        }
        internal void Run() { new ZoneMigrationCommand().Run(); }
        internal void Unchanged(string name)
        {
            using (var tr = new Transaction(Db))
                Check(!ZoneGeometryGuard.HasSnapshot(tr, Hatch) && Mark.Contents == "old label", name + " CAD unchanged");
            Check(File.ReadAllText(Path) == Before, name + " sidecar unchanged");
            Check(Db.Commits == 1, name + " no command commit");
        }
    }

    private static void CommandCases(string directory)
    {
        var canceled = new CommandFixture(directory, "cancel");
        canceled.Doc.Editor.Keywords.Status = Autodesk.AutoCAD.EditorInput.PromptStatus.Cancel;
        canceled.Run(); canceled.Unchanged("cancel");

        var accepted = new CommandFixture(directory, "accept"); accepted.Run();
        Check(accepted.Db.Commits == 2 && accepted.EngineCalls == 1, "one command commit after consent");
        Check(accepted.Doc.Editor.Messages.Contains("НЕ подтверждаются"), "legacy output warning shown");
        using (var tr = new Transaction(accepted.Db))
        {
            var sidecar = Items(Json.DeserializeObject(File.ReadAllText(accepted.Path))).Select(Dict).ToDictionary(p => (string)p["id"]);
            Check(ZoneGeometryGuard.Verify(tr, accepted.Db, accepted.Hatch, sidecar).Ok, "actual command produces usable zone and sidecar");
            Check(ZoneGeometryGuard.Verify(tr, accepted.Db, accepted.Mark, sidecar).HatchId == accepted.Hatch.ObjectId, "actual command keeps carrier owner");
            Check(!LayoutGeometryGuard.Verify(tr, accepted.Db, accepted.Hatch, "ATTILE").Ok, "command does not refresh old cladding");
            Check(!LayoutGeometryGuard.Verify(tr, accepted.Db, accepted.Hatch, "ATFRAME").Ok, "command does not refresh old frame");
            Check(ZoneCommand.ReadZoneData(tr, accepted.Hatch, "ATFRAME").Contains("old_output"), "old output metadata preserved");
        }

        var duplicate = new CommandFixture(directory, "duplicate");
        var copy = duplicate.Db.Add(new Hatch()); duplicate.Db.Model.Ids.Add(copy.ObjectId);
        using (var tr = new Transaction(duplicate.Db)) { Store(tr, copy, "ATFZONE", Data(Dict(Fixture["old"]))); tr.Commit(); }
        int commits = duplicate.Db.Commits;
        duplicate.Run();
        Check(duplicate.EngineCalls == 0 && duplicate.Db.Commits == commits && duplicate.Doc.Editor.Messages.Contains("неоднозначна"), "actual command refuses COPY ambiguity before engine/write");

        var corrupt = new CommandFixture(directory, "corrupt");
        using (var tr = new Transaction(corrupt.Db))
        {
            var ext = (DBDictionary)tr.GetObject(corrupt.Hatch.ExtensionDictionary, OpenMode.ForWrite);
            ext.SetAt(ZoneGeometryGuard.Key, new Xrecord { Data = null }); tr.Commit();
        }
        corrupt.Run();
        Check(corrupt.EngineCalls == 0 && corrupt.Doc.Editor.Messages.Contains("уже есть снимок"), "empty snapshot cannot masquerade as legacy");

        var fileFailure = new CommandFixture(directory, "file-failure");
        fileFailure.Doc.BeforeLock = () =>
        { foreach (var path in Directory.GetFiles(System.IO.Path.GetDirectoryName(fileFailure.Path), "*.accept-*.tmp")) File.Delete(path); };
        fileFailure.Run(); fileFailure.Unchanged("file install failure rollback");

        var commitFailure = new CommandFixture(directory, "commit-failure");
        commitFailure.Db.FailCommit = true;
        commitFailure.Run(); commitFailure.Unchanged("commit failure restores file and CAD");

        var restoreFailure = new CommandFixture(directory, "restore-failure");
        restoreFailure.Db.FailCommit = true;
        restoreFailure.Db.OnFailedCommit = () => { File.Delete(restoreFailure.Path); Directory.CreateDirectory(restoreFailure.Path); };
        restoreFailure.Run();
        var backups = Directory.GetFiles(System.IO.Path.GetDirectoryName(restoreFailure.Path), "*.bak");
        Check(backups.Length == 1 && File.ReadAllText(backups[0]) == restoreFailure.Before, "backup kept when restore fails: " + restoreFailure.Doc.Editor.Messages + "; backup count=" + backups.Length);
        Check(restoreFailure.Doc.Editor.Messages.Contains(backups[0]) && restoreFailure.Doc.Editor.Messages.Contains("simulated commit failure"), "restore warning gives recovery file and original failure");
        using (var tr = new Transaction(restoreFailure.Db))
            Check(!ZoneGeometryGuard.HasSnapshot(tr, restoreFailure.Hatch) && restoreFailure.Mark.Contents == "old label", "failed commit still rolls back CAD when file restore fails");

        var raced = new CommandFixture(directory, "source-change");
        raced.Doc.Editor.OnKeywords = () => raced.Mark.Contents = "user edited label";
        // A geometry edit during confirmation changes the request; no cached adoption.
        raced.Doc.BeforeLock = () => ((Polyline)raced.Hatch.AssociatedIds[0][0].Item).Points[0] = new Point2d(-100, 0);
        raced.Run();
        using (var tr = new Transaction(raced.Db)) Check(!ZoneGeometryGuard.HasSnapshot(tr, raced.Hatch), "changed source not accepted");
        Check(raced.Db.Commits == 1 && File.ReadAllText(raced.Path) == raced.Before, "changed source abort keeps file and no commit");
    }
}
