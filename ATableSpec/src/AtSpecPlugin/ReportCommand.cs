// ATSPECREPORT — "свой отчёт": выбрать блоки -> окно с выражениями по столбцам
// (аналог "Шаблона отчёта" СПДС) -> движок dxf_spec (action=report) -> AcDbTable.
//
// Каркас (этап 1): один шаблон отчёта (источник-фильтр по слою/атрибуту, столбцы-
// выражения, группировка по столбцу, сортировка). Производные строки (несколько
// шаблонов в одном отчёте) и реактор авто-пересчёта по правке блока — следующими
// этапами; движок их уже поддерживает (templates[] / action=report).
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собиралось — проверяется автосборкой GitHub Actions (workflow check).
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013-2024).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AtSpecPlugin.ReportCommands))]

namespace AtSpecPlugin
{
    public class ReportCommands
    {
        [CommandMethod("ATSPECREPORT")]
        public void AtSpecReport()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            // --- 1. выбор блоков ---
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите блоки: " };
            PromptSelectionResult sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 2. собрать записи блоков (имя/слой/атрибуты) -> формат контракта ---
            var records = new List<Dictionary<string, object>>();
            var layerSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var fieldSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            // карта значений для контекстного фильтра: слой -> поле -> уникальные значения; "" = все блоки
            var valuesRaw = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            var styleSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);   // текстстили чертежа -> «Шрифт»
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    if (so == null) continue;
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(arId, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) { attrs[ar.Tag] = ar.TextString; fieldSet.Add(ar.Tag); }
                    }
                    // Динамические свойства блока (ручки): у доборников и т.п. длина/ширина —
                    // это параметры, а не ATTRIB; без них Object.«Длина» пустой. ATTRIB в приоритете.
                    if (br.IsDynamicBlock)
                    {
                        foreach (DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                        {
                            string pn = dp.PropertyName;
                            if (string.IsNullOrEmpty(pn) || attrs.ContainsKey(pn)) continue;
                            attrs[pn] = Convert.ToString(dp.Value, System.Globalization.CultureInfo.InvariantCulture);
                            fieldSet.Add(pn);
                        }
                    }
                    layerSet.Add(br.Layer);
                    string effName = EffectiveName(tr, br);
                    // накопить значения полей для контекстного фильтра (по слою и в общий "")
                    AddVal(valuesRaw, br.Layer, "Слой", br.Layer);
                    AddVal(valuesRaw, br.Layer, "Имя", effName);
                    foreach (var kv in attrs)
                        AddVal(valuesRaw, br.Layer, kv.Key, Convert.ToString(kv.Value));
                    records.Add(new Dictionary<string, object>
                    {
                        { "name", effName },
                        { "layer", br.Layer },
                        { "attributes", attrs }
                    });
                }
                // текстстили чертежа -> выпадушка «Шрифт» (единый стиль документации)
                try
                {
                    var tst = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
                    if (tst != null)
                        foreach (ObjectId sid in tst)
                        {
                            var rec = tr.GetObject(sid, OpenMode.ForRead) as TextStyleTableRecord;
                            if (rec != null && !string.IsNullOrEmpty(rec.Name)) styleSet.Add(rec.Name);
                        }
                }
                catch { }
                tr.Commit();
            }
            if (records.Count == 0) { ed.WriteMessage("\nСреди выбранного нет блоков."); return; }

            // --- 3. найти движок рядом с DLL ---
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string engineExe = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "dxf_spec.exe"));
            string configYaml = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "mapping.yaml"));
            if (!File.Exists(engineExe)) { ed.WriteMessage("\nНе найден движок: " + engineExe); return; }
            if (!File.Exists(configYaml)) { ed.WriteMessage("\nНе найден конфиг: " + configYaml); return; }

            // поля для подсказки в окне: атрибуты/параметры выбранных блоков + служебные.
            // Скрываем переменные деталировки (в спецификациях не участвуют).
            // нормализатор имени поля (без «», без крайних пробелов, верхний регистр) — как _nk
            // в движке. Деталировочные имена приходят «как есть» (иной регистр/пробел), поэтому
            // регистрозависимый HashSet их пропускал → они текли в фильтр.
            System.Func<string, string> nkf = z => (z ?? "").Trim().Trim('«', '»', '"', ' ').ToUpperInvariant();
            var HIDE = new[] { "DOBL", "DOBR", "KLL", "KLR", "L", "R", "UGL", "UGR" };
            var HIDEN = new HashSet<string>();
            foreach (var h0 in HIDE) HIDEN.Add(nkf(h0));
            var fields = new List<string>();
            foreach (var f in fieldSet) if (!HIDEN.Contains(nkf(f))) fields.Add(f);
            foreach (var extra in new[] { "Имя", "Слой", "Длина", "Ширина", "Высота" })
                if (!fields.Exists(z => string.Equals(z, extra, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(extra);
            var layers = new List<string>(layerSet);
            var textStyles = new List<string>(styleSet);
            // значения для фильтра: HashSet -> отсортированный List. #5: ту же деталировку
            // вырезаем и из ключей карты «слой→поле→значения» (фильтр берёт поля отсюда).
            var valuesByLayer = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvL in valuesRaw)
            {
                var byF = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvF in kvL.Value)
                {
                    if (HIDEN.Contains(nkf(kvF.Key))) continue;   // деталировку в фильтр не пускаем
                    var lst = new List<string>(kvF.Value);
                    lst.Sort(StringComparer.OrdinalIgnoreCase);
                    byF[kvF.Key] = lst;
                }
                valuesByLayer[kvL.Key] = byF;
            }

            // --- 4. построитель отчёта (шаблон выбирается списком прямо в окне) ---
            var form = new ReportBuilderForm(layers, fields, valuesByLayer, textStyles);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 5. payload: action=report + определение отчёта ---
            var payload = new Dictionary<string, object>
            {
                { "blocks", records },
                { "action", "report" },
                { "report", form.ReportDef }
            };

            // --- 6. вызов движка ---
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> result;
            try
            {
                result = ser.Deserialize<Dictionary<string, object>>(CallEngine(engineExe, configYaml, payload));
            }
            catch (System.Exception e)
            {
                ed.WriteMessage("\nОшибка движка (report): " + e.Message);
                return;
            }
            if (!Convert.ToBoolean(Get(result, "ok"))) { ed.WriteMessage("\nДвижок вернул ошибку."); return; }

            var rep = Get(result, "report") as Dictionary<string, object>;
            if (rep == null) { ed.WriteMessage("\nПустой отчёт."); return; }
            string title = SafeStr(Get(rep, "title"));
            var secs = ReportReactor.ParseSections(Get(rep, "sections"));
            if (secs.Count == 0) { ed.WriteMessage("\nОтчёт без секций."); return; }
            int totalRows = 0; foreach (var s in secs) totalRows += s.Rows.Count;
            if (totalRows == 0) { ed.WriteMessage("\nВ отчёт не попало ни одной строки."); return; }

            // --- 7. точка вставки ---
            PromptPointResult pr = ed.GetPoint("\nТочка вставки таблицы: ");
            if (pr.Status != PromptStatus.OK) return;

            // --- 8. AcDbTable: заголовок + секции (подпись/шапка/данные) + определение в таблице ---
            bool hideTitle = GetBoolFlag(form.ReportDef, "hide_title");
            double scale = GetDoubleFlag(form.ReportDef, "scale", 1.0);
            string fontName = SafeStr(Get(form.ReportDef, "font"));
            DrawTable(db, pr.Value, title, secs, ser.Serialize(form.ReportDef), hideTitle, scale, fontName);
            ed.WriteMessage("\nГотово: \"" + title + "\", секций: " + secs.Count + ", строк: " + totalRows + ". Пересчёт — ATSPECUPDATE.");
        }

        // ATSPECEDIT — обратная связь: выбрать готовую таблицу ATableSpec, прочитать её
        // определение (ReadDef), открыть построитель ЗАПОЛНЕННЫМ, по OK перезаписать
        // определение и пересобрать таблицу НА МЕСТЕ через реактор (Recompute).
        [CommandMethod("ATSPECEDIT")]
        public void AtSpecEdit()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            var peo = new PromptEntityOptions("\nВыберите таблицу ATableSpec для правки: ");
            peo.SetRejectMessage("\nНужна таблица.");
            peo.AddAllowedClass(typeof(Table), false);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }
            ObjectId tblId = per.ObjectId;

            string defJson = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var tbl = tr.GetObject(tblId, OpenMode.ForRead) as Table;
                if (tbl != null) defJson = ReportReactor.ReadDef(tr, tbl);
                tr.Commit();
            }
            if (string.IsNullOrEmpty(defJson))
            { ed.WriteMessage("\nУ этой таблицы нет определения ATableSpec (создайте через ATSPECREPORT)."); return; }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            object def;
            try { def = ser.DeserializeObject(defJson); }
            catch (System.Exception e) { ed.WriteMessage("\nНе удалось прочитать определение: " + e.Message); return; }

            // данные для выпадушек формы — со всех блоков чертежа
            List<string> layers, fields, textStyles;
            Dictionary<string, Dictionary<string, List<string>>> valuesByLayer;
            GatherFormData(db, out layers, out fields, out valuesByLayer, out textStyles);

            var form = ReportBuilderForm.FromDef(def, layers, fields, valuesByLayer, textStyles);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK) { ed.WriteMessage("\nПравка отменена."); return; }

            // перезаписать определение и пересобрать на месте (реактор читает def из таблицы)
            string newDefJson = ser.Serialize(form.ReportDef);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var tbl = tr.GetObject(tblId, OpenMode.ForWrite) as Table;
                if (tbl == null) { ed.WriteMessage("\nТаблица недоступна."); return; }
                try { ReportReactor.StoreDef(tr, tbl, newDefJson); } catch { }
                tr.Commit();
            }
            int n = ReportReactor.Recompute(doc, new List<ObjectId> { tblId });
            ed.WriteMessage(n > 0 ? "\nТаблица обновлена по новому определению."
                                  : "\nОпределение сохранено, но пересборка не дала строк (проверьте фильтры/блоки).");
        }

        // ATSPECEXPORT — выгрузка готовой таблицы в CSV (для внешней программы оптимизации
        // раскроя Алексея). Сырой дамп ячеек: разделитель «;», UTF-8 с BOM.
        [CommandMethod("ATSPECEXPORT")]
        public void AtSpecExport()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            var peo = new PromptEntityOptions("\nВыберите таблицу для экспорта в CSV: ");
            peo.SetRejectMessage("\nНужна таблица.");
            peo.AddAllowedClass(typeof(Table), false);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            var sb = new StringBuilder();
            int rows = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var tbl = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Table;
                if (tbl == null) { ed.WriteMessage("\nЭто не таблица."); return; }
                rows = tbl.Rows.Count;
                int cols = tbl.Columns.Count;
                for (int r = 0; r < rows; r++)
                {
                    var cells = new List<string>();
                    for (int c = 0; c < cols; c++)
                    {
                        string t;
                        try { t = tbl.Cells[r, c].TextString ?? ""; } catch { t = ""; }
                        cells.Add(CsvEscape(t));
                    }
                    sb.Append(string.Join(";", cells)).Append("\r\n");
                }
                tr.Commit();
            }
            if (rows == 0) { ed.WriteMessage("\nТаблица пуста."); return; }

            string path;
            using (var dlg = new SaveFileDialog
            {
                Title = "Экспорт таблицы в CSV", Filter = "CSV (*.csv)|*.csv",
                DefaultExt = "csv", FileName = "atspec_export.csv", OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) { ed.WriteMessage("\nЭкспорт отменён."); return; }
                path = dlg.FileName;
            }
            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // BOM — кириллица в Excel
                ed.WriteMessage("\nЭкспортировано строк: " + rows + " -> " + path);
            }
            catch (System.Exception e) { ed.WriteMessage("\nНе удалось записать файл: " + e.Message); }
        }

        private static string CsvEscape(string s)
        {
            if (s == null) s = "";
            bool quote = s.IndexOf('"') >= 0 || s.IndexOf(';') >= 0 || s.IndexOf(',') >= 0
                         || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
            if (s.IndexOf('"') >= 0) s = s.Replace("\"", "\"\"");
            return quote ? "\"" + s + "\"" : s;
        }

        // Сбор данных для выпадушек формы (слои/поля/значения/текстстили) по ВСЕМ блокам
        // модели — для ATSPECEDIT (в отличие от ATSPECREPORT, где блоки выбирает пользователь).
        // Деталировочные поля (DOBL/…/UGR) вырезаются нормализованным сравнением (как в ATSPECREPORT).
        private static void GatherFormData(Database db,
            out List<string> layers, out List<string> fields,
            out Dictionary<string, Dictionary<string, List<string>>> valuesByLayer, out List<string> textStyles)
        {
            var layerSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var fieldSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var valuesRaw = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            var styleSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

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
                        if (ar != null) { attrs[ar.Tag] = ar.TextString; fieldSet.Add(ar.Tag); }
                    }
                    if (br.IsDynamicBlock)
                        foreach (DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                        {
                            string pn = dp.PropertyName;
                            if (string.IsNullOrEmpty(pn) || attrs.ContainsKey(pn)) continue;
                            attrs[pn] = Convert.ToString(dp.Value, System.Globalization.CultureInfo.InvariantCulture);
                            fieldSet.Add(pn);
                        }
                    layerSet.Add(br.Layer);
                    string effName = EffectiveName(tr, br);
                    AddVal(valuesRaw, br.Layer, "Слой", br.Layer);
                    AddVal(valuesRaw, br.Layer, "Имя", effName);
                    foreach (var kv in attrs)
                        AddVal(valuesRaw, br.Layer, kv.Key, Convert.ToString(kv.Value));
                }
                try
                {
                    var tst = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
                    if (tst != null)
                        foreach (ObjectId sid in tst)
                        {
                            var rec = tr.GetObject(sid, OpenMode.ForRead) as TextStyleTableRecord;
                            if (rec != null && !string.IsNullOrEmpty(rec.Name)) styleSet.Add(rec.Name);
                        }
                }
                catch { }
                tr.Commit();
            }

            System.Func<string, string> nkf = z => (z ?? "").Trim().Trim('«', '»', '"', ' ').ToUpperInvariant();
            var HIDE = new[] { "DOBL", "DOBR", "KLL", "KLR", "L", "R", "UGL", "UGR" };
            var HIDEN = new HashSet<string>();
            foreach (var h0 in HIDE) HIDEN.Add(nkf(h0));
            fields = new List<string>();
            foreach (var f in fieldSet) if (!HIDEN.Contains(nkf(f))) fields.Add(f);
            foreach (var extra in new[] { "Имя", "Слой", "Длина", "Ширина", "Высота" })
                if (!fields.Exists(z => string.Equals(z, extra, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(extra);
            layers = new List<string>(layerSet);
            textStyles = new List<string>(styleSet);
            valuesByLayer = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvL in valuesRaw)
            {
                var byF = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvF in kvL.Value)
                {
                    if (HIDEN.Contains(nkf(kvF.Key))) continue;
                    var lst = new List<string>(kvF.Value);
                    lst.Sort(StringComparer.OrdinalIgnoreCase);
                    byF[kvF.Key] = lst;
                }
                valuesByLayer[kvL.Key] = byF;
            }
        }

        // Диагностика: точные имена/значения/типы атрибутов и динам. параметров выбранных
        // блоков + габарит. Маркеры «» показывают крайние пробелы в именах. Нужна, чтобы
        // понять, под каким именем живой API отдаёт длину/ширину/высоту (имена ручек разнятся).
        [CommandMethod("ATSPECDUMP")]
        public void AtSpecDump()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите блок(и) для диагностики: " };
            PromptSelectionResult sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                int idx = 0;
                foreach (SelectedObject so in sel.Value)
                {
                    if (so == null) continue;
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    idx++;
                    ed.WriteMessage("\n===== блок #" + idx + " =====");
                    ed.WriteMessage("\n  слой: «" + br.Layer + "»");
                    ed.WriteMessage("\n  имя : «" + EffectiveName(tr, br) + "»  (динамоблок: " + br.IsDynamicBlock + ")");
                    ed.WriteMessage("\n  поворот: " + (br.Rotation * 180.0 / Math.PI).ToString("0.#", ci) + " град");
                    try
                    {
                        Extents3d ext = br.GeometricExtents;
                        ed.WriteMessage("\n  габарит: Ш=" + (ext.MaxPoint.X - ext.MinPoint.X).ToString("0.#", ci)
                                      + "  В=" + (ext.MaxPoint.Y - ext.MinPoint.Y).ToString("0.#", ci));
                    }
                    catch { ed.WriteMessage("\n  габарит: (недоступен)"); }

                    ed.WriteMessage("\n  -- ATTRIB --");
                    bool anyAttr = false;
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(arId, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) { ed.WriteMessage("\n    «" + ar.Tag + "» = «" + ar.TextString + "»"); anyAttr = true; }
                    }
                    if (!anyAttr) ed.WriteMessage("\n    (нет)");

                    ed.WriteMessage("\n  -- динам. параметры --");
                    bool anyDyn = false;
                    if (br.IsDynamicBlock)
                    {
                        foreach (DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                        {
                            string val;
                            try { val = Convert.ToString(dp.Value, ci); } catch { val = "<?>"; }
                            string tn = dp.Value == null ? "null" : dp.Value.GetType().Name;
                            ed.WriteMessage("\n    «" + dp.PropertyName + "» = «" + val + "»  [" + tn + "]");
                            anyDyn = true;
                        }
                    }
                    if (!anyDyn) ed.WriteMessage("\n    (нет)");
                }
                tr.Commit();
            }
            ed.WriteMessage("\n===== конец =====");
        }

        private static void DrawTable(Database db, Point3d pos, string title,
            List<ReportReactor.SectionView> secs, string defJson, bool hideTitle, double scale, string font)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var tbl = new Table();
                tbl.TableStyle = db.Tablestyle;
                tbl.Position = pos;
                // сначала делаем таблицу резидентной — тогда применение текстстиля к ячейкам
                // (Cells[].TextStyleId в LayoutSections) безопасно, как и при пересчёте (Refill).
                ms.AppendEntity(tbl);
                tr.AddNewlyCreatedDBObject(tbl, true);
                // шрифт = текстстиль чертежа по имени из формы; пусто/не найден → не переопределять
                ObjectId fontId = ReportReactor.ResolveTextStyle(tr, db, font);
                // раскладка секций (rebuild): SetSize + текст + усечение + масштаб + объединения + шрифт
                int[] wm = ReportReactor.LayoutSections(tbl, title, hideTitle, scale, secs, true, fontId);
                tbl.GenerateLayout();
                // определение отчёта + сигнатуру раскладки — в саму таблицу (для пересчёта ATSPECUPDATE)
                try { ReportReactor.StoreDef(tr, tbl, defJson); } catch { }
                try { ReportReactor.StoreShape(tr, tbl, ReportReactor.ComputeShape(wm[1], secs)); } catch { }
                tr.Commit();
            }
        }

        // --- вызов движка через временные файлы (как в Commands.cs) ---
        private static string CallEngine(string engineExe, string configYaml, object payload)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string tmpIn = Path.Combine(Path.GetTempPath(), "atspec_rin_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(), "atspec_rout_" + Guid.NewGuid().ToString("N") + ".json");
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
            finally { TryDelete(tmpIn); TryDelete(tmpOut); }
        }

        private static object Get(Dictionary<string, object> d, string key)
        { object v; return (d != null && d.TryGetValue(key, out v)) ? v : null; }

        private static bool GetBoolFlag(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            { try { return Convert.ToBoolean(v); } catch { return false; } }
            return false;
        }

        private static double GetDoubleFlag(Dictionary<string, object> d, string key, double dflt)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            { try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return dflt; } }
            return dflt;
        }

        private static List<string> ToStrList(object o)
        {
            var list = new List<string>(); var il = o as IList;
            if (il != null) foreach (var x in il) list.Add(SafeStr(x));
            return list;
        }

        // накопитель значений поля для контекстного фильтра: пишем в свой слой и в общий ""
        private static void AddVal(Dictionary<string, Dictionary<string, HashSet<string>>> map,
                                   string layer, string field, string val)
        {
            if (string.IsNullOrEmpty(field)) return;
            val = (val ?? "").Trim();
            if (val.Length == 0) return;
            string[] keys = { layer ?? "", "" };
            foreach (var key in keys)
            {
                Dictionary<string, HashSet<string>> byF;
                if (!map.TryGetValue(key, out byF))
                { byF = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); map[key] = byF; }
                HashSet<string> set;
                if (!byF.TryGetValue(field, out set))
                { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); byF[field] = set; }
                set.Add(val);
            }
        }

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
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
