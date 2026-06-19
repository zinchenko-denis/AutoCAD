// Пересчёт "своих отчётов" (action=report): определение отчёта хранится в самой
// таблице (Xrecord в словаре расширения), поэтому таблицу можно скопировать в
// другой чертёж и пересчитать там. Команда ATSPECUPDATE — ручной (детерминированный)
// пересчёт выбранных или всех отчётных таблиц из текущих блоков чертежа.
//
// Это КОСТЯК авто-пересчёта. Авто-триггер по правке блока (ObjectModified + дебаунс +
// блокировка документа + загрузка на старте) добавляется ОТДЕЛЬНЫМ шагом поверх этого
// проверенного пересчёта — чтобы не отдавать конструктору непроверенный реактор.
//
// ВНИМАНИЕ: компилируется на Windows (AutoCAD.NET 24.0.0). В песочнице не собиралось —
// проверяется автосборкой GitHub Actions. Рантайм: .NET Framework 4.8.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AtSpecPlugin.ReportReactor))]

namespace AtSpecPlugin
{
    public class ReportReactor
    {
        // ключ записи определения отчёта в словаре расширения таблицы
        public const string DICTKEY = "ATSPEC_REPORT_DEF";

        // состояние реактора (см. секцию «авто-триггер» ниже)
        private static bool _attached;
        private static bool _busy;     // гасит события от нашей же перезаписи таблиц
        private static bool _dirty;    // в текущей команде менялись блоки/атрибуты
        private static readonly HashSet<Document> _hooked = new HashSet<Document>();

        // ───────────────────── команда: ручной пересчёт ─────────────────────
        [CommandMethod("ATSPECUPDATE")]
        public void AtSpecUpdate()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var sel = ed.GetSelection(new SelectionFilter(new[] {
                new TypedValue((int)DxfCode.Start, "ACAD_TABLE") }));
            List<ObjectId> only = null;
            if (sel.Status == PromptStatus.OK)
            {
                only = new List<ObjectId>();
                foreach (SelectedObject so in sel.Value)
                    if (so != null) only.Add(so.ObjectId);
            }
            else
                ed.WriteMessage("\nТаблицы не выбраны — пересчитываю все отчётные таблицы чертежа.");

            int n = Recompute(doc, only);
            ed.WriteMessage("\nПересчитано отчётных таблиц: " + n + ".");
            _dirty = false;   // ручной пересчёт обнулил «грязь» → авто-триггер не дублирует
        }

        // ───────────────────── авто-триггер (реактор) ─────────────────────
        // Правка блока/атрибута → таблица пересчитывается сама по завершении команды.
        // Дебаунс: копим «грязь» в _dirty, пересчёт один раз на CommandEnded (не на каждое
        // событие). Ре-энтрантность: _busy гасит события от нашей же перезаписи таблицы.
        // AutoCAD однопоточен (UI-поток) → поля без блокировок.

        public static void Attach()
        {
            if (_attached) return;
            _attached = true;
            var dm = AcApp.DocumentManager;
            dm.DocumentCreated += OnDocCreated;
            dm.DocumentToBeDestroyed += OnDocDestroyed;
            foreach (Document doc in dm) Hook(doc);   // уже открытые документы
        }

        public static void Detach()
        {
            if (!_attached) return;
            var dm = AcApp.DocumentManager;
            dm.DocumentCreated -= OnDocCreated;
            dm.DocumentToBeDestroyed -= OnDocDestroyed;
            foreach (Document doc in new List<Document>(_hooked)) Unhook(doc);
            _attached = false;
        }

        private static void Hook(Document doc)
        {
            if (doc == null || _hooked.Contains(doc)) return;
            Database db = doc.Database;
            db.ObjectModified += OnObjModified;
            db.ObjectAppended += OnObjAppended;
            db.ObjectErased += OnObjErased;
            doc.CommandEnded += OnCommandEnded;
            doc.CommandCancelled += OnCommandEnded;
            _hooked.Add(doc);
        }

        private static void Unhook(Document doc)
        {
            if (doc == null || !_hooked.Contains(doc)) return;
            try
            {
                Database db = doc.Database;
                db.ObjectModified -= OnObjModified;
                db.ObjectAppended -= OnObjAppended;
                db.ObjectErased -= OnObjErased;
                doc.CommandEnded -= OnCommandEnded;
                doc.CommandCancelled -= OnCommandEnded;
            }
            catch { }
            _hooked.Remove(doc);
        }

        private static void OnDocCreated(object sender, DocumentCollectionEventArgs e) { Hook(e.Document); }
        private static void OnDocDestroyed(object sender, DocumentCollectionEventArgs e) { Unhook(e.Document); }

