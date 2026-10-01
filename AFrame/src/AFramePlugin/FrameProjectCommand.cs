using System;
using System.Collections.Generic;
using FacadeSafety;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using WinForms = System.Windows.Forms;

[assembly: CommandClass(typeof(AFramePlugin.FrameProjectCommand))]

namespace AFramePlugin
{
    internal sealed class FrameProjectOperation
    {
        private sealed class Source
        {
            internal ObjectId Id;
            internal string Root, Fingerprint;
            internal FacadeProjectParameterStore.Record Record;
            internal FrameParameterResolution Resolution;
            internal FrameSettings Previous;
        }
        private readonly Dictionary<ObjectId, Source> sources = new Dictionary<ObjectId, Source>();
        private readonly FacadeProjectParameterStore.ReadContext initialReads = new FacadeProjectParameterStore.ReadContext();
        private readonly Dictionary<ObjectId, string> savedSettings = new Dictionary<ObjectId, string>();
        private readonly Dictionary<ObjectId, FrameSettings> savedParameterSettings = new Dictionary<ObjectId, FrameSettings>();
        private readonly Dictionary<ObjectId, ObjectId> aliasOwners = new Dictionary<ObjectId, ObjectId>();
        private readonly Dictionary<ObjectId, string> savedRoots = new Dictionary<ObjectId, string>();
        private readonly System.Web.Script.Serialization.JavaScriptSerializer serializer =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private FrameProjectParameters project;
        internal bool IsBound { get; private set; }
        internal FacadeProjectParameterStore.ProjectDependency Dependency {
            get { return IsBound ? FacadeProjectParameterStore.Dependency(initialReads.Project) : null; }
        }
        internal void Observe(Transaction tr, Database db, Hatch hatch, string fingerprint, string root,
            Dictionary<string, object> previous)
        {
            Source existing;
            if (sources.TryGetValue(hatch.ObjectId, out existing))
            {
                if (existing.Root != root || existing.Fingerprint != fingerprint)
                    throw new FrameSolutionSelectionException("Одна штриховка относится к разным исходным зонам.");
                return;
            }
            var record = FacadeProjectParameterStore.ReadZone(tr, hatch, fingerprint);
            var source = new Source { Id = hatch.ObjectId, Root = root, Fingerprint = fingerprint,
                Record = record, Previous = previous == null ? null : FrameSettings.FromDict(previous) };
            if (record.Present)
            {
                if (project == null)
                {
                    var storedProject = FacadeProjectParameterStore.ReadProject(tr, db, initialReads);
                    if (!storedProject.Present) throw new FrameSolutionSelectionException("Зона связана с отсутствующим проектом. Выполните ATFPROJECT; привязка не заменяется локальным выбором.");
                    project = FrameProjectParameters.FromDict(storedProject.Payload);
                }
                var zone = FrameZoneParameters.FromDict(record.Payload);
                source.Resolution = FrameParameterResolver.Resolve(project, initialReads.Project.RecordDigest,
                    zone, record.RecordDigest, hatch.Handle.ToString(), root);
                IsBound = true;
            }
            sources.Add(hatch.ObjectId, source);
        }
        internal void CaptureSavedSettings(IDictionary<ObjectId, Dictionary<string, object>> observed,
            IDictionary<ObjectId, ObjectId> canonicalOwners = null,
            IDictionary<ObjectId, Tuple<string, bool>> ownerRoots = null)
        {
            foreach (var item in observed)
            {
                savedSettings[item.Key] = serializer.Serialize(item.Value);
                savedParameterSettings[item.Key] = item.Value == null ? null : FrameSettings.FromDict(item.Value);
            }
            if (canonicalOwners != null) foreach (var pair in canonicalOwners) aliasOwners[pair.Key] = pair.Value;
            if (ownerRoots != null) foreach (var pair in ownerRoots) savedRoots[pair.Key] = pair.Value.Item1;
        }
        internal void ValidateGroup(IEnumerable<string> actualRoots = null)
        {
            HashSet<string> actual = actualRoots == null ? null : new HashSet<string>(actualRoots, StringComparer.Ordinal);
            var bound = new Dictionary<string, FrameParameterResolution>(StringComparer.Ordinal);
            var unbound = new HashSet<string>(StringComparer.Ordinal);
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources.Values)
            {
                if (actual != null && !actual.Contains(source.Root)) continue;
                covered.Add(source.Root);
                if (source.Resolution == null) unbound.Add(source.Root);
                else bound.Add(source.Root + " [" + source.Id.Handle.ToString() + "]", source.Resolution);
            }
            // Raw openings disappear from engine per_zone; only actual outer
            // roots participate in the bound/unbound rule after computation.
            if (actual != null) foreach (string root in actual) if (!covered.Contains(root)) unbound.Add(root);
            if (actual != null && (actual.Count == 0 || (IsBound && bound.Count == 0)))
                throw new FrameSolutionSelectionException("Движок не подтвердил выбранные привязанные зоны. Параметры проекта не переносятся на другую область.");
            string reason = FrameParameterResolver.ValidateGroup(bound, unbound);
            if (reason != null) throw new FrameSolutionSelectionException(reason);
        }
        internal List<FrameParameterContext> Contexts(IEnumerable<string> actualRoots = null)
        {
            var actual = actualRoots == null ? null : new HashSet<string>(actualRoots, StringComparer.Ordinal);
            var result = new List<FrameParameterContext>();
            foreach (var source in sources.Values)
                if (source.Resolution != null && (actual == null || actual.Contains(source.Root)))
                    result.Add(source.Resolution.Context);
            return result;
        }
        internal void Apply(FrameSettings settings)
        {
            if (!IsBound) { settings.DetachProjectParameters(); return; }
            foreach (var source in sources.Values) if (source.Resolution != null)
            { settings.ApplyProjectParameters(source.Resolution); return; }
        }
        internal void ValidateSelectedSettings(FrameSettings settings)
        {
            if (!IsBound)
            {
                if (settings.ProjectParametersContext != null)
                    throw new FrameSolutionSelectionException("У непривязанной зоны не может сохраняться унаследованное заявление проекта.");
                return;
            }
            foreach (var source in sources.Values) if (source.Resolution != null)
            {
                if (!FrameSolutionSelection.Same(settings.SolutionSelection, source.Resolution.Selection))
                    throw new FrameSolutionSelectionException("Параметры связанной зоны меняются через ATFPROJECT или ATFZONEPARAMS.");
                break;
            }
        }
        internal void ValidateClamps(IEnumerable<string> actualRoots)
        {
            var actual = new HashSet<string>(actualRoots, StringComparer.Ordinal);
            // A marker can retain the old statement even when the canonical
            // hatch has lost its ATFRAME metadata. Never erase that evidence
            // while retaining the physical frame in a clamps-only operation.
            foreach (var pair in savedParameterSettings)
            {
                var previous = pair.Value; if (previous == null) continue;
                string root;
                if (savedRoots.TryGetValue(pair.Key, out root) && !actual.Contains(root)) continue;
                ObjectId canonical; Source source;
                if (!aliasOwners.TryGetValue(pair.Key, out canonical)) canonical = pair.Key;
                if (sources.TryGetValue(canonical, out source) && source.Resolution != null)
                {
                    if (!FrameSolutionSelection.Same(previous.SolutionSelection, source.Resolution.Selection) ||
                        !FrameParameterContext.Same(previous.ProjectParametersContext, source.Resolution.Context))
                        throw new FrameSolutionSelectionException("Метка зоны «" + source.Root +
                            "» содержит другое происхождение прежнего каркаса. Для изменения параметров требуется полное ATFRAME.");
                }
                else if (previous.ProjectParametersContext != null)
                    throw new FrameSolutionSelectionException("На исходной метке сохранён прежний результат проекта без действующей привязки. Режим «только кляммеры» не снимает его происхождение; выполните полное ATFRAME.");
            }
            foreach (var source in sources.Values)
            {
                if (!actual.Contains(source.Root)) continue;
                if (source.Resolution == null)
                {
                    if (source.Previous != null && source.Previous.ProjectParametersContext != null)
                        throw new FrameSolutionSelectionException("Зона «" + source.Root +
                            "»: привязка к проекту снята. Режим «только кляммеры» не меняет происхождение прежнего каркаса; выполните полное ATFRAME.");
                    continue;
                }
                if (source.Previous == null ||
                    !FrameSolutionSelection.Same(source.Previous.SolutionSelection, source.Resolution.Selection) ||
                    !FrameParameterContext.Same(source.Previous.ProjectParametersContext, source.Resolution.Context))
                    throw new FrameSolutionSelectionException("Зона «" + source.Root +
                        "»: параметры проекта или переопределения изменены либо ещё не применены. Режим «только кляммеры» требует полного построения ATFRAME.");
            }
        }
        internal void VerifyFresh(Transaction tr, Database db, FacadeProjectParameterStore.ReadContext reads)
        {
            FacadeProjectParameterStore.VerifyProject(tr, db, Dependency, reads);
            foreach (var source in sources.Values)
            {
                var hatch = source.Id.IsErased ? null : tr.GetObject(source.Id, OpenMode.ForRead) as Hatch;
                var current = FacadeProjectParameterStore.ReadZone(tr, hatch, source.Fingerprint, reads);
                FacadeProjectParameterStore.Compare(source.Record, current,
                    "Параметры зоны «" + source.Root + "» изменились после открытия окна. Повторите ATFRAME.");
                if (source.Resolution != null)
                {
                    // The binding's saved fingerprint is not a fresh geometry
                    // observation. Check the bound pilot before any drawing
                    // mutation, even when the quantity report is unavailable.
                    var geometry = ZoneGeometryGuard.Verify(tr, db, hatch, null);
                    if (!geometry.Ok || geometry.HatchId != source.Id || geometry.Fingerprint != source.Fingerprint)
                        throw new FrameSolutionSelectionException("Геометрия связанной зоны «" + source.Root +
                            "» изменилась после открытия окна. Повторите ATFRAME; прежний каркас сохранён.");
                }
            }
        }
        internal void VerifySavedSettings(ObjectId id, Dictionary<string, object> current)
        {
            string prior;
            if (!savedSettings.TryGetValue(id, out prior) || prior != serializer.Serialize(current))
                throw new FrameSolutionSelectionException("Параметры прежнего каркаса изменились после открытия окна. Повторите ATFRAME.");
        }
        internal Dictionary<string, object> SettingsForOwner(FrameSettings settings, ObjectId owner,
            IDictionary<ObjectId, ObjectId> canonicalOwners)
        {
            if (!IsBound) return settings.ToDict();
            ObjectId canonical; Source source;
            if (!canonicalOwners.TryGetValue(owner, out canonical) || !sources.TryGetValue(canonical, out source) || source.Resolution == null)
                throw new FrameSolutionSelectionException("Нет однозначного происхождения параметров владельца подсистемы.");
            var local = settings.Clone(); local.ApplyProjectParameters(source.Resolution); return local.ToDict();
        }
    }

    // Editing intent is separate from generating a frame. The forms only edit
    // detached DTOs; all DWG writes happen after their final change preview.
    public sealed class FrameProjectCommand
    {
        [CommandMethod("ATFPROJECT", CommandFlags.Modal)]
        public void Project()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                FacadeProjectParameterStore.Record snapshot; FrameProjectParameters previous;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    snapshot = FacadeProjectParameterStore.ReadProject(tr, doc.Database);
                    previous = snapshot.Present ? FrameProjectParameters.FromDict(snapshot.Payload) : null;
                    tr.Commit();
                }
                FrameProjectParameters result;
                using (var form = new FrameProjectForm(previous))
                {
                    if (AcApp.ShowModalDialog(form) != WinForms.DialogResult.OK) return;
                    result = form.Result;
                }
                if (result == null) throw new FrameSolutionSelectionException("Нет подтверждённых параметров проекта.");
                // Rebuild the revision from the observed baseline, not from a UI counter.
                result = FrameProjectParameters.CreateNext(previous, result.defaults, result.project_id);
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    FacadeProjectParameterStore.WriteProject(tr, doc.Database, snapshot, result.ToDict());
                    tr.Commit();
                }
                doc.Editor.WriteMessage("\nПараметры проекта сохранены, редакция " + result.revision +
                    ". Зависимые результаты проверяются при использовании; для их обновления выполните полное ATFRAME.");
            }
            catch (OperationCanceledException) { doc.Editor.WriteMessage("\nATFPROJECT отменено. Параметры сохранены без изменений."); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nATFPROJECT: " + ex.Message + "\nИзменения не записаны."); }
        }

        private sealed class ZoneEdit
        {
            internal ObjectId Id;
            internal string Name, Fingerprint;
            internal FacadeProjectParameterStore.Record Record;
            internal FrameZoneParameters Parameters;
        }

        [CommandMethod("ATFZONEPARAMS", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ZoneParameters()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                var chosen = doc.Editor.GetSelection(new PromptSelectionOptions {
                    MessageForAdding = "\nВыберите штриховку или марку зоны ATFZONE (либо группу с одинаковыми переопределениями): "
                }, new SelectionFilter(new[] {
                    new TypedValue((int)DxfCode.Operator, "<or"), new TypedValue((int)DxfCode.Start, "HATCH"),
                    new TypedValue((int)DxfCode.Start, "MTEXT"), new TypedValue((int)DxfCode.Operator, "or>")
                }));
                if (chosen.Status != PromptStatus.OK) return;
                var zones = new Dictionary<ObjectId, ZoneEdit>();
                FacadeProjectParameterStore.Record projectRecord; FrameProjectParameters project;
                FrameZoneParameters baseline = null; bool first = true; bool generationChanged = false;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    projectRecord = FacadeProjectParameterStore.ReadProject(tr, doc.Database);
                    if (!projectRecord.Present) throw new FrameSolutionSelectionException("Сначала явно задайте параметры проекта командой ATFPROJECT.");
                    project = FrameProjectParameters.FromDict(projectRecord.Payload);
                    var verified = new Dictionary<ObjectId, ZoneGeometryResult>();
                    foreach (SelectedObject item in chosen.Value)
                    {
                        ZoneGeometryResult zone;
                        if (!verified.TryGetValue(item.ObjectId, out zone))
                        {
                            var carrier = tr.GetObject(item.ObjectId, OpenMode.ForRead) as Entity;
                            zone = ZoneGeometryGuard.Verify(tr, doc.Database, carrier, null);
                            if (!zone.Ok) throw new FrameSolutionSelectionException(zone.Reason);
                            verified[item.ObjectId] = zone; verified[zone.HatchId] = zone;
                        }
                        if (zones.ContainsKey(zone.HatchId)) continue;
                        var hatch = tr.GetObject(zone.HatchId, OpenMode.ForRead) as Hatch;
                        // The explicit editor may rebind a new verified generation;
                        // it never conceals an old record as an absent binding.
                        var record = FacadeProjectParameterStore.ReadZone(tr, hatch);
                        var parameters = record.Present ? FrameZoneParameters.FromDict(record.Payload) : null;
                        if (parameters != null && parameters.project_id != project.project_id)
                            throw new FrameSolutionSelectionException("Зона «" + zone.ZoneId + "» связана с другим проектом. Автоматическая перепривязка запрещена.");
                        if (first) { baseline = parameters; first = false; }
                        else if ((baseline == null) != (parameters == null) ||
                            (baseline != null && baseline.content_digest != parameters.content_digest))
                            throw new FrameSolutionSelectionException("У выбранных зон разные намерения или переопределения. Отредактируйте их отдельными группами.");
                        if (record.Present && record.ZoneFingerprint != zone.Fingerprint) generationChanged = true;
                        zones.Add(zone.HatchId, new ZoneEdit { Id = zone.HatchId, Name = zone.ZoneId,
                            Fingerprint = zone.Fingerprint, Record = record, Parameters = parameters });
                    }
                    tr.Commit();
                }
                if (zones.Count == 0) return;
                string caption = zones.Count == 1 ? FirstZoneName(zones) : "Зон: " + zones.Count;
                if (generationChanged) caption += "; будет подтверждено новое поколение геометрии";
                FrameZoneParameters result;
                using (var form = new FrameZoneParametersForm(project, baseline, caption, zones.Count, generationChanged))
                {
                    if (AcApp.ShowModalDialog(form) != WinForms.DialogResult.OK) return;
                    result = form.Result; // null is explicit detach, never field clear.
                }
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    FacadeProjectParameterStore.VerifyProject(tr, doc.Database,
                        FacadeProjectParameterStore.Dependency(projectRecord), new FacadeProjectParameterStore.ReadContext());
                    foreach (var edit in zones.Values)
                    {
                        var hatch = tr.GetObject(edit.Id, OpenMode.ForRead) as Hatch;
                        var now = ZoneGeometryGuard.Verify(tr, doc.Database, hatch, null);
                        if (!now.Ok || now.HatchId != edit.Id || now.Fingerprint != edit.Fingerprint)
                            throw new FrameSolutionSelectionException("Зона «" + edit.Name + "» изменилась после открытия окна. Повторите команду.");
                        if (result == null)
                            FacadeProjectParameterStore.ClearZone(tr, hatch, now.Fingerprint, edit.Record);
                        else
                        {
                            var next = FrameZoneParameters.CreateNext(edit.Parameters, project, result.overrides);
                            // Validate the complete inherited selection before any commit.
                            FrameParameterResolver.Resolve(project, projectRecord.RecordDigest, next,
                                new string('0', 64), hatch.Handle.ToString(), edit.Name);
                            FacadeProjectParameterStore.WriteZone(tr, hatch, now.Fingerprint, edit.Record, next.ToDict());
                        }
                    }
                    tr.Commit();
                }
                doc.Editor.WriteMessage(result == null ?
                    "\nПривязка к проекту снята. Прежний результат устарел; его параметры проекта не становятся локальным решением. Выполните полное ATFRAME." :
                    "\nПараметры зон сохранены. Геометрия не перестроена; выполните полное ATFRAME для обновления зависимого результата.");
            }
            catch (OperationCanceledException) { doc.Editor.WriteMessage("\nATFZONEPARAMS отменено. Параметры сохранены без изменений."); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nATFZONEPARAMS: " + ex.Message + "\nИзменения не записаны."); }
        }
        private static string FirstZoneName(Dictionary<ObjectId, ZoneEdit> zones)
        { foreach (var zone in zones.Values) return zone.Name; return "Зона"; }
    }
}
