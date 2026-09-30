using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace FacadeSafety
{
    internal static partial class FacadeQuantityStore
    {
        internal const string ManualKey = "AFACADE_MANUAL_QUANTITY";
        internal const string ManualAnchorPrefix = "AFACADE_MANUAL_QUANTITY_INDEX_";
        private const string ManualDirectoryKey = "MANUAL_INDEXES";
        private const string ManualRulePrefix = "MANUAL_RULE_";
        private static readonly string[] ManualGeneratedKeys = { OwnerKey, ElementKey, FrameOwnerKey, FrameElementKey, FrameUnavailableKey,
            "ATFZONE", "ATFZONE_GEOMETRY", "ATTILE", "ATCLAD", "ATFRAME", "ATFRAME_RAIL", "ATLAYOUT_CURRENT", "ATTILE_GEOMETRY", "ATCLAD_GEOMETRY" };

        public sealed class ManualBinding
        {
            public int schema { get; set; }
            public string document_id { get; set; }
            public string owner { get; set; }
            public string kind { get; set; }
            public string zone_handle { get; set; }
            public string zone_id { get; set; }
            public string zone_fingerprint { get; set; }
            public string rule_id { get; set; }
            public string rule_fingerprint { get; set; }
            public string observation_fingerprint { get; set; }
            public string duplicate_key { get; set; }
            public QuantityElement element { get; set; }
        }
        public sealed class ManualIndexMember
        {
            public string handle { get; set; }
            public string element_id { get; set; }
            public string digest { get; set; }
        }
        public sealed class ManualIndex
        {
            public int schema { get; set; }
            public string document_id { get; set; }
            public string owner { get; set; }
            public string zone_id { get; set; }
            public string kind { get; set; }
            public string report_id { get; set; }
            public List<ManualIndexMember> members { get; set; } = new List<ManualIndexMember>();
        }
        public sealed class ManualAnchor
        {
            public int schema { get; set; }
            public string document_id { get; set; }
            public string owner { get; set; }
            public string kind { get; set; }
            public string digest { get; set; }
        }
        public sealed class ManualSavedRule
        {
            public int schema { get; set; }
            public string document_id { get; set; }
            public string fingerprint { get; set; }
            public ManualQuantityRule rule { get; set; }
        }
        internal sealed class ManualImportPreview
        {
            internal ManualQuantityPreview Preview;
            internal string ZoneFingerprint, ZoneHandle, RuleFingerprint, DocumentId;
        }
        internal sealed class ManualZoneResult
        {
            internal bool HasIndex;
            internal QuantityReport Report;
            internal string Digest;
            internal readonly HashSet<ObjectId> VerifiedIds = new HashSet<ObjectId>();
            internal readonly List<string> Warnings = new List<string>();
            internal readonly Dictionary<string, List<string>> DuplicateKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
        internal sealed class ManualReadContext
        {
            internal readonly LayoutGeometryGuard.VerificationContext Validation;
            internal readonly HashSet<ObjectId> UsedZones = new HashSet<ObjectId>();
            internal readonly HashSet<ObjectId> VerifiedIds = new HashSet<ObjectId>();
            internal readonly List<string> Warnings = new List<string>();
            internal readonly Dictionary<string, List<string>> DuplicateKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, ManualZoneResult> Results = new Dictionary<string, ManualZoneResult>();
            internal readonly Dictionary<string, ManualQuantityRule> Rules = new Dictionary<string, ManualQuantityRule>();
            internal readonly Dictionary<string, ManualQuantityEvaluationContext> Evaluators = new Dictionary<string, ManualQuantityEvaluationContext>();
            internal readonly ManualQuantityGeometry.Cache Geometry = new ManualQuantityGeometry.Cache();
            internal bool FingerprintCachePrepared;
            internal ManualReadContext(LayoutGeometryGuard.VerificationContext validationContext = null)
            { Validation = validationContext ?? new LayoutGeometryGuard.VerificationContext(); }
        }
        private sealed class ManualIndexState
        {
            internal ManualIndex Index;
            internal string Digest;
            internal readonly List<ObjectId> Refs = new List<ObjectId>();
            internal readonly HashSet<string> MemberHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        private sealed class ManualPending
        {
            internal Entity Entity;
            internal ManualBinding Binding;
            internal string Json;
        }

        internal static bool HasManual(Transaction tr, Entity e)
        { return e != null && !(e is Table) && Has(tr, e, ManualKey); }
        internal static bool HasManualAnchor(Transaction tr, Entity e, string kind)
        { ManualKind(kind); return Has(tr, e, ManualAnchorPrefix + kind); }
        internal static bool HasAnyManualIndexes(Transaction tr, Database db)
        { var root = Root(tr, db, false); return root != null && root.Contains(ManualDirectoryKey); }
        internal static bool HasManualIndex(Transaction tr, Database db, Hatch hatch, string kind)
        {
            ManualKind(kind);
            if (hatch == null) return false;
            var directory = ManualDirectory(tr, db, false);
            return HasManualAnchor(tr, hatch, kind) || (directory != null && directory.Contains(ManualIndexKey(kind, hatch)));
        }

        internal static ZoneGeometryResult GetCanonicalZone(Transaction tr, Database db, Entity carrier, ManualReadContext context)
        {
            if (carrier == null) throw ManualFail("Зона ручных деталей удалена или отсутствует.");
            if (context == null) throw new ArgumentNullException("context");
            ZoneGeometryResult zone;
            if (!context.Validation.Zones.TryGetValue(carrier.ObjectId, out zone))
            {
                zone = ZoneGeometryGuard.Verify(tr, db, carrier, null);
                context.Validation.Zones[carrier.ObjectId] = zone;
            }
            if (!zone.Ok) throw ManualFail(zone.Reason);
            context.Validation.Zones[zone.HatchId] = zone;
            context.UsedZones.Add(zone.HatchId);
            return zone;
        }

        internal static ObjectId ResolveManualSelection(Transaction tr, Database db, Entity entity, string kind, ManualReadContext context)
        {
            ManualKind(kind);
            if (!HasManual(tr, entity)) return ObjectId.Null;
            var refs = new List<ObjectId>();
            ManualBinding binding = ReadManualBinding(tr, entity, refs);
            if (binding.kind != kind) return ObjectId.Null;
            string document = ManualDocument(tr, db);
            if (binding.owner != entity.Handle.ToString() || binding.document_id != document)
                throw ManualFail("Копия ручной детали ещё не зарегистрирована. Выполните её явный импорт в выбранную зону.");
            if (refs.Count != 1 || refs[0].IsNull || refs[0].IsErased || refs[0].Handle.ToString() != binding.zone_handle)
                throw ManualFail("Потеряна связь ручной детали с зоной. Повторите импорт или снимите её с учёта.");
            var zone = GetCanonicalZone(tr, db, Live(tr, refs[0]), context);
            if (zone.HatchId != refs[0] || zone.ZoneId != binding.zone_id)
                throw ManualFail("Ручная деталь связана с другой зоной.");
            return zone.HatchId;
        }

        internal static ManualImportPreview PreviewImport(Transaction tr, Database db, ObjectId zoneCarrierId,
            ManualQuantityRule rule, IEnumerable<ObjectId> entityIds, Action cancel = null)
        {
            if (cancel != null) cancel();
            if (rule == null) throw ManualFail("Не задано сопоставление ручного образца.");
            ManualKind(rule.kind);
            var context = new ManualReadContext();
            var zone = GetCanonicalZone(tr, db, Live(tr, zoneCarrierId), context);
            var observations = new List<ManualQuantityObservation>();
            var chosen = new HashSet<ObjectId>();
            int operations = 0;
            DefinitionCaches.Remove(tr);
            foreach (ObjectId id in entityIds ?? new ObjectId[0])
            {
                ManualPoll(cancel, ref operations);
                if (!chosen.Add(id)) continue;
                var entity = Live(tr, id);
                if (entity == null) throw ManualFail("Один из выбранных ручных объектов отсутствует.");
                var observation = ManualQuantityGeometry.Extract(tr, entity, context.Geometry, cancel);
                if (ManualGeneratedConflict(tr, entity)) observation.generated_conflict = true;
                observations.Add(observation);
            }
            // A separate rule instance prevents form edits from rewriting the approved snapshot.
            var frozenRule = Serializer().Deserialize<ManualQuantityRule>(Serializer().Serialize(rule));
            if (cancel != null) cancel();
            return new ManualImportPreview {
                Preview = ManualQuantitiesCore.BuildPreview(frozenRule, observations, zone.ZoneId),
                RuleFingerprint = ManualQuantitiesCore.RuleFingerprint(frozenRule), ZoneFingerprint = zone.Fingerprint,
                ZoneHandle = zone.HatchId.Handle.ToString(), DocumentId = ManualDocument(tr, db)
            };
        }

        internal static int ApplyImport(Transaction tr, Database db, ManualImportPreview approved, Action cancel = null)
        {
            if (cancel != null) cancel();
            int operations = 0;
            if (approved == null || approved.Preview == null || approved.Preview.rule == null)
                throw ManualFail("Нет проверенного предпросмотра ручного импорта.");
            var rule = approved.Preview.rule;
            ManualKind(rule.kind);
            if (!SafeId(rule.rule_id) || ManualQuantitiesCore.RuleFingerprint(rule) != approved.RuleFingerprint)
                throw ManualFail("Сопоставление изменено после предпросмотра. Повторите проверку.");
            var context = new ManualReadContext();
            var hatch = Live(tr, Resolve(db, approved.ZoneHandle)) as Hatch;
            var zone = GetCanonicalZone(tr, db, hatch, context);
            string priorDocument = ManualDocument(tr, db);
            if (zone.Fingerprint != approved.ZoneFingerprint || zone.ZoneId != approved.Preview.zone_id || priorDocument != approved.DocumentId)
                throw ManualFail("Зона или чертёж изменились после предпросмотра. Повторите проверку импорта.");
            DefinitionCaches.Remove(tr);
            var observations = new List<ManualQuantityObservation>();
            var selectedEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in approved.Preview.items)
            {
                ManualPoll(cancel, ref operations);
                if (item == null || item.observation == null || string.IsNullOrEmpty(item.handle))
                    throw ManualFail("Повреждён предпросмотр ручного импорта.");
                if (selectedEntities.ContainsKey(item.handle)) continue;
                var entity = Live(tr, Resolve(db, item.handle));
                if (entity == null) throw ManualFail("Объект удалён после предпросмотра. Повторите проверку импорта.");
                var observation = ManualQuantityGeometry.Extract(tr, entity, context.Geometry, cancel);
                if (ManualGeneratedConflict(tr, entity)) observation.generated_conflict = true;
                observations.Add(observation); selectedEntities.Add(item.handle, entity);
            }
            var current = ManualQuantitiesCore.BuildPreview(rule, observations, zone.ZoneId);
            if (cancel != null) cancel();
            if (current.fingerprint != approved.Preview.fingerprint)
                throw ManualFail("Объекты или сопоставление изменились после предпросмотра. Повторите проверку импорта.");
            if (current.accepted_count == 0) throw ManualFail("Нет распознанных ручных деталей для регистрации.");

            // Preflight all reads, mappings, snapshots and immutable rules before the first write.
            string document = string.IsNullOrEmpty(priorDocument) ? Guid.NewGuid().ToString("N") : priorDocument;
            var root = Root(tr, db, false);
            string existingRule = ReadRecord(tr, root, ManualRulePrefix + rule.rule_id, null);
            if (existingRule != null)
            {
                var saved = ParseManual<ManualSavedRule>(existingRule);
                if (saved.schema != 1 || saved.document_id != document || saved.rule == null ||
                    saved.fingerprint != approved.RuleFingerprint || ManualQuantitiesCore.RuleFingerprint(saved.rule) != approved.RuleFingerprint)
                    throw ManualFail("Сопоставление с таким идентификатором уже сохранено с другими данными. Создайте новую версию сопоставления.");
            }
            var state = ReadManualIndex(tr, db, hatch, rule.kind);
            var index = state == null ? new ManualIndex { schema = 1, document_id = document,
                owner = hatch.Handle.ToString(), zone_id = zone.ZoneId, kind = rule.kind, report_id = "manual-" + Guid.NewGuid().ToString("N") } : state.Index;
            if (index.zone_id != zone.ZoneId) throw ManualFail("Марка зоны изменилась. Снимите старый ручной состав с учёта и импортируйте заново.");
            var members = new Dictionary<string, ManualIndexMember>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in index.members) members.Add(member.handle, member);
            var pending = new List<ManualPending>();
            foreach (var item in current.items)
            {
                ManualPoll(cancel, ref operations);
                if (item.status != "accepted") continue;
                Entity entity = selectedEntities[item.handle];
                if (ManualGeneratedConflict(tr, entity)) throw ManualFail("Созданный плагином объект нельзя переоформить как ручную деталь.");
                string elementId = "manual-" + Guid.NewGuid().ToString("N");
                ManualIndexMember member;
                if (HasManual(tr, entity))
                {
                    var bindingRefs = new List<ObjectId>(); var previous = ReadManualBinding(tr, entity, bindingRefs);
                    if (previous.owner == entity.Handle.ToString() && previous.document_id == document)
                    {
                        if (previous.zone_handle != index.owner || previous.kind != rule.kind)
                            throw ManualFail("Деталь уже зарегистрирована в другой зоне или ведомости. Сначала снимите прежнее назначение с учёта.");
                        if (!members.TryGetValue(previous.owner, out member) || member.element_id != previous.element.element_id ||
                            member.digest != Hash(Read(tr, entity, ManualKey)) || bindingRefs.Count != 1 || bindingRefs[0] != hatch.ObjectId)
                            throw ManualFail("Прежнее назначение детали не соответствует индексу зоны. Снимите повреждённое назначение с учёта.");
                        elementId = previous.element.element_id;
                    }
                    // A copied/foreign manual stamp is adopted only by this explicit import, with a new identity.
                }
                else if (members.ContainsKey(entity.Handle.ToString()))
                    throw ManualFail("У зарегистрированной детали потерян паспорт. Снимите повреждённый состав с учёта перед импортом.");
                var element = Serializer().Deserialize<QuantityElement>(Serializer().Serialize(item.element));
                element.element_id = elementId;
                var binding = new ManualBinding { schema = 1, document_id = document, owner = entity.Handle.ToString(),
                    kind = rule.kind, zone_handle = index.owner, zone_id = zone.ZoneId, zone_fingerprint = zone.Fingerprint,
                    rule_id = rule.rule_id, rule_fingerprint = approved.RuleFingerprint,
                    observation_fingerprint = item.observation.fingerprint, duplicate_key = item.duplicate_key, element = element };
                string json = Serializer().Serialize(binding); CheckPayloadSize(Encoding.UTF8.GetByteCount(json));
                members[binding.owner] = new ManualIndexMember { handle = binding.owner, element_id = elementId, digest = Hash(json) };
                pending.Add(new ManualPending { Entity = entity, Binding = binding, Json = json });
            }
            index.members = new List<ManualIndexMember>(members.Values);
            index.members.Sort((a, b) => StringComparer.Ordinal.Compare(a.handle, b.handle));
            // Existing unselected bindings keep their old zone fingerprint and geometry snapshot.
            // A partial reimport therefore cannot make an obsolete remainder current.
            var refs = ManualIndexRefs(db, hatch, index);
            string indexJson = Serializer().Serialize(index); CheckPayloadSize(Encoding.UTF8.GetByteCount(indexJson));
            string savedRuleJson = Serializer().Serialize(new ManualSavedRule { schema = 1, document_id = document,
                fingerprint = approved.RuleFingerprint, rule = rule });
            CheckPayloadSize(Encoding.UTF8.GetByteCount(savedRuleJson));
            if (cancel != null) cancel();
            root = Root(tr, db, true);
            if (string.IsNullOrEmpty(priorDocument)) WriteRecord(tr, root, DocumentKey, document, new List<ObjectId>());
            if (existingRule == null) WriteRecord(tr, root, ManualRulePrefix + rule.rule_id, savedRuleJson, new List<ObjectId>());
            foreach (var write in pending)
            { ManualPoll(cancel, ref operations); WriteManualBinding(tr, write.Entity, write.Json, hatch.ObjectId); }
            if (cancel != null) cancel();
            WriteManualIndex(tr, db, hatch, index, indexJson, refs);
            if (cancel != null) cancel();
            return pending.Count;
        }

        internal static ManualZoneResult ReadManualZone(Transaction tr, Database db, Hatch hatch, string kind, ManualReadContext context)
        {
            ManualKind(kind);
            var zone = GetCanonicalZone(tr, db, hatch, context);
            string key = ManualIndexKey(kind, hatch);
            ManualZoneResult result;
            if (context.Results.TryGetValue(key, out result)) return result;
            result = new ManualZoneResult();
            var state = ReadManualIndex(tr, db, hatch, kind);
            if (state == null) { context.Results.Add(key, result); return result; }
            if (state.Index.zone_id != zone.ZoneId) throw ManualFail("Марка зоны ручных деталей изменилась. Повторите импорт всего её состава.");
            result.HasIndex = true;
            if (!context.FingerprintCachePrepared)
            { DefinitionCaches.Remove(tr); context.FingerprintCachePrepared = true; }
            var report = new QuantityReport { report_id = state.Index.report_id, run_id = state.Index.report_id,
                document_id = state.Index.document_id, kind = kind, scope = "manual_zone_contribution",
                algorithm = "manual-import/1", completeness = "partial", engineering_coverage = "manual_geometry_and_user_mapping_only",
                zone_ids = new List<string> { zone.ZoneId } };
            report.issues.Add(new QuantityIssue { code = "Q_MANUAL_SCOPE_LIMIT", severity = "warning", report_id = report.report_id,
                message = kind == "frame" ?
                    "Ручные позиции учтены по геометрии и явно заданным свойствам; они не включены в оценку хлыстов. Оценки автоматической подсистемы не изменены." :
                    "Ручные позиции учтены по геометрии и явно заданным свойствам; они не включены в групповой раскрой. Раскрой автоматической облицовки не изменён." });
            report.source_revisions["manual_zone"] = zone.Fingerprint;
            report.source_revisions["manual_index"] = state.Digest;
            var digests = new List<string> { state.Digest };
            var manualIssues = new Dictionary<string, QuantityIssue>(StringComparer.Ordinal);
            for (int i = 0; i < state.Index.members.Count; i++)
            {
                var member = state.Index.members[i];
                var entity = Live(tr, state.Refs[i + 1]);
                if (entity == null) throw ManualFail("Зарегистрированная ручная деталь удалена. Снимите прежний состав с учёта или восстановите объект.");
                if (ManualGeneratedConflict(tr, entity)) throw ManualFail("Ручная деталь одновременно имеет метку автоматической раскладки.");
                if (!HasManual(tr, entity)) throw ManualFail("Паспорт ручной детали удалён. Снимите повреждённое назначение с учёта и повторите импорт.");
                var refs = new List<ObjectId>();
                string json = ReadRecord(tr, (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead), ManualKey, refs);
                if (string.IsNullOrEmpty(json) || Hash(json) != member.digest)
                    throw ManualFail("Паспорт ручной детали удалён или изменён. Повторите её проверенный импорт.");
                var binding = ParseManual<ManualBinding>(json);
                ValidateManualBinding(binding);
                if (binding.owner != member.handle || entity.Handle.ToString() != member.handle || binding.document_id != state.Index.document_id ||
                    binding.kind != kind || binding.zone_handle != state.Index.owner || binding.zone_id != zone.ZoneId ||
                    binding.element.element_id != member.element_id || refs.Count != 1 || refs[0] != hatch.ObjectId)
                    throw ManualFail("Связи ручного паспорта и индекса зоны различаются.");
                if (binding.zone_fingerprint != zone.Fingerprint)
                    throw ManualFail("Зона изменилась после ручного импорта. Повторите импорт всего устаревшего состава зоны.");
                var rule = ReadManualRule(tr, db, binding, context);
                string evaluatorKey = binding.rule_id + ":" + binding.rule_fingerprint;
                ManualQuantityEvaluationContext evaluator;
                if (!context.Evaluators.TryGetValue(evaluatorKey, out evaluator))
                { evaluator = ManualQuantitiesCore.Prepare(rule); context.Evaluators.Add(evaluatorKey, evaluator); }
                var observation = ManualQuantityGeometry.Extract(tr, entity, context.Geometry);
                if (observation.fingerprint != binding.observation_fingerprint)
                    throw ManualFail("Геометрия, атрибуты или свойства ручной детали изменились. Повторите её импорт.");
                var evaluation = ManualQuantitiesCore.Evaluate(evaluator, observation, zone.ZoneId);
                if (evaluation.status != "accepted" || evaluation.element == null)
                    throw ManualFail("Ручной образец больше не соответствует сопоставлению: " + evaluation.reason);
                evaluation.element.element_id = member.element_id;
                if (ManualObjectKey(evaluation.element) != ManualObjectKey(binding.element) || evaluation.duplicate_key != binding.duplicate_key)
                    throw ManualFail("Номенклатура или размеры ручной детали не соответствуют сохранённому сопоставлению.");
                report.elements.Add(binding.element);
                foreach (var issue in evaluation.issues)
                {
                    string issueKey = issue.code + ":" + issue.severity + ":" + issue.message;
                    QuantityIssue aggregate;
                    if (!manualIssues.TryGetValue(issueKey, out aggregate))
                    {
                        aggregate = new QuantityIssue { code = issue.code, severity = issue.severity, message = issue.message,
                            report_id = report.report_id };
                        manualIssues.Add(issueKey, aggregate); report.issues.Add(aggregate);
                    }
                    aggregate.element_count++; aggregate.element_ids.Add(member.element_id);
                }
                result.VerifiedIds.Add(entity.ObjectId); context.VerifiedIds.Add(entity.ObjectId);
                digests.Add(member.handle + ":" + member.digest);
                if (!string.IsNullOrEmpty(binding.duplicate_key))
                {
                    List<string> ids;
                    if (!result.DuplicateKeys.TryGetValue(binding.duplicate_key, out ids))
                    { ids = new List<string>(); result.DuplicateKeys.Add(binding.duplicate_key, ids); }
                    ids.Add(member.handle);
                    if (!context.DuplicateKeys.TryGetValue(binding.duplicate_key, out ids))
                    { ids = new List<string>(); context.DuplicateKeys.Add(binding.duplicate_key, ids); }
                    ids.Add(member.handle);
                }
            }
            result.Report = report;
            result.Digest = Hash(Serializer().Serialize(digests));
            context.Results.Add(key, result);
            return result;
        }

        // Called by the existing single ModelSpace scan. Never starts its own scan.
        internal static void VerifyManualOrphanAtScan(Transaction tr, Database db, Entity entity, string kind, ManualReadContext context,
            DBDictionary extension = null)
        {
            if (entity == null || entity is Table || context.VerifiedIds.Contains(entity.ObjectId) || entity.ExtensionDictionary.IsNull) return;
            if (extension == null) extension = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead);
            if (!extension.Contains(ManualKey)) return;
            var refs = new List<ObjectId>();
            var binding = ParseManual<ManualBinding>(ReadRecord(tr, extension, ManualKey, refs));
            ValidateManualBinding(binding);
            if (binding.kind != kind) return;
            ObjectId zone = Resolve(db, binding.zone_handle);
            if (!context.UsedZones.Contains(zone)) return;
            if (binding.owner != entity.Handle.ToString() || binding.document_id != ManualDocument(tr, db))
            {
                string warning = "Не учтена копия ручной детали " + entity.Handle + ": для включения выполните её явный импорт.";
                context.Warnings.Add(warning); return;
            }
            throw ManualFail("У зоны обнаружена ручная деталь вне проверенного индекса. Состав неполон; восстановите назначение или снимите его с учёта.");
        }

        internal static int ClearManualZone(Transaction tr, Database db, ObjectId zoneCarrierId, string kind)
        {
            ManualKind(kind);
            var zone = GetCanonicalZone(tr, db, Live(tr, zoneCarrierId), new ManualReadContext());
            var hatch = (Hatch)tr.GetObject(zone.HatchId, OpenMode.ForRead);
            var directory = ManualDirectory(tr, db, false);
            var refs = new List<ObjectId>();
            ReadRecord(tr, directory, ManualIndexKey(kind, hatch), refs);
            var indexed = new HashSet<ObjectId>(refs);
            // Clearing a zone is an explicit repair operation. One scan also finds
            // orphan bindings when the index itself has been deleted or damaged.
            // Import and bill reads never perform a scan per manual object.
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms) refs.Add(id);
            int count = 0;
            var seen = new HashSet<ObjectId>();
            // Explicit repair: absent/deleted links do not prevent removing the remaining registrations.
            foreach (var id in refs)
            {
                if (!seen.Add(id) || id == hatch.ObjectId) continue;
                var entity = Live(tr, id);
                if (entity == null || !HasManual(tr, entity)) continue;
                ManualBinding binding = null;
                try { binding = ReadManualBinding(tr, entity, new List<ObjectId>()); }
                catch (InvalidOperationException) { /* The authoritative selected-zone refs permit clearing its damaged binding. */ }
                if (binding != null && (binding.zone_handle != hatch.Handle.ToString() || binding.kind != kind)) continue;
                if (binding == null && !indexed.Contains(id)) continue;
                Remove(tr, entity, ManualKey); count++;
            }
            if (directory != null) RemoveManualRecord(tr, directory, ManualIndexKey(kind, hatch));
            Remove(tr, hatch, ManualAnchorPrefix + kind);
            return count;
        }

        internal static int RemoveManualSelected(Transaction tr, Database db, IEnumerable<ObjectId> selectedIds)
        {
            var states = new Dictionary<string, ManualIndexState>();
            var hatches = new Dictionary<string, Hatch>();
            var removals = new Dictionary<string, HashSet<string>>();
            var entities = new List<Entity>(); var seen = new HashSet<ObjectId>();
            string document = ManualDocument(tr, db);
            foreach (var id in selectedIds ?? new ObjectId[0])
            {
                if (!seen.Add(id)) continue;
                var entity = Live(tr, id);
                if (entity == null || !HasManual(tr, entity)) continue;
                var binding = ReadManualBinding(tr, entity, new List<ObjectId>());
                if (binding.owner != entity.Handle.ToString() || binding.document_id != document)
                { entities.Add(entity); continue; } // Removing a copied stamp never removes the original membership.
                var hatch = Live(tr, Resolve(db, binding.zone_handle)) as Hatch;
                string key = binding.kind + "_" + binding.zone_handle;
                ManualIndexState state;
                if (!states.TryGetValue(key, out state))
                {
                    state = hatch == null ? ReadOrphanManualIndex(tr, db, binding.zone_handle, binding.kind) : ReadManualIndex(tr, db, hatch, binding.kind);
                    if (state == null)
                    {
                        if (hatch != null) throw ManualFail("Индекс назначения отсутствует. Очистите учёт зоны перед повторным импортом.");
                        // The user explicitly selected this owned binding; both its zone
                        // and its index are gone. Removing only this stamp repairs the orphan.
                        entities.Add(entity); continue;
                    }
                    states.Add(key, state); hatches.Add(key, hatch); removals.Add(key, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
                if (!state.MemberHandles.Contains(entity.Handle.ToString())) throw ManualFail("Ручная деталь отсутствует в индексе зоны.");
                removals[key].Add(entity.Handle.ToString()); entities.Add(entity);
            }
            // Prepare each zone once; never rewrite a growing index inside the object loop.
            var jsons = new Dictionary<string, string>(); var references = new Dictionary<string, List<ObjectId>>();
            foreach (var pair in states)
            {
                pair.Value.Index.members.RemoveAll(m => removals[pair.Key].Contains(m.handle));
                string json = Serializer().Serialize(pair.Value.Index); CheckPayloadSize(Encoding.UTF8.GetByteCount(json));
                jsons.Add(pair.Key, json);
                if (hatches[pair.Key] != null) references.Add(pair.Key, ManualIndexRefs(db, hatches[pair.Key], pair.Value.Index));
                else
                {
                    // A missing canonical zone is retained as an explicit null link;
                    // it can never be read as a valid zone or silently rebound by handle.
                    var refs = new List<ObjectId> { ObjectId.Null };
                    foreach (var member in pair.Value.Index.members) refs.Add(Resolve(db, member.handle));
                    references.Add(pair.Key, refs);
                }
            }
            foreach (var entity in entities) Remove(tr, entity, ManualKey);
            foreach (var pair in states)
            {
                if (hatches[pair.Key] != null) WriteManualIndex(tr, db, hatches[pair.Key], pair.Value.Index, jsons[pair.Key], references[pair.Key]);
                else
                {
                    var directory = ManualDirectory(tr, db, false);
                    if (pair.Value.Index.members.Count == 0) RemoveManualRecord(tr, directory, pair.Key);
                    else WriteRecord(tr, directory, pair.Key, jsons[pair.Key], references[pair.Key]);
                }
            }
            return entities.Count;
        }

        // Explicit selected-object repair only. Ordinary bill reads require the
        // canonical hatch and its independent anchor; this path never returns a report.
        private static ManualIndexState ReadOrphanManualIndex(Transaction tr, Database db, string zoneHandle, string kind)
        {
            ManualKind(kind);
            var directory = ManualDirectory(tr, db, false);
            string key = kind + "_" + zoneHandle;
            if (directory == null || !directory.Contains(key)) return null;
            var state = new ManualIndexState();
            string json = ReadRecord(tr, directory, key, state.Refs);
            state.Index = ParseManual<ManualIndex>(json); state.Digest = Hash(json);
            var index = state.Index;
            if (index.schema != 1 || index.document_id != ManualDocument(tr, db) || index.owner != zoneHandle ||
                index.kind != kind || index.members == null || !SafeId(index.report_id) || state.Refs.Count != index.members.Count + 1 ||
                (!state.Refs[0].IsNull && state.Refs[0].Handle.ToString() != zoneHandle))
                throw ManualFail("Индекс удалённой зоны повреждён; автоматическое снятие чужих назначений невозможно.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < index.members.Count; i++)
            {
                var member = index.members[i]; var id = state.Refs[i + 1];
                if (member == null || string.IsNullOrEmpty(member.handle) || !SafeId(member.element_id) || string.IsNullOrEmpty(member.digest) ||
                    !state.MemberHandles.Add(member.handle) || !identities.Add(member.element_id) || (!id.IsNull && id.Handle.ToString() != member.handle))
                    throw ManualFail("Состав удалённой зоны повреждён; назначение не снято.");
            }
            return state;
        }

        private static ManualIndexState ReadManualIndex(Transaction tr, Database db, Hatch hatch, string kind)
        {
            var directory = ManualDirectory(tr, db, false); string key = ManualIndexKey(kind, hatch);
            bool saved = directory != null && directory.Contains(key), anchored = HasManualAnchor(tr, hatch, kind);
            if (!saved && !anchored) return null;
            if (!saved || !anchored) throw ManualFail("Индекс ручного состава зоны удалён или потерял связь. Очистите учёт зоны и повторите импорт.");
            var result = new ManualIndexState();
            string json = ReadRecord(tr, directory, key, result.Refs);
            result.Index = ParseManual<ManualIndex>(json); result.Digest = Hash(json);
            var index = result.Index; string document = ManualDocument(tr, db);
            var anchor = ParseManual<ManualAnchor>(Read(tr, hatch, ManualAnchorPrefix + kind));
            if (index.schema != 1 || index.owner != hatch.Handle.ToString() || index.document_id != document || index.kind != kind ||
                string.IsNullOrEmpty(index.zone_id) || !SafeId(index.report_id) || index.members == null ||
                anchor.schema != 1 || anchor.document_id != document || anchor.owner != index.owner || anchor.kind != kind || anchor.digest != result.Digest ||
                result.Refs.Count != index.members.Count + 1 || result.Refs[0] != hatch.ObjectId)
                throw ManualFail("Индекс ручного состава скопирован или повреждён.");
            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < index.members.Count; i++)
            {
                var member = index.members[i]; var id = result.Refs[i + 1];
                if (member == null || string.IsNullOrEmpty(member.handle) || !SafeId(member.element_id) || string.IsNullOrEmpty(member.digest) ||
                    !handles.Add(member.handle) || !ids.Add(member.element_id) || id.IsNull || id.Handle.ToString() != member.handle)
                    throw ManualFail("Неполные или повторные ссылки индекса ручных деталей.");
                result.MemberHandles.Add(member.handle);
            }
            return result;
        }

        private static ManualQuantityRule ReadManualRule(Transaction tr, Database db, ManualBinding binding, ManualReadContext context)
        {
            string key = binding.rule_id + ":" + binding.rule_fingerprint;
            ManualQuantityRule rule;
            if (context.Rules.TryGetValue(key, out rule)) return rule;
            var saved = ParseManual<ManualSavedRule>(ReadRecord(tr, Root(tr, db, false), ManualRulePrefix + binding.rule_id, null));
            if (saved.schema != 1 || saved.document_id != binding.document_id || saved.rule == null || saved.rule.rule_id != binding.rule_id ||
                saved.fingerprint != binding.rule_fingerprint || ManualQuantitiesCore.RuleFingerprint(saved.rule) != binding.rule_fingerprint || saved.rule.kind != binding.kind)
                throw ManualFail("Сохранённое сопоставление ручного образца изменено или отсутствует.");
            context.Rules.Add(key, saved.rule); return saved.rule;
        }
        private static ManualBinding ReadManualBinding(Transaction tr, Entity entity, List<ObjectId> refs)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) throw ManualFail("Паспорт ручной детали отсутствует.");
            var binding = ParseManual<ManualBinding>(ReadRecord(tr, (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead), ManualKey, refs));
            ValidateManualBinding(binding); return binding;
        }
        private static void ValidateManualBinding(ManualBinding b)
        {
            if (b.schema != 1 || string.IsNullOrEmpty(b.document_id) || string.IsNullOrEmpty(b.owner) || string.IsNullOrEmpty(b.zone_id) ||
                string.IsNullOrEmpty(b.zone_handle) || string.IsNullOrEmpty(b.zone_fingerprint) || !SafeId(b.rule_id) ||
                string.IsNullOrEmpty(b.rule_fingerprint) || string.IsNullOrEmpty(b.observation_fingerprint) ||
                b.element == null || !SafeId(b.element.element_id) || (b.kind != "cladding" && b.kind != "frame"))
                throw ManualFail("Паспорт ручной детали повреждён.");
        }
        private static List<ObjectId> ManualIndexRefs(Database db, Hatch hatch, ManualIndex index)
        {
            var refs = new List<ObjectId> { hatch.ObjectId };
            foreach (var member in index.members)
            {
                ObjectId id = Resolve(db, member.handle);
                if (id.IsNull) throw ManualFail("Ссылка на зарегистрированную ручную деталь потеряна. Очистите прежний учёт зоны.");
                refs.Add(id);
            }
            return refs;
        }
        private static void WriteManualIndex(Transaction tr, Database db, Hatch hatch, ManualIndex index, string json, List<ObjectId> refs)
        {
            WriteRecord(tr, ManualDirectory(tr, db, true), ManualIndexKey(index.kind, hatch), json, refs);
            Write(tr, hatch, ManualAnchorPrefix + index.kind, Serializer().Serialize(new ManualAnchor {
                schema = 1, document_id = index.document_id, owner = index.owner, kind = index.kind, digest = Hash(json) }));
        }
        private static void WriteManualBinding(Transaction tr, Entity entity, string json, ObjectId hatchId)
        {
            if (!entity.IsWriteEnabled) entity.UpgradeOpen();
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            WriteRecord(tr, (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite), ManualKey, json, new List<ObjectId> { hatchId });
        }
        private static DBDictionary ManualDirectory(Transaction tr, Database db, bool create)
        {
            var root = Root(tr, db, create); if (root == null) return null;
            if (root.Contains(ManualDirectoryKey)) return (DBDictionary)tr.GetObject(root.GetAt(ManualDirectoryKey), create ? OpenMode.ForWrite : OpenMode.ForRead);
            if (!create) return null;
            var directory = new DBDictionary(); root.SetAt(ManualDirectoryKey, directory); tr.AddNewlyCreatedDBObject(directory, true); return directory;
        }
        internal static string ManualDocument(Transaction tr, Database db)
        { return ReadRecord(tr, Root(tr, db, false), DocumentKey, null) ?? ""; }
        private static string ManualIndexKey(string kind, Hatch hatch)
        { return kind + "_" + hatch.Handle.ToString(); }
        private static void ManualKind(string kind)
        { if (kind != "cladding" && kind != "frame") throw ManualFail("Неизвестный вид ручной ведомости."); }
        internal static bool ManualGeneratedConflict(Transaction tr, Entity entity)
        {
            if (entity is Table) return true;
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            var extension = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead);
            foreach (string key in ManualGeneratedKeys) if (extension.Contains(key)) return true;
            return false;
        }
        private static T ParseManual<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) throw ManualFail("Сохранённые данные ручного учёта отсутствуют.");
            CheckPayloadSize(Encoding.UTF8.GetByteCount(json));
            try { var value = Serializer().Deserialize<T>(json); if (value != null) return value; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            throw ManualFail("Повреждены данные ручного учёта. Очистите назначение и повторите импорт.");
        }
        private static void RemoveManualRecord(Transaction tr, DBDictionary directory, string key)
        {
            if (!directory.Contains(key)) return;
            if (!directory.IsWriteEnabled) directory.UpgradeOpen();
            ObjectId id = directory.GetAt(key); directory.Remove(key); tr.GetObject(id, OpenMode.ForWrite).Erase();
        }
        private static InvalidOperationException ManualFail(string message) { return new InvalidOperationException(message); }
        private static void ManualPoll(Action cancel, ref int operations)
        { if ((operations++ & 127) == 0 && cancel != null) cancel(); }
        private static string ManualObjectKey(object value)
        { return Hash(ManualCanonical(Serializer().DeserializeObject(Serializer().Serialize(value)))); }
        private static string ManualCanonical(object value)
        {
            if (value == null) return "null";
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var keys = new List<string>(); foreach (object key in dictionary.Keys) keys.Add(Convert.ToString(key, CultureInfo.InvariantCulture));
                keys.Sort(StringComparer.Ordinal); var parts = new List<string>();
                foreach (string key in keys) parts.Add(Serializer().Serialize(key) + ":" + ManualCanonical(dictionary[key]));
                return "{" + string.Join(",", parts.ToArray()) + "}";
            }
            if (value is string || value is bool) return Serializer().Serialize(value);
            var sequence = value as IEnumerable;
            if (sequence != null) { var parts = new List<string>(); foreach (object item in sequence) parts.Add(ManualCanonical(item)); return "[" + string.Join(",", parts.ToArray()) + "]"; }
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsInfinity(number) || double.IsNaN(number)) throw ManualFail("Неконечное число в ручном паспорте.");
            return (number == 0 ? 0 : number).ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
