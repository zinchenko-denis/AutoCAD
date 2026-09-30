using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AFacadesPlugin
{
    internal static class CladdingTableCommand
    {
        private const string TableKey = "ATFQUANTITY_TABLE";
        private const string TableSchema = "facade_quantity_table/1";

        internal static void Run(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            try { RunCore(doc); }
            catch (IOException ex)
            { doc.Editor.WriteMessage("\nВедомость не записана: " + ex.Message); }
            catch (UnauthorizedAccessException ex)
            { doc.Editor.WriteMessage("\nНет доступа для записи ведомости: " + ex.Message); }
        }

        private static void RunCore(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;
            string choice;
            if (!Ask(ed, "\nВыбрать область облицовки [Слой/Объекты] <Слой>: ", "Слой Объекты", "Слой", out choice)) return;
            List<ObjectId> ids;
            if (choice == "Слой")
            { if (!PickLayers(ed, db, out ids)) return; }
            else
            {
                var picked = ed.GetSelection(new PromptSelectionOptions {
                    MessageForAdding = "\nВыберите зоны, метки раскладки или детали облицовки (учитывается вся зона детали): " });
                if (picked.Status != PromptStatus.OK) { Cancel(ed); return; }
                ids = new List<ObjectId>();
                foreach (SelectedObject item in picked.Value) if (item != null) ids.Add(item.ObjectId);
            }
            if (!Ask(ed, "\nГруппировка [ПоЗонам/Общая] <ПоЗонам>: ", "ПоЗонам Общая", "ПоЗонам", out choice)) return;
            bool byZone = choice != "Общая";
            if (!Ask(ed, "\nСостав [Детали/ДеталиИРаскрой] <Детали>: ", "Детали ДеталиИРаскрой", "Детали", out choice)) return;
            bool cutting = choice == "ДеталиИРаскрой";
            CladdingTableData data;
            string sourceStamp;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Read(tr, db, ids, byZone, cutting, ed, out data, out sourceStamp)) return;
                tr.Commit();
            }
            string note = "";
            if (!Preview(data, ref note)) { Cancel(ed); return; }
            if (!Ask(ed, "\nКуда вывести ведомость [Чертеж/Ексель/Оба] <Чертеж>: ", "Чертеж Ексель Оба", "Чертеж", out choice)) return;
            bool toDwg = choice != "Ексель", toXlsx = choice != "Чертеж";
            ObjectId target = ObjectId.Null;
            Point3d point = Point3d.Origin;
            double height = 250;
            string oldTargetStamp = null;
            if (toDwg)
            {
                if (!Ask(ed, "\nТаблица [Новая/Обновить] <Новая>: ", "Новая Обновить", "Новая", out choice)) return;
                if (choice == "Обновить")
                {
                    var options = new PromptEntityOptions("\nВыберите ведомость облицовки ATFTABLE для обновления: ");
                    options.SetRejectMessage("\nНужна таблица ведомости облицовки ATFTABLE.");
                    options.AddAllowedClass(typeof(Table), true);
                    var picked = ed.GetEntity(options);
                    if (picked.Status != PromptStatus.OK) { Cancel(ed); return; }
                    target = picked.ObjectId;
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var table = tr.GetObject(target, OpenMode.ForRead) as Table;
                        Dictionary<string, object> old;
                        if (!ReadTable(tr, table, out old, out oldTargetStamp))
                        { ed.WriteMessage("\nОбновление отменено: выбранная таблица не является ведомостью облицовки текущего формата."); return; }
                        point = table.Position;
                        try { height = Convert.ToDouble(old["text_height"], CultureInfo.InvariantCulture); }
                        catch { height = 250; }
                        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height)) height = 250;
                        if (string.IsNullOrEmpty(note)) note = Convert.ToString(old["user_note"], CultureInfo.InvariantCulture);
                        tr.Commit();
                    }
                    // Показываем сохранённое примечание и новый состав до любых изменений таблицы.
                    if (!Preview(data, ref note)) { Cancel(ed); return; }
                }
                var heightPrompt = new PromptDoubleOptions("\nВысота текста таблицы, мм: ")
                    { DefaultValue = height, AllowNegative = false, AllowZero = false };
                var heightResult = ed.GetDouble(heightPrompt);
                if (heightResult.Status == PromptStatus.Cancel) { Cancel(ed); return; }
                if (heightResult.Status == PromptStatus.OK) height = heightResult.Value;
                if (target.IsNull)
                {
                    var position = ed.GetPoint("\nТочка вставки ведомости облицовки: ");
                    if (position.Status != PromptStatus.OK) { Cancel(ed); return; }
                    point = position.Value;
                }
            }
            string path = null;
            if (toXlsx)
            {
                using (var dialog = new System.Windows.Forms.SaveFileDialog {
                    Filter = "Excel (*.xlsx)|*.xlsx", OverwritePrompt = true,
                    FileName = (string.IsNullOrEmpty(db.Filename) ? "ведомость" : Path.GetFileNameWithoutExtension(db.Filename)) + "_облицовка.xlsx",
                    InitialDirectory = string.IsNullOrEmpty(db.Filename) ? null : Path.GetDirectoryName(db.Filename) })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) { Cancel(ed); return; }
                    path = dialog.FileName;
                }
            }
            // Все пользовательские запросы завершены. До этой точки DWG и существующий XLSX неизменны.
            var rows = data.Rows(note);
            using (var staged = path == null ? null : new QuantityXlsxFile(path, rows))
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CladdingTableData refreshed;
                string refreshedStamp;
                if (!Read(tr, db, ids, byZone, cutting, ed, out refreshed, out refreshedStamp)) return;
                if (sourceStamp != refreshedStamp)
                { ed.WriteMessage("\nВедомость не создана: состав изменился после предпросмотра. Повторите ATFTABLE."); return; }
                if (toDwg)
                {
                    Table table;
                    if (target.IsNull)
                    {
                        table = new Table { TableStyle = db.Tablestyle, Position = point };
                        var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                        space.AppendEntity(table);
                        tr.AddNewlyCreatedDBObject(table, true);
                    }
                    else
                    {
                        table = tr.GetObject(target, OpenMode.ForWrite) as Table;
                        Dictionary<string, object> prior;
                        string stamp;
                        if (!ReadTable(tr, table, out prior, out stamp) || stamp != oldTargetStamp)
                        { ed.WriteMessage("\nОбновление отменено: выбранная таблица изменилась после предпросмотра."); return; }
                    }
                    Fill(table, rows, height);
                    var metadata = new Dictionary<string, object> {
                        { "schema", TableSchema }, { "owner", table.Handle.ToString() },
                        { "user_note", note }, { "text_height", height }, { "by_zone", byZone },
                        { "include_cutting", cutting }, { "scope", data.Scope }, { "source_snapshot", sourceStamp },
                        { "completeness", data.Complete ? "complete" : "partial" }, { "form_shapes", data.FormShapes }, { "rows", rows } };
                    ZoneCommand.StoreZoneData(tr, table, Serializer().Serialize(metadata), TableKey, ids);
                }
                // Публикуем Excel до commit DWG. При отказе записи CAD-транзакция откатится.
                // Если commit DWG завершится ошибкой, Dispose восстановит прежний Excel.
                if (staged != null) staged.Publish();
                tr.Commit();
                if (staged != null) staged.Complete();
            }
            ed.WriteMessage("\nATFTABLE: ведомость облицовки " + (target.IsNull ? "сформирована" : "обновлена") +
                (data.Complete ? "." : "; неполнота указана в таблице.") +
                (path == null ? "" : "\nExcel: " + path));
        }

        private static bool Read(Transaction tr, Database db, List<ObjectId> ids, bool byZone, bool cutting,
            Editor ed, out CladdingTableData data, out string stamp)
        {
            data = null; stamp = null;
            var selection = FacadeQuantityStore.ReadCladding(tr, db, ids);
            if (!selection.Ok)
            { ed.WriteMessage("\nВедомость не создана: " + selection.Reason); return false; }
            var result = FacadeQuantitiesCore.BuildRows(selection.Reports, selection.SelectedZoneIds, byZone, cutting);
            if (!result.ok)
            {
                ed.WriteMessage("\nВедомость не создана:");
                foreach (var issue in result.issues) ed.WriteMessage("\n  " + issue.message);
                return false;
            }
            data = CladdingTableData.Build(result, selection.Reports, selection.SelectedZoneIds, selection.Warnings, byZone, cutting);
            using (var hash = SHA256.Create())
                stamp = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Serializer().Serialize(new object[] { selection.Reports, selection.SelectedZoneIds, selection.Warnings })))).Replace("-", "");
            return true;
        }

        private static bool PickLayers(Editor ed, Database db, out List<ObjectId> ids)
        {
            ids = new List<ObjectId>();
            var counts = new Dictionary<string, int>();
            var byLayer = new Dictionary<string, List<ObjectId>>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null) continue;
                    if (!byLayer.ContainsKey(entity.Layer)) byLayer[entity.Layer] = new List<ObjectId>();
                    byLayer[entity.Layer].Add(id);
                    if (!FacadeQuantityStore.IsCandidate(tr, entity)) continue;
                    if (!counts.ContainsKey(entity.Layer)) counts[entity.Layer] = 0;
                    counts[entity.Layer]++;
                }
                tr.Commit();
            }
            if (counts.Count == 0)
            { ed.WriteMessage("\nНет зон или раскладок облицовки. Сначала выполните ATFZONE и ATTILE либо ATCLAD."); return false; }
            using (var form = new LayerPickForm(counts, null, "облицовка",
                "Выберите слои зон или облицовки. Все объекты выбранных слоёв проверяются; детали включают свои зоны целиком.", "носителей"))
            {
                if (AcApp.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) { Cancel(ed); return false; }
                foreach (var layer in form.Selected) ids.AddRange(byLayer[layer]);
            }
            return ids.Count > 0;
        }

        private static bool Preview(CladdingTableData data, ref string note)
        {
            using (var form = new CladdingTablePreview(data, note))
            {
                if (AcApp.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) return false;
                note = form.UserNote; return true;
            }
        }

        private static bool ReadTable(Transaction tr, Table table, out Dictionary<string, object> data, out string raw)
        {
            data = null; raw = null;
            if (table == null) return false;
            raw = ZoneCommand.ReadZoneData(tr, table, TableKey);
            if (string.IsNullOrEmpty(raw)) return false;
            try { data = Serializer().DeserializeObject(raw) as Dictionary<string, object>; }
            catch { return false; }
            return data != null && Text(data, "schema") == TableSchema && Text(data, "owner") == table.Handle.ToString() &&
                data.ContainsKey("user_note") && data.ContainsKey("text_height");
        }

        private static void Fill(Table table, List<object[]> rows, double height)
        {
            if (table.Rows.Count > 0 && table.Columns.Count > 0)
                table.UnmergeCells(CellRange.Create(table, 0, 0, table.Rows.Count - 1, table.Columns.Count - 1));
            table.SetSize(rows.Count, CladdingTableData.Headers.Length);
            table.UnmergeCells(CellRange.Create(table, 0, 0, rows.Count - 1, CladdingTableData.Headers.Length - 1));
            double[] widths = { 7, 12, 12, 8, 24, 10, 4, 7, 8, 24 };
            for (int col = 0; col < widths.Length; col++) table.Columns[col].Width = widths[col] * height;
            for (int row = 0; row < rows.Count; row++)
            {
                table.Rows[row].Height = 2.8 * height;
                for (int col = 0; col < widths.Length; col++)
                {
                    string text = col < rows[row].Length ? CladdingTableData.Display(rows[row][col]) : "";
                    table.Cells[row, col].TextString = EscapeTableText(text);
                    table.Cells[row, col].TextHeight = height;
                    double lines = TextLines(text, widths[col] * 1.65);
                    if (rows[row].Length != 1) table.Rows[row].Height = Math.Max(table.Rows[row].Height, (lines + 1) * 1.5 * height);
                }
                if (rows[row].Length == 1)
                {
                    table.MergeCells(CellRange.Create(table, row, 0, row, widths.Length - 1));
                    table.Rows[row].Height = Math.Max(2.8, TextLines(CladdingTableData.Display(rows[row][0]), 180) * 1.5 + 1) * height;
                }
            }
            table.GenerateLayout();
        }

        private static string EscapeTableText(string text)
        { return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("%", "\\U+0025").Replace("\n", "\\P"); }
        private static double TextLines(string text, double width)
        {
            double count = 0;
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                count += Math.Max(1, Math.Ceiling(line.Length / width));
            return count;
        }
        private static bool Ask(Editor ed, string prompt, string keywords, string defaultValue, out string value)
        {
            var answer = ed.GetKeywords(new PromptKeywordOptions(prompt, keywords));
            value = answer.Status == PromptStatus.OK && !string.IsNullOrEmpty(answer.StringResult) ? answer.StringResult : defaultValue;
            if (answer.Status == PromptStatus.Cancel) { Cancel(ed); return false; }
            return true;
        }
        private static void Cancel(Editor ed) { ed.WriteMessage("\nОтменено. Прежние таблица и файл сохранены."); }
        private static string Text(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
    }
}
