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
        // ключ сигнатуры раскладки (число столбцов + строк по секциям) — для бережного
        // пересчёта: если структура та же, правим только текст, сохраняя ручное оформление.
        public const string SHAPEKEY = "ATSPEC_REPORT_SHAPE";

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
            string title = SafeStr(Get(rep, "title"));
            var secs = ParseSections(Get(rep, "sections"));
            if (secs.Count == 0) return false;

            var defDict = reportDef as Dictionary<string, object>;
            bool hideTitle = GetBool(defDict, "hide_title");
            double scale = GetDouble(defDict, "scale", 1.0);

            var p = MakePlan(hideTitle, secs);
            string newShape = ComputeShape(p.MaxCols, secs);
            string oldShape = ReadShape(tr, tbl);
            // Бережный пересчёт (C): структура та же (та же сигнатура И габариты таблицы) —
            // обновляем ТОЛЬКО текст ячеек, ручное оформление (ширины/масштаб/объединения)
            // сохраняется. Иначе — полная перестройка (SetSize + масштаб + объединения).
            bool sameShape = oldShape != null && oldShape == newShape
                             && tbl.Rows.Count == p.Want && tbl.Columns.Count == p.MaxCols;

            tbl.UpgradeOpen();
            LayoutSections(tbl, title, hideTitle, scale, secs, !sameShape);
            if (!sameShape) { try { StoreShape(tr, tbl, newShape); } catch { } }
            tbl.GenerateLayout();
            return true;
        }

        // ───────────── общий рендер секций (для вставки и пересчёта) ─────────────
        // Одна секция итоговой таблицы: подпись-разделитель + шапка столбцов + строки данных.
        public class SectionView
        {
            public string Title;            // заголовок секции (пусто = без строки-разделителя)
            public List<string> Header;     // подписи столбцов секции
            public bool HideHeader;
            public List<int[]> Merges;      // объединения шапки секции [s,e] (0-базово)
            public IList Rows;              // строки данных (каждая — IList)
            public int Width;               // столбцов в секции = max(Header, длина строк)
        }

        // Разбор ответа движка report.sections -> список секций для отрисовки.
        public static List<SectionView> ParseSections(object o)
        {
            var res = new List<SectionView>();
            var arr = o as IList;
            if (arr == null) return res;
            foreach (var item in arr)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                var sv = new SectionView
                {
                    Title = SafeStr(Get(d, "title")),
                    Header = ToStrList(Get(d, "header")),
                    HideHeader = GetBool(d, "hide_header"),
                    Merges = ParseMerges(Get(d, "header_merges")),
                    Rows = Get(d, "rows") as IList
                };
                if (sv.Rows == null) sv.Rows = new System.Collections.ArrayList();
                int w = sv.Header.Count;
                foreach (var r in sv.Rows) { var rl = r as IList; if (rl != null && rl.Count > w) w = rl.Count; }
                sv.Width = w < 1 ? 1 : w;
                res.Add(sv);
            }
            return res;
        }

        // План раскладки: сквозная нумерация строк итоговой таблицы по секциям.
        private class Plan
        {
            public int MaxCols, Want, TitleRow;
            public int[] SecTitleRow, SecHeaderRow, SecFirstData;
        }
        private static Plan MakePlan(bool hideTitle, List<SectionView> secs)
        {
            var p = new Plan { MaxCols = 1 };
            foreach (var s in secs) if (s.Width > p.MaxCols) p.MaxCols = s.Width;
            int n = secs.Count;
            p.SecTitleRow = new int[n]; p.SecHeaderRow = new int[n]; p.SecFirstData = new int[n];
            int cur = 0;
            p.TitleRow = -1;
            if (!hideTitle) { p.TitleRow = cur; cur++; }
            for (int i = 0; i < n; i++)
            {
                p.SecTitleRow[i] = -1; p.SecHeaderRow[i] = -1;
                if (!string.IsNullOrEmpty(secs[i].Title)) { p.SecTitleRow[i] = cur; cur++; }
                if (!secs[i].HideHeader) { p.SecHeaderRow[i] = cur; cur++; }
                p.SecFirstData[i] = cur;
                cur += secs[i].Rows.Count;
            }
            p.Want = cur < 1 ? 1 : cur;
            return p;
        }

        // Заполняет таблицу секциями. rebuild=true → SetSize + масштаб + объединения (полная
        // перестройка); rebuild=false → ТОЛЬКО текст ячеек (бережный пересчёт). Возвращает
        // {want, maxCols}. Присваивание текста и объединения обёрнуты в try (объединённые подъячейки).
        public static int[] LayoutSections(Table tbl, string title, bool hideTitle,
            double scale, List<SectionView> secs, bool rebuild)
        {
            var p = MakePlan(hideTitle, secs);
            if (rebuild) tbl.SetSize(p.Want, p.MaxCols);

            // 1) текст: заголовок таблицы + по секциям (подпись / шапка / данные)
            if (p.TitleRow >= 0)
                try { tbl.Cells[p.TitleRow, 0].TextString = title ?? ""; } catch { }
            for (int i = 0; i < secs.Count; i++)
            {
                var s = secs[i];
                if (p.SecTitleRow[i] >= 0)
                    try { tbl.Cells[p.SecTitleRow[i], 0].TextString = s.Title ?? ""; } catch { }
                if (p.SecHeaderRow[i] >= 0)
                    for (int c = 0; c < p.MaxCols; c++)
                        try { tbl.Cells[p.SecHeaderRow[i], c].TextString = c < s.Header.Count ? SafeStr(s.Header[c]) : ""; } catch { }
                for (int r = 0; r < s.Rows.Count; r++)
                {
                    var row = s.Rows[r] as IList;
                    for (int c = 0; c < p.MaxCols; c++)
                        try { tbl.Cells[p.SecFirstData[i] + r, c].TextString = (row != null && c < row.Count) ? SafeStr(row[c]) : ""; } catch { }
                }
            }

            // 2) структура (только перестройка): усечение хвоста, масштаб, затем объединения
            if (rebuild)
            {
                if (tbl.Rows.Count > p.Want) tbl.DeleteRows(p.Want, tbl.Rows.Count - p.Want);
                ApplyTableScale(tbl, scale);                       // #6: масштаб таблицы
                AutoFitColumns(tbl, p.MaxCols, secs);              // #4: ширины столбцов по содержимому (как СПДС)
                if (p.TitleRow >= 0 && p.MaxCols > 1)
                    try { tbl.MergeCells(CellRange.Create(tbl, p.TitleRow, 0, p.TitleRow, p.MaxCols - 1)); } catch { }
                for (int i = 0; i < secs.Count; i++)
                {
                    if (p.SecTitleRow[i] >= 0 && p.MaxCols > 1)
                        try { tbl.MergeCells(CellRange.Create(tbl, p.SecTitleRow[i], 0, p.SecTitleRow[i], p.MaxCols - 1)); } catch { }
                    if (p.SecHeaderRow[i] >= 0)
                        ApplyHeaderMerges(tbl, p.SecHeaderRow[i], p.MaxCols, secs[i].Merges);  // #5: объединение шапки секции
                }
            }
            return new[] { p.Want, p.MaxCols };
        }

        // Сигнатура раскладки: столбцы + по секциям (число строк, есть ли подпись, скрыта ли шапка).
        public static string ComputeShape(int maxCols, List<SectionView> secs)
        {
            var sb = new StringBuilder();
            sb.Append("c").Append(maxCols).Append("|");
            for (int i = 0; i < secs.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(secs[i].Rows.Count)
                  .Append(string.IsNullOrEmpty(secs[i].Title) ? "" : "t")
                  .Append(secs[i].HideHeader ? "h" : "");
            }
            return sb.ToString();
        }

        // ───────────── определение отчёта в словаре расширения таблицы ─────────────
        public static void StoreDef(Transaction tr, Table tbl, string json)
        {
            StoreString(tr, tbl, DICTKEY, json);
        }

        // сигнатура раскладки — для бережного пересчёта (см. SHAPEKEY).
        public static void StoreShape(Transaction tr, Table tbl, string shape)
        {
            StoreString(tr, tbl, SHAPEKEY, shape);
        }
        private static string ReadShape(Transaction tr, Table tbl)
        {
            return ReadString(tr, tbl, SHAPEKEY);
        }

        // запись произвольной строки в словарь расширения таблицы (чанками по 250 — лимит XData/Xrecord).
        private static void StoreString(Transaction tr, Table tbl, string key, string s)
        {
            if (tbl.ExtensionDictionary.IsNull)
                tbl.CreateExtensionDictionary();
            var dict = (DBDictionary)tr.GetObject(tbl.ExtensionDictionary, OpenMode.ForWrite);
            var rb = new ResultBuffer();
            foreach (string chunk in Chunks(s, 250))
                rb.Add(new TypedValue((int)DxfCode.Text, chunk));
            var xrec = new Xrecord { Data = rb };
            if (dict.Contains(key))
                dict.Remove(key);
            dict.SetAt(key, xrec);
            tr.AddNewlyCreatedDBObject(xrec, true);
        }
        private static string ReadString(Transaction tr, Table tbl, string key)
        {
            if (tbl.ExtensionDictionary.IsNull) return null;
            var dict = tr.GetObject(tbl.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (dict == null || !dict.Contains(key)) return null;
            var xrec = tr.GetObject(dict.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (xrec == null || xrec.Data == null) return null;
            var sb = new StringBuilder();
            foreach (TypedValue tv in xrec.Data)
                if (tv.TypeCode == (int)DxfCode.Text) sb.Append(SafeStr(tv.Value));
            return sb.ToString();
        }

        private static string ReadDef(Transaction tr, Table tbl)
        {
            return ReadString(tr, tbl, DICTKEY);
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
                    var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(arId, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) attrs[ar.Tag] = ar.TextString;
                    }
                    // Динам. параметры (ручки): длина доборника / угол створки и т.п. — это
                    // параметры, а не ATTRIB. Без них при ПЕРЕСЧЁТЕ Object.«Длина» обнуляется
                    // (первичная вставка их читает, а пересчёт — нет). ATTRIB в приоритете.
                    if (br.IsDynamicBlock)
                    {
                        foreach (DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                        {
                            string pn = dp.PropertyName;
                            if (string.IsNullOrEmpty(pn) || attrs.ContainsKey(pn)) continue;
                            attrs[pn] = Convert.ToString(dp.Value, System.Globalization.CultureInfo.InvariantCulture);
                        }
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
                    try { tbl.Cells[r, c].TextHeight = th; } catch { }   // объединённые подъячейки пропускаем
            tbl.SetColumnWidth(th * 10.0);
            tbl.SetRowHeight(th * 1.8);
        }

        // #4: ширины столбцов ПО СОДЕРЖИМОМУ (а не одинаковые) — № узкий, наименование/площадь
        // шире, как в таблицах СПДС. Оценка ширины глифа ≈ 0.62·высоты текста (пропорц. шрифт),
        // зажим [4·th, 48·th]. Только при перестройке (rebuild) — ручную ширину бережный пересчёт
        // сохраняет. Подписи (заголовок таблицы/секций) объединены и на ширину столбца не влияют.
        public static void AutoFitColumns(Table tbl, int maxCols, List<SectionView> secs)
        {
            if (tbl == null || maxCols < 1) return;
            double th = 2.5;
            try
            {
                if (tbl.Rows.Count > 0 && tbl.Columns.Count > 0)
                {
                    double h = tbl.Cells[tbl.Rows.Count - 1, 0].TextHeight;
                    if (h > 0) th = h;
                }
            }
            catch { }
            for (int c = 0; c < maxCols && c < tbl.Columns.Count; c++)
            {
                int maxLen = 1;
                foreach (var s in secs)
                {
                    if (c < s.Header.Count)
                    { int l = SafeStr(s.Header[c]).Length; if (l > maxLen) maxLen = l; }
                    foreach (var r in s.Rows)
                    {
                        var rl = r as IList;
                        if (rl != null && c < rl.Count)
                        { int l = SafeStr(rl[c]).Length; if (l > maxLen) maxLen = l; }
                    }
                }
                double w = maxLen * th * 0.62 + th * 1.4;
                double lo = th * 4.0, hi = th * 48.0;
                if (w < lo) w = lo;
                if (w > hi) w = hi;
                try { tbl.Columns[c].Width = w; } catch { }
            }
        }

        // #5: распарсить спаны объединения шапки из определения ([[s,e],...]).
        public static List<int[]> ParseMerges(object o)
        {
            var res = new List<int[]>();
            var outer = o as IList;
            if (outer == null) return res;
            foreach (var item in outer)
            {
                var pair = item as IList;
                if (pair == null || pair.Count < 2) continue;
                try
                {
                    int a = Convert.ToInt32(pair[0]), b = Convert.ToInt32(pair[1]);
                    if (a > b) { int t = a; a = b; b = t; }
                    res.Add(new[] { a, b });
                }
                catch { }
            }
            return res;
        }

        // #5: объединить ячейки СТРОКИ-ШАПКИ по спанам (только если шапка видима).
        public static void ApplyHeaderMerges(Table tbl, int headerRow, int nCols, List<int[]> merges)
        {
            if (headerRow < 0 || merges == null) return;
            foreach (var sp in merges)
            {
                int s = sp[0] < 0 ? 0 : sp[0];
                int e = sp[1] > nCols - 1 ? nCols - 1 : sp[1];
                if (e > s)
                    try { tbl.MergeCells(CellRange.Create(tbl, headerRow, s, headerRow, e)); } catch { }
            }
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