        // Правка атрибута (EATTEDIT/двойной клик) шлёт ObjectModified на AttributeReference,
        // а НЕ на владельца-BlockReference. ДЛИНА/ИМЯ — атрибуты, поэтому ловим оба типа.
        private static void OnObjModified(object sender, ObjectEventArgs e)
        {
            if (_busy) return;
            if (e.DBObject is BlockReference || e.DBObject is AttributeReference) _dirty = true;
        }
        private static void OnObjAppended(object sender, ObjectEventArgs e)
        {
            if (_busy) return;
            if (e.DBObject is BlockReference) _dirty = true;   // вставлен новый блок
        }
        private static void OnObjErased(object sender, ObjectErasedEventArgs e)
        {
            if (_busy) return;
            if (e.DBObject is BlockReference) _dirty = true;   // блок удалён/восстановлен
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (!_dirty || _busy) return;
            _dirty = false;
            _busy = true;
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    using (doc.LockDocument())   // правка вне команды иначе кинет eLockViolation
                        Recompute(doc, null);
            }
            catch { /* авто-пересчёт не должен ронять команду пользователя */ }
            finally { _busy = false; }
        }

        // ───────────────────── пересчёт таблиц ─────────────────────
        // only == null  → все таблицы модели, у которых есть наше определение отчёта.
        public static int Recompute(Autodesk.AutoCAD.ApplicationServices.Document doc, List<ObjectId> only)
        {
            string engineExe, configYaml;
            if (!FindEngine(out engineExe, out configYaml))
            {
                doc.Editor.WriteMessage("\nНе найден движок dxf_spec.exe рядом с DLL.");
                return 0;
            }
            Database db = doc.Database;
            var records = CollectRecords(db);          // все блоки модели один раз
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            int count = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                IEnumerable<ObjectId> targets = only;
                if (targets == null)
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var found = new List<ObjectId>();
                    foreach (ObjectId id in ms)
                        if (id.ObjectClass.DxfName == "ACAD_TABLE") found.Add(id);
                    targets = found;
                }

                foreach (ObjectId id in targets)
                {
                    var tbl = tr.GetObject(id, OpenMode.ForRead) as Table;
                    if (tbl == null) continue;
                    string defJson = ReadDef(tr, tbl);
                    if (string.IsNullOrEmpty(defJson)) continue;       // не наша таблица
                    if (Refill(tr, tbl, defJson, records, engineExe, configYaml, ser)) count++;
                }
                tr.Commit();
            }
            return count;
        }

        private static bool Refill(Transaction tr, Table tbl, string defJson,
            List<Dictionary<string, object>> records, string engineExe, string configYaml,
            JavaScriptSerializer ser)
        {
            object reportDef = ser.DeserializeObject(defJson);
            var payload = new Dictionary<string, object> {
                { "blocks", records }, { "action", "report" }, { "report", reportDef } };
            Dictionary<string, object> result;
            try { result = ser.Deserialize<Dictionary<string, object>>(CallEngine(engineExe, configYaml, payload)); }
            catch { return false; }

            var rep = Get(result, "report") as Dictionary<string, object>;
            if (rep == null) return false;
            var header = ToStrList(Get(rep, "header"));
            var rows = Get(rep, "rows") as IList;
            string title = SafeStr(Get(rep, "title"));
            if (rows == null) return false;

            var defDict = reportDef as Dictionary<string, object>;
            bool hideTitle = GetBool(defDict, "hide_title");
            bool hideHeader = GetBool(defDict, "hide_header");

            int nCols = header.Count > 0 ? header.Count
                       : (rows.Count > 0 ? ((IList)rows[0]).Count : tbl.Columns.Count);
            int nRows = rows.Count;
            int titleRow = hideTitle ? -1 : 0;
            int headerRow = hideHeader ? -1 : (hideTitle ? 0 : 1);
            int top = (hideTitle ? 0 : 1) + (hideHeader ? 0 : 1);
            int want = top + nRows; if (want < 1) want = 1;

            tbl.UpgradeOpen();
            tbl.SetSize(want, nCols);
            if (titleRow >= 0)
            {
                tbl.Cells[titleRow, 0].TextString = title ?? "";
                try { tbl.MergeCells(CellRange.Create(tbl, titleRow, 0, titleRow, nCols - 1)); } catch { }
            }
            if (headerRow >= 0)
                for (int c = 0; c < nCols; c++)
                    tbl.Cells[headerRow, c].TextString = c < header.Count ? SafeStr(header[c]) : "";
            for (int r = 0; r < nRows; r++)
            {
                var row = rows[r] as IList;
                for (int c = 0; c < nCols; c++)
                    tbl.Cells[top + r, c].TextString = (row != null && c < row.Count) ? SafeStr(row[c]) : "";
            }
            // #7: убрать возможные хвостовые пустые строки (усечение SetSize иногда оставляет лишние)
            if (tbl.Rows.Count > want)
                tbl.DeleteRows(want, tbl.Rows.Count - want);
            ApplyTableScale(tbl, GetDouble(defDict, "scale", 1.0));   // #6: масштаб из определения
            tbl.GenerateLayout();
            return true;
        }

        // ───────────── определение отчёта в словаре расширения таблицы ─────────────
        public static void StoreDef(Transaction tr, Table tbl, string json)
        {
            if (tbl.ExtensionDictionary.IsNull)
                tbl.CreateExtensionDictionary();
            var dict = (DBDictionary)tr.GetObject(tbl.ExtensionDictionary, OpenMode.ForWrite);
            var rb = new ResultBuffer();
            foreach (string chunk in Chunks(json, 250))     // строки XData/Xrecord ограничены 255 байт
                rb.Add(new TypedValue((int)DxfCode.Text, chunk));
            var xrec = new Xrecord { Data = rb };
            if (dict.Contains(DICTKEY))
                dict.Remove(DICTKEY);
            dict.SetAt(DICTKEY, xrec);
            tr.AddNewlyCreatedDBObject(xrec, true);
        }

        private static string ReadDef(Transaction tr, Table tbl)
        {
            if (tbl.ExtensionDictionary.IsNull) return null;
            var dict = tr.GetObject(tbl.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (dict == null || !dict.Contains(DICTKEY)) return null;
            var xrec = tr.GetObject(dict.GetAt(DICTKEY), OpenMode.ForRead) as Xrecord;
            if (xrec == null || xrec.Data == null) return null;
            var sb = new StringBuilder();
            foreach (TypedValue tv in xrec.Data)
                if (tv.TypeCode == (int)DxfCode.Text) sb.Append(SafeStr(tv.Value));
            return sb.ToString();
        }

        private static IEnumerable<string> Chunks(string s, int n)
        {
            for (int i = 0; i < (s ?? "").Length; i += n)
                yield return s.Substring(i, Math.Min(n, s.Length - i));
        }

        // ───────────── сбор записей блоков модели ─────────────
        private static List<Dictionary<string, object>> CollectRecords(Database db)
        {
            var recs = new List<Dictionary<string, object>>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    var attrs = new Dictionary<string, object>();
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(arId, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) attrs[ar.Tag] = ar.TextString;
                    }
                    recs.Add(new Dictionary<string, object> {
                        { "name", EffectiveName(tr, br) }, { "layer", br.Layer }, { "attributes", attrs } });
                }
                tr.Commit();
            }
            return recs;
        }

        // ───────────── вызов движка (как в ReportCommand) ─────────────
        private static bool FindEngine(out string engineExe, out string configYaml)
        {
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            engineExe = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "dxf_spec.exe"));
            configYaml = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "mapping.yaml"));
            return File.Exists(engineExe) && File.Exists(configYaml);
        }

        private static string CallEngine(string engineExe, string configYaml, object payload)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string tmpIn = Path.Combine(Path.GetTempPath(), "atspec_uin_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(), "atspec_uout_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmpIn, ser.Serialize(payload), new UTF8Encoding(false));
                var psi = new ProcessStartInfo
                {
                    FileName = engineExe,
                    Arguments = "--json -c \"" + configYaml + "\" --in \"" + tmpIn + "\" --out-json \"" + tmpOut + "\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new ApplicationException(err);
                }
                return File.ReadAllText(tmpOut, Encoding.UTF8);
            }
            finally
            {
                try { if (File.Exists(tmpIn)) File.Delete(tmpIn); } catch { }
                try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
            }
        }

        private static object Get(Dictionary<string, object> d, string key)
        { object v; return (d != null && d.TryGetValue(key, out v)) ? v : null; }
        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            { try { return Convert.ToBoolean(v); } catch { return false; } }
            return false;
        }
        private static double GetDouble(Dictionary<string, object> d, string key, double dflt)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            { try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return dflt; } }
            return dflt;
        }

        // Масштаб итоговой таблицы (#6): текст/строки/столбцы × scale, размеры АБСОЛЮТНЫЕ —
        // идемпотентно (повторный пересчёт не накапливает). scale<=1 -> размеры стиля.
        public static void ApplyTableScale(Table tbl, double scale)
        {
            double s = scale <= 0 ? 1.0 : scale;
            if (s == 1.0) return;
            double th = 2.5 * s;
            for (int r = 0; r < tbl.Rows.Count; r++)
                for (int c = 0; c < tbl.Columns.Count; c++)
                    tbl.Cells[r, c].TextHeight = th;
            tbl.SetColumnWidth(th * 10.0);
            tbl.SetRowHeight(th * 1.8);
        }
        private static List<string> ToStrList(object o)
        { var l = new List<string>(); var il = o as IList; if (il != null) foreach (var x in il) l.Add(SafeStr(x)); return l; }
        private static string EffectiveName(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                return btr != null ? btr.Name : br.Name;
            }
            catch { return br.Name; }
        }
        private static string SafeStr(object o) { return o == null ? "" : o.ToString(); }
    }
}
