using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using FacadeSafety;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AFacadesPlugin
{
    internal static class ManualQuantityCommand
    {
        internal static void Run(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            try
            {
                string action;
                if (!Ask(doc.Editor, "\nРучные позиции [Импорт/Исключить] <Импорт>: ", "Импорт Исключить", "Импорт", out action)) return;
                if (action == "Исключить") Exclude(doc);
                else Import(doc);
            }
            catch (OperationCanceledException) { Cancel(doc.Editor); }
            catch (InvalidOperationException ex) { doc.Editor.WriteMessage("\nРучной учёт не изменён: " + ex.Message); }
        }

        private static void Import(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            List<ObjectId> ids;
            if (!SelectObjects(ed, "\nВыберите ручные детали для явной регистрации (все выбранные объекты будут показаны в проверке): ", out ids)) return;
            ObjectId zoneId;
            if (!PickZone(ed, "\nВыберите проверенную штриховку ATFZONE для этих деталей: ", out zoneId)) return;
            var observations = new List<ManualQuantityObservation>();
            string zoneName, zoneFingerprint, zoneHandle, documentId;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var carrier = tr.GetObject(zoneId, OpenMode.ForRead) as Entity;
                var zone = FacadeQuantityStore.GetCanonicalZone(tr, doc.Database, carrier, new FacadeQuantityStore.ManualReadContext());
                if (!zone.Ok) throw new InvalidOperationException(zone.Reason);
                zoneName = zone.ZoneId; zoneFingerprint = zone.Fingerprint;
                zoneHandle = zone.HatchId.Handle.ToString();
                documentId = FacadeQuantityStore.ManualDocument(tr, doc.Database);
                var cache = new ManualQuantityGeometry.Cache();
                foreach (var id in ids)
                {
                    CheckCancellation();
                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null) throw new InvalidOperationException("Выбранный объект больше не доступен. Повторите выбор.");
                    var observation = ManualQuantityGeometry.Extract(tr, entity, cache, CheckCancellation);
                    observation.generated_conflict = FacadeQuantityStore.ManualGeneratedConflict(tr, entity);
                    observations.Add(observation);
                }
                tr.Commit();
            }
            ManualQuantityPreview approved;
            using (var form = new ManualQuantityForm(observations, zoneName))
            {
                if (AcApp.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) { Cancel(ed); return; }
                approved = form.ApprovedPreview;
            }
            if (approved == null || approved.accepted_count <= 0) throw new InvalidOperationException("Нет проверенных позиций для регистрации.");
            int registered;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                CheckCancellation();
                var pending = new FacadeQuantityStore.ManualImportPreview { Preview = approved,
                    ZoneFingerprint = zoneFingerprint, ZoneHandle = zoneHandle,
                    RuleFingerprint = ManualQuantitiesCore.RuleFingerprint(approved.rule), DocumentId = documentId };
                // Apply re-extracts every approved snapshot, including rejected objects,
                // and compares the whole inventory before any binding is written.
                registered = FacadeQuantityStore.ApplyImport(tr, doc.Database, pending, CheckCancellation);
                CheckCancellation();
                tr.Commit();
            }
            ed.WriteMessage("\nЗарегистрировано ручных позиций: " + registered + " из " + observations.Count +
                ". Геометрия не изменена. Для совместной ведомости выполните ATFTABLE → " +
                (approved.rule.kind == "frame" ? "Подсистема" : "Облицовка") + ".");
        }

        private static void Exclude(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            string scope;
            if (!Ask(ed, "\nИсключить только из ручного учёта, геометрия сохраняется [Объекты/ВсяЗона] <Объекты>: ",
                "Объекты ВсяЗона", "Объекты", out scope)) return;
            var ids = new List<ObjectId>();
            ObjectId zoneId = ObjectId.Null;
            string kind = null;
            if (scope == "ВсяЗона")
            {
                string choice;
                if (!Ask(ed, "\nОчистить ручную регистрацию во всей зоне [Облицовка/Подсистема] <Облицовка>: ",
                    "Облицовка Подсистема", "Облицовка", out choice)) return;
                kind = choice == "Подсистема" ? "frame" : "cladding";
                if (!PickZone(ed, "\nВыберите штриховку зоны: будет очищен ручной учёт выбранного вида, включая утраченные ссылки: ", out zoneId)) return;
            }
            else if (!SelectObjects(ed, "\nВыберите объекты для снятия ручной регистрации; геометрия сохранится: ", out ids)) return;
            int removed;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                CheckCancellation();
                removed = scope == "ВсяЗона" ? FacadeQuantityStore.ClearManualZone(tr, doc.Database, zoneId, kind) :
                    FacadeQuantityStore.RemoveManualSelected(tr, doc.Database, ids);
                CheckCancellation(); tr.Commit();
            }
            ed.WriteMessage("\nИз ручного учёта исключено позиций: " + removed + ". Геометрия сохранена. Существующие ведомости обновляются отдельно через ATFTABLE.");
        }
        private static bool SelectObjects(Editor ed, string prompt, out List<ObjectId> ids)
        {
            ids = new List<ObjectId>();
            var result = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = prompt });
            if (result.Status != PromptStatus.OK) { Cancel(ed); return false; }
            var seen = new HashSet<ObjectId>();
            foreach (SelectedObject item in result.Value) if (item != null && seen.Add(item.ObjectId)) ids.Add(item.ObjectId);
            return ids.Count > 0;
        }
        private static bool PickZone(Editor ed, string prompt, out ObjectId id)
        {
            var options = new PromptEntityOptions(prompt);
            options.SetRejectMessage("\nНужна штриховка зоны ATFZONE."); options.AddAllowedClass(typeof(Hatch), true);
            var result = ed.GetEntity(options); id = result.ObjectId;
            if (result.Status != PromptStatus.OK) { Cancel(ed); return false; }
            return true;
        }
        private static bool Ask(Editor ed, string prompt, string keywords, string defaultValue, out string value)
        {
            var answer = ed.GetKeywords(new PromptKeywordOptions(prompt, keywords));
            value = answer.Status == PromptStatus.OK && !string.IsNullOrEmpty(answer.StringResult) ? answer.StringResult : defaultValue;
            if (answer.Status == PromptStatus.Cancel) { Cancel(ed); return false; }
            return true;
        }
        private static void CheckCancellation()
        {
            bool stop;
            try { stop = HostApplicationServices.Current.UserBreak(); } catch { stop = false; }
            if (stop) throw new OperationCanceledException();
        }
        private static void Cancel(Editor ed) { ed.WriteMessage("\nОтменено. Ручной учёт и геометрия сохранены."); }
    }
}
