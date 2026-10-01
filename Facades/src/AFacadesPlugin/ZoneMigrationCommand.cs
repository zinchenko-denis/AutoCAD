using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using FacadeSafety;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AFacadesPlugin.ZoneMigrationCommand))]

namespace AFacadesPlugin
{
    public sealed class ZoneMigrationCommand
    {
        private static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; } }

        [CommandMethod("ATFZONEACCEPT", CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor; var db = doc.Database;
            try
            {
                var choice = ed.GetEntity("\nВыберите штриховку или марку старой зоны для принятия текущей геометрии: ");
                if (choice.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено без изменений."); return; }
                if (string.IsNullOrEmpty(db.Filename)) throw new InvalidOperationException("Сначала сохраните DWG и его исходный файл _fzones.json.");
                string path = Path.Combine(Path.GetDirectoryName(db.Filename) ?? ".", Path.GetFileNameWithoutExtension(db.Filename) + "_fzones.json");
                if (!File.Exists(path)) throw new InvalidOperationException("Не найден " + path +
                    ". Восстановите исходный _fzones.json рядом с DWG; без него нельзя отличить старую зону от копии.");
                string oldFile = File.ReadAllText(path, Encoding.UTF8);
                var sidecar = Json.DeserializeObject(oldFile) as object[];
                if (sidecar == null) throw new InvalidOperationException("Неверный формат _fzones.json.");
                Source source;
                using (var tr = db.TransactionManager.StartTransaction())
                    source = ReadSource(tr, db, choice.ObjectId, sidecar);
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                var calculated = Json.DeserializeObject(ZoneCommand.CallEngine(Path.GetFullPath(Path.Combine(
                    baseDir, "..", "engine", "facades_engine.exe")), source.Request)) as Dictionary<string, object>;
                List<Dictionary<string, object>> parts;
                var data = ZoneMigration.Result(source.Input, calculated, out parts);
                string area = Convert.ToDouble(ZoneMigration.Get((Dictionary<string, object>)data["report"], "area_net_m2"),
                    CultureInfo.InvariantCulture).ToString("0.000", CultureInfo.GetCultureInfo("ru-RU"));
                ed.WriteMessage("\nЗона «" + source.Input.ZoneId + "»: текущая площадь " + area + " м². " +
                    "Штриховка, марка и контуры сохранятся. Обновятся данные зоны и _fzones.json." +
                    "\nСтарые раскладка, каркас, линии откосов/отливов и ведомости НЕ подтверждаются и НЕ перестраиваются. После принятия пересчитайте нужные результаты.");
                var confirm = ed.GetKeywords(new PromptKeywordOptions(
                    "\nПринять текущую геометрию как исходную [Принять/Отмена] <Отмена>: ", "Принять Отмена"));
                if (confirm.Status != PromptStatus.OK || confirm.StringResult != "Принять")
                { ed.WriteMessage("\nОтменено без изменений."); return; }

                var updated = new List<object>();
                var replaced = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in source.Input.PreviousParts) replaced.Add(ZoneMigration.Text(part, "id"));
                foreach (var value in sidecar)
                    if (!replaced.Contains(ZoneMigration.Text(value as Dictionary<string, object>, "id"))) updated.Add(value);
                updated.AddRange(parts);
                string pending = path + ".accept-" + Guid.NewGuid().ToString("N") + ".tmp";
                string backup = pending + ".bak";
                bool fileReplaced = false, committed = false;
                try
                {
                    File.WriteAllText(pending, Json.Serialize(updated), new UTF8Encoding(false));
                    using (doc.LockDocument())
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var current = ReadSource(tr, db, choice.ObjectId, sidecar);
                        if (current.Hatch != source.Hatch || current.Mark != source.Mark || current.Request != source.Request ||
                            current.LegacyData != source.LegacyData || File.ReadAllText(path, Encoding.UTF8) != oldFile)
                            throw new InvalidOperationException("Исходники изменились во время подтверждения. Повторите ATFZONEACCEPT.");
                        var hatch = (Hatch)tr.GetObject(current.Hatch, OpenMode.ForWrite);
                        var mark = (MText)tr.GetObject(current.Mark, OpenMode.ForWrite);
                        string raw = Json.Serialize(data);
                        ZoneCommand.StoreZoneData(tr, hatch, raw);
                        ZoneCommand.StoreZoneData(tr, mark, raw);
                        var captured = ZoneGeometryGuard.Capture(tr, db, hatch, mark, data, parts, current.Outers, current.Holes);
                        if (!captured.Ok) throw new InvalidOperationException(captured.Reason);
                        mark.Contents = current.Input.ZoneId + @"\PS = " + area + " м²";
                        // No CAD changes commit unless the new sidecar can be installed.
                        // A failed commit restores that file as well. AutoCAD Undo covers
                        // this command's CAD transaction; the exported JSON is external.
                        File.Replace(pending, path, backup);
                        fileReplaced = true;
                        tr.Commit(); committed = true;
                    }
                }
                finally
                {
                    if (fileReplaced && !committed)
                    {
                        try
                        {
                            if (Directory.Exists(path)) throw new IOException("вместо файла появилась папка");
                            File.Copy(backup, path, true);
                            if (File.ReadAllText(path, Encoding.UTF8) != oldFile) throw new IOException("содержимое восстановленного файла не совпало");
                            Delete(backup);
                        }
                        catch (System.Exception restoreError)
                        {
                            ed.WriteMessage("\nНе удалось вернуть прежний _fzones.json: " + restoreError.Message +
                                ". Резервный файл сохранён: " + backup + ". Восстановите его перед продолжением работы.");
                        }
                    }
                    else if (committed) Delete(backup);
                    Delete(pending);
                }
                ed.WriteMessage("\nЗона «" + source.Input.ZoneId + "» принята без пересоздания. " +
                    "Теперь повторите ATTILE/ATFRAME и сформируйте ведомости заново. " +
                    "Отмена U возвращает изменения в DWG; внешний _fzones.json не входит в Undo.");
            }
            catch (System.Exception ex)
            { ed.WriteMessage("\nATFZONEACCEPT: принятие не выполнено — " + ex.Message); }
        }

        private sealed class Source
        {
            internal ZoneMigration.Input Input;
            internal string Request, LegacyData;
            internal ObjectId Hatch, Mark;
            internal readonly List<ObjectId> Outers = new List<ObjectId>(), Holes = new List<ObjectId>();
        }

        private static Source ReadSource(Transaction tr, Database db, ObjectId selected, object[] sidecar)
        {
            var carrier = tr.GetObject(selected, OpenMode.ForRead) as Entity;
            if (!(carrier is Hatch) && !(carrier is MText)) throw new InvalidOperationException("Выберите штриховку или марку ATFZONE.");
            string raw = ZoneCommand.ReadZoneData(tr, carrier);
            if (raw == null) throw new InvalidOperationException("На объекте нет данных ATFZONE.");
            var data = Json.DeserializeObject(raw) as Dictionary<string, object>;
            string zoneId = ZoneMigration.Text(data, "zone_id");
            var result = new Source { LegacyData = raw };
            int hatches = 0, marks = 0;
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            bool selectedInModel = false;
            foreach (ObjectId id in model)
            {
                if (id == selected) selectedInModel = true;
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (!(entity is Hatch) && !(entity is MText)) continue;
                string candidate = ZoneCommand.ReadZoneData(tr, entity);
                if (candidate == null) continue;
                Dictionary<string, object> candidateData;
                try { candidateData = Json.DeserializeObject(candidate) as Dictionary<string, object>; }
                catch { continue; }
                if (ZoneMigration.Text(candidateData, "zone_id") != zoneId) continue;
                if (ZoneGeometryGuard.HasSnapshot(tr, entity))
                    throw new InvalidOperationException("У зоны уже есть снимок геометрии. Эта команда предназначена только для старых зон; изменённую новую зону пересоздайте через ATFZONE.");
                if (candidate != raw) throw new InvalidOperationException("Штриховка и марка содержат разные старые данные. Проверьте дубли зоны.");
                if (entity is Hatch) { result.Hatch = id; hatches++; }
                else { result.Mark = id; marks++; }
            }
            if (!selectedInModel) throw new InvalidOperationException("Поддерживается зона в пространстве модели.");
            result.Input = ZoneMigration.ReadIdentity(data, sidecar, hatches, marks);
            var hatch = (Hatch)tr.GetObject(result.Hatch, OpenMode.ForRead);
            if (!hatch.Associative || hatch.HatchStyle != HatchStyle.Normal)
                throw new InvalidOperationException("У штриховки нет исходных ассоциативных контуров или изменён учёт островков.");
            var contours = new List<Dictionary<string, object>>();
            var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < hatch.NumberOfLoops; i++)
            {
                var ids = hatch.GetAssociatedObjectIdsAt(i);
                if (ids == null || ids.Count != 1 || ids[0].IsNull || ids[0].IsErased)
                    throw new InvalidOperationException("Граница штриховки потеряла связь с исходной полилинией.");
                var poly = tr.GetObject(ids[0], OpenMode.ForRead) as Polyline;
                string role = (hatch.GetLoopAt(i).LoopType & (HatchLoopTypes.External | HatchLoopTypes.Outermost)) != 0 ? "outer" : "hole";
                GeometryLoop loop; string reason;
                if (!ZoneGeometryGuard.TryReadPolyline(poly, role, out loop, out reason)) throw new InvalidOperationException(reason);
                if (!poly.Closed && poly.GetPoint2dAt(0).GetDistanceTo(poly.GetPoint2dAt(poly.NumberOfVertices - 1)) > 0.5)
                    throw new InvalidOperationException("Контур " + poly.Handle + " не замкнут.");
                string handle = poly.Handle.ToString();
                if (roles.ContainsKey(handle)) throw new InvalidOperationException("Контур повторяется в границах штриховки.");
                roles.Add(handle, role);
                (role == "outer" ? result.Outers : result.Holes).Add(poly.ObjectId);
                contours.Add(new Dictionary<string, object> { { "id", handle }, { "pts", loop.Points }, { "bulges", loop.Bulges } });
            }
            result.Request = Json.Serialize(ZoneMigration.Request(result.Input, contours, roles));
            return result;
        }

        private static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
