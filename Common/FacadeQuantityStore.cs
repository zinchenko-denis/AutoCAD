using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FacadeSafety
{
    internal sealed class QuantityUnaccountedItem
    {
        public string handle { get; set; }
        public string type { get; set; }
        public string layer { get; set; }
        public string reason { get; set; }
    }

    internal sealed class QuantitySelection
    {
        internal bool Ok;
        internal string Reason;
        internal QuantityResult Rows;
        internal string Fingerprint;
        internal double VerificationMilliseconds;
        internal double AggregationMilliseconds;
        internal readonly List<QuantityUnaccountedItem> Unaccounted = new List<QuantityUnaccountedItem>();
        internal readonly List<QuantityReport> Reports = new List<QuantityReport>();
        internal readonly List<string> SelectedZoneIds = new List<string>();
        internal readonly List<string> Warnings = new List<string>();
    }

    // One compressed, versioned ledger per complete generation in the DWG NOD.
    // Carrier/element stamps bind it to live objects. Handles are checked against
    // soft references; COPY is not silently adopted as a second physical part.
    internal static partial class FacadeQuantityStore
    {
        internal const string OwnerKey = "AFACADE_QUANTITY_OWNER";
        internal const string ElementKey = "AFACADE_QUANTITY_ELEMENT";
        private const string RootKey = "AFACADE_QUANTITIES";
        private const string DocumentKey = "DOCUMENT_ID";
        private const string Prefix = "RUN_";
        private const long MaxPayloadBytes = 256L * 1024 * 1024;
        private static readonly ConditionalWeakTable<Transaction, Dictionary<ObjectId, string>> DefinitionCaches =
            new ConditionalWeakTable<Transaction, Dictionary<ObjectId, string>>();

        public sealed class Owner
        {
            public string handle { get; set; }
            public List<string> zone_ids { get; set; }
            public string revision { get; set; }
            public string fingerprint { get; set; }
            public string layout_digest { get; set; }
        }
        public sealed class Entry
        {
            public int schema { get; set; }
            public string document_id { get; set; }
            public string run_id { get; set; }
            public string layout_key { get; set; }
            public string report_json { get; set; }
            public QuantityReport quantity_report { get; set; }
            public List<Owner> owners { get; set; }
            public List<string> entity_handles { get; set; }
            public FrameSources frame_sources { get; set; }
        }
        private sealed class PendingQuantityRead
        {
            internal Entry Entry;
            internal QuantityReport Report;
            internal string Digest;
            internal List<ObjectId> Refs;
        }
        public sealed class Stamp
        {
            public int schema { get; set; }
            public string owner { get; set; }
            public string document_id { get; set; }
            public string run_id { get; set; }
            public string layout_key { get; set; }
            public string entry_digest { get; set; }
            public string element_id { get; set; }
            public string role { get; set; }
            public List<string> zone_ids { get; set; }
        }

        internal static bool IsCandidate(Transaction tr, Entity e)
        {
            return e != null && !(e is Table) &&
                (Has(tr, e, OwnerKey) || Has(tr, e, ElementKey) ||
                 Has(tr, e, "ATFZONE") || Has(tr, e, "ATCLAD") || Has(tr, e, "ATTILE"));
        }

        internal static QuantityCadEntity CaptureEntity(Transaction tr, Entity e, string role)
        {
            if (e == null || e.ObjectId.IsNull || e.IsErased || string.IsNullOrEmpty(role))
                throw new InvalidOperationException("Не удалось связать деталь облицовки с объектом чертежа.");
            return new QuantityCadEntity { handle = e.Handle.ToString(), role = role,
                fingerprint = Fingerprint(tr, e, DefinitionCaches.GetOrCreateValue(tr), new HashSet<ObjectId>()) };
        }

        internal static void Store(Transaction tr, Database db, IEnumerable<Entity> carriers,
            string layoutKey, QuantityReport report)
        {
            StoreCore(tr, db, carriers, layoutKey, report, null);
        }

        private static void StoreCore(Transaction tr, Database db, IEnumerable<Entity> carriers,
            string layoutKey, QuantityReport report, FrameSources frameSources)
        {
            bool frame = layoutKey == "ATFRAME";
            string ownerKey = frame ? FrameOwnerKey : OwnerKey, elementKey = frame ? FrameElementKey : ElementKey;
            var validationContext = new LayoutGeometryGuard.VerificationContext();
            if (frame) { DefinitionCaches.Remove(tr); VerifyFrameSources(tr, db, frameSources); }
            if (!frame && layoutKey != "ATTILE" && layoutKey != "ATCLAD")
                throw new InvalidOperationException("Неизвестный источник ведомости облицовки.");
            if (report == null || !SafeId(report.run_id) || !SafeId(report.report_id))
                throw new InvalidOperationException("Нет идентификатора результата облицовки.");
            if (report.kind != (frame ? "frame" : "cladding"))
                throw new InvalidOperationException("Вид паспорта не соответствует источнику ведомости.");
            var root = Root(tr, db, true);
            string documentId = ReadRecord(tr, root, DocumentKey, null);
            if (string.IsNullOrEmpty(documentId))
            {
                documentId = Guid.NewGuid().ToString("N");
                WriteRecord(tr, root, DocumentKey, documentId, new List<ObjectId>());
            }
            report.document_id = documentId;
            var owners = new List<Owner>();
            var refs = new List<ObjectId>();
            var ownerIds = new HashSet<ObjectId>();
            var oldRuns = new HashSet<string>();
            foreach (Entity e in carriers ?? new Entity[0])
            {
                if (e == null || !ownerIds.Add(e.ObjectId)) continue;
                var current = frame ? FrameOwnerState(tr, e) : LayoutGeometryGuard.Verify(tr, db, e, layoutKey, validationContext);
                if (!current.Ok) throw new InvalidOperationException(current.Reason);
                string handle = e.Handle.ToString();
                string zone;
                if (report.source_revisions == null || !report.source_revisions.TryGetValue("owner_zone:" + handle, out zone) ||
                    string.IsNullOrEmpty(zone) || !report.zone_ids.Contains(zone))
                    throw new InvalidOperationException("Нет однозначной связи владельца раскладки с зоной ведомости.");
                var previous = ReadStamp(tr, e, ownerKey);
                if (previous != null && SafeId(previous.run_id)) oldRuns.Add(previous.run_id);
                owners.Add(new Owner { handle = handle, zone_ids = new List<string> { zone },
                    revision = current.Revision, fingerprint = current.Fingerprint, layout_digest = current.LayoutDigest });
                refs.Add(e.ObjectId);
            }
            if (owners.Count == 0) throw new InvalidOperationException("Нет владельцев результата облицовки.");
            var covered = new HashSet<string>();
            foreach (Owner o in owners) foreach (string z in o.zone_ids) covered.Add(z);
            foreach (string z in report.zone_ids)
                if (!covered.Contains(z)) throw new InvalidOperationException("Неполная группа владельцев ведомости: " + z);

            var check = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, false);
            if (!check.ok) throw new InvalidOperationException(FirstIssue(check));
            report.completeness = check.completeness;
            var handles = new List<string>();
            var entityIds = new HashSet<ObjectId>();
            DefinitionCaches.Remove(tr);
            var cache = DefinitionCaches.GetOrCreateValue(tr);
            foreach (QuantityElement element in report.elements)
                foreach (QuantityCadEntity link in element.cad_entities)
                {
                    ObjectId id = Resolve(db, link.handle);
                    var entity = Live(tr, id);
                    if (entity == null || !entityIds.Add(id) || ownerIds.Contains(id))
                        throw new InvalidOperationException("Физические детали содержат отсутствующую или повторную DWG-ссылку.");
                    string now = Fingerprint(tr, entity, cache, new HashSet<ObjectId>());
                    if (now != link.fingerprint)
                        throw new InvalidOperationException("Деталь изменилась во время построения; ведомость не сохранена.");
                    handles.Add(link.handle);
                    refs.Add(id);
                }
            // Entry v2 stores a typed report once: embedding its escaped JSON string would
            // serialize/parse the largest payload twice. Published cladding v1 stays readable.
            var entry = new Entry { schema = 2, document_id = documentId, run_id = report.run_id,
                layout_key = layoutKey, quantity_report = report,
                owners = owners, entity_handles = handles, frame_sources = frameSources };
            if (frame) foreach (var source in frameSources.sources) refs.Add(Resolve(db, source.handle));
            string json = Serializer().Serialize(entry), digest = Hash(json);
            if (root.Contains(Prefix + report.run_id))
                throw new InvalidOperationException("Идентификатор нового построения уже используется.");
            WriteRecord(tr, root, Prefix + report.run_id, Compress(json), refs);
            foreach (Owner owner in owners)
            {
                var e = Live(tr, Resolve(db, owner.handle));
                Write(tr, e, ownerKey, Serializer().Serialize(new Stamp { schema = 1, owner = owner.handle,
                    document_id = documentId, run_id = report.run_id, layout_key = layoutKey,
                    entry_digest = digest, zone_ids = owner.zone_ids }));
            }
            foreach (QuantityElement element in report.elements)
                foreach (QuantityCadEntity link in element.cad_entities)
                {
                    var e = Live(tr, Resolve(db, link.handle));
                    Write(tr, e, elementKey, Serializer().Serialize(new Stamp { schema = 1, owner = link.handle,
                        document_id = documentId, run_id = report.run_id, layout_key = layoutKey,
                        entry_digest = digest, element_id = element.element_id, role = link.role,
                        zone_ids = element.zone_ids }));
                }
            foreach (string old in oldRuns) if (old != report.run_id) RemoveUnusedRun(tr, db, root, old);
        }

        internal static void RemoveForLayout(Transaction tr, Entity carrier, string layoutKey)
        {
            if (carrier == null) return;
            var stamp = ReadStamp(tr, carrier, OwnerKey);
            if (stamp == null || stamp.layout_key != layoutKey) return;
            Remove(tr, carrier, OwnerKey);
            var root = Root(tr, carrier.Database, false);
            if (root != null && SafeId(stamp.run_id)) RemoveUnusedRun(tr, carrier.Database, root, stamp.run_id);
        }

        internal static QuantitySelection ReadCladding(Transaction tr, Database db, IEnumerable<ObjectId> selectedIds)
        {
            return ReadCore(tr, db, selectedIds, false, true, false);
        }
        internal static QuantitySelection ReadCladding(Transaction tr, Database db, IEnumerable<ObjectId> selectedIds,
            bool byZone, bool includeCutting)
        { return ReadCore(tr, db, selectedIds, false, byZone, includeCutting); }

        private static QuantitySelection ReadCore(Transaction tr, Database db, IEnumerable<ObjectId> selectedIds,
            bool frame, bool byZone, bool includeCutting)
        {
            var result = new QuantitySelection();
            string kind = frame ? "frame" : "cladding";
            string ownerKey = frame ? FrameOwnerKey : OwnerKey, elementKey = frame ? FrameElementKey : ElementKey;
            var validationContext = new LayoutGeometryGuard.VerificationContext();
            var manualContext = new ManualReadContext(validationContext);
            var manualZones = new Dictionary<ObjectId, ManualZoneResult>();
            var manualNames = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
            var digestParts = new List<string>();
            var frameObserved = new Dictionary<string, FrameSource>();
            var timer = Stopwatch.StartNew();
            try
            {
                var root = Root(tr, db, false);
                string document = root == null ? null : ReadRecord(tr, root, DocumentKey, null);
                var requested = new Dictionary<string, Stamp>();
                var chosen = new HashSet<ObjectId>();
                var selectedZones = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Queue<ObjectId>(selectedIds ?? new ObjectId[0]);
                bool manualIndexes = HasAnyManualIndexes(tr, db);
                while (pending.Count > 0)
                {
                    ObjectId id = pending.Dequeue();
                    if (!chosen.Add(id)) continue;
                    var e = Live(tr, id);
                    if (e == null) throw new InvalidOperationException("Один из выбранных объектов отсутствует.");
                    var stamp = ReadStamp(tr, e, ownerKey);
                    if (stamp == null) stamp = ReadStamp(tr, e, elementKey);
                    if (stamp == null)
                    {
                        if (HasManual(tr, e))
                        {
                            ObjectId canonical = ResolveManualSelection(tr, db, e, kind, manualContext);
                            if (!canonical.IsNull) { pending.Enqueue(canonical); continue; }
                            AddUnaccounted(result, e, "Ручная позиция относится к другому виду ведомости.");
                            continue;
                        }
                        if (frame) CheckFrameUnavailable(tr, e);
                        if (Has(tr, e, "ATFZONE"))
                        {
                            var zone = GetCanonicalZone(tr, db, e, manualContext);
                            if (!zone.Ok) throw new InvalidOperationException(zone.Reason);
                            var hatch = Live(tr, zone.HatchId) as Hatch;
                            bool imported = HasManualIndex(tr, db, hatch, kind);
                            // Manual quantities cannot conceal missing/corrupt generated data.
                            if (HasGeneratedSource(tr, e, frame) || HasGeneratedSource(tr, hatch, frame))
                                throw new InvalidOperationException("У выбранной зоны есть результат построения без действующего паспорта. Повторите ATTILE или ATCLAD; ручной импорт не заменяет этот результат.");
                            if (imported)
                            {
                                CollectManualZone(tr, db, hatch, kind, manualContext, manualZones, manualNames, selectedZones);
                                manualIndexes = true;
                                continue;
                            }
                        }
                        if (frame ? IsFrameCandidate(tr, e) : IsCandidate(tr, e))
                            throw new InvalidOperationException("У выбранной зоны или раскладки нет паспорта деталей. Повторите построение этой раскладки либо зарегистрируйте ручные элементы через ATFTABLE → Ручные.");
                        AddUnaccounted(result, e, "Объект не зарегистрирован для ведомости. Используйте ATFTABLE → Ручные.");
                        continue;
                    }
                    if (stamp.schema != 1 || stamp.owner != e.Handle.ToString() || stamp.document_id != document ||
                        !SafeId(stamp.run_id) || stamp.zone_ids == null || stamp.zone_ids.Count == 0)
                        throw new InvalidOperationException("Выбранный паспорт скопирован или повреждён. Повторите раскладку.");
                    Stamp prior;
                    if (requested.TryGetValue(stamp.run_id, out prior))
                    {
                        if (prior.entry_digest != stamp.entry_digest || prior.layout_key != stamp.layout_key)
                            throw new InvalidOperationException("Выбранные метки одного построения имеют разный состав.");
                    }
                    else requested[stamp.run_id] = stamp;
                    foreach (string z in stamp.zone_ids) selectedZones.Add(z);
                }
                var known = new Dictionary<string, HashSet<string>>();
                var verifiedEntities = new HashSet<ObjectId>();
                var zones = new Dictionary<string, string>();
                DefinitionCaches.Remove(tr);
                var pendingReads = new List<PendingQuantityRead>();
                foreach (var request in requested)
                {
                    var refs = new List<ObjectId>();
                    string packed = ReadRecord(tr, root, Prefix + request.Key, refs);
                    if (string.IsNullOrEmpty(packed)) throw new InvalidOperationException("Сохранённый состав раскладки отсутствует. Повторите ATTILE или ATCLAD.");
                    string json = Decompress(packed), digest = Hash(json);
                    var entry = Serializer().Deserialize<Entry>(json);
                    if (entry == null || (entry.schema != 2 && (frame || entry.schema != 1)) || entry.document_id != document || entry.run_id != request.Key ||
                        (frame ? entry.layout_key != "ATFRAME" : (entry.layout_key != "ATCLAD" && entry.layout_key != "ATTILE")) || entry.owners == null ||
                        entry.entity_handles == null || entry.owners.Count == 0)
                        throw new InvalidOperationException("Несовместимый паспорт деталей; повторите раскладку.");
                    if (request.Value.entry_digest != digest || request.Value.layout_key != entry.layout_key)
                        throw new InvalidOperationException("Состав раскладки не соответствует выбранной метке.");
                    var report = entry.schema == 2 ? entry.quantity_report : Serializer().Deserialize<QuantityReport>(entry.report_json);
                    if (report == null || report.document_id != document || report.run_id != entry.run_id || report.kind != kind)
                        throw new InvalidOperationException("Паспорт деталей повреждён.");
                    pendingReads.Add(new PendingQuantityRead { Entry = entry, Report = report, Digest = digest, Refs = refs });
                    // Load selected manual indexes before comparing physical elements.
                    // Every generation and its owners must still pass VerifyEntry below.
                    foreach (Owner owner in entry.owners)
                    {
                        bool included = false;
                        foreach (string z in owner.zone_ids) if (selectedZones.Contains(z)) { included = true; break; }
                        if (!included) continue;
                        var carrier = Live(tr, Resolve(db, owner.handle));
                        if (carrier == null || !Has(tr, carrier, "ATFZONE")) continue;
                        // A mark may have only a soft link to the anchored hatch. Resolve
                        // it even if the NOD index container was deleted.
                        if (!manualIndexes && carrier is Hatch && !HasManualAnchor(tr, carrier, kind))
                        {
                            // The generated owner already names the selected canonical
                            // hatch. Keep its scope even when both manual index markers
                            // were removed: surviving bindings must be found below.
                            manualContext.UsedZones.Add(carrier.ObjectId);
                            continue;
                        }
                        CollectManualZone(tr, db, carrier, kind, manualContext, manualZones, manualNames, selectedZones);
                    }
                }
                var manualRoles = new HashSet<string>(StringComparer.Ordinal);
                foreach (var manual in manualZones.Values)
                    if (manual.HasIndex) foreach (var element in manual.Report.elements) manualRoles.Add(element.role);
                var generatedCoincidences = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var seenCoincidences = new HashSet<string>(StringComparer.Ordinal);
                Action<Entity, QuantityElement> checkCoincidence = null;
                if (manualContext.DuplicateKeys.Count > 0)
                    checkCoincidence = (entity, element) => {
                        if (!manualRoles.Contains(element.role) || !SelectedManualScope(element.zone_ids, selectedZones)) return;
                        string key = ManualQuantityGeometry.GeneratedDuplicateKey(tr, entity, element, kind, manualContext.Geometry);
                        if (key == null || !manualContext.DuplicateKeys.ContainsKey(key) ||
                            !seenCoincidences.Add(key + ":" + element.element_id)) return;
                        List<string> handles;
                        if (!generatedCoincidences.TryGetValue(key, out handles)) {
                            handles = new List<string>(); generatedCoincidences.Add(key, handles);
                        }
                        handles.Add(entity.Handle.ToString());
                    };
                foreach (var pendingRead in pendingReads)
                {
                    var entry = pendingRead.Entry; var report = pendingRead.Report;
                    VerifyEntry(tr, db, entry, report, pendingRead.Digest, pendingRead.Refs, validationContext, frameObserved, checkCoincidence);
                    for (int i = entry.owners.Count; i < entry.owners.Count + entry.entity_handles.Count; i++)
                        verifiedEntities.Add(pendingRead.Refs[i]);
                    digestParts.Add(entry.run_id + ":" + pendingRead.Digest);
                    foreach (string z in report.zone_ids)
                    {
                        string prior;
                        if (zones.TryGetValue(z, out prior) && prior != report.run_id)
                            throw new InvalidOperationException("Марка зоны «" + z + "» относится к разным построениям. Уточните область ведомости.");
                        zones[z] = report.run_id;
                    }
                    result.Reports.Add(report);
                    known[entry.run_id] = new HashSet<string>(entry.entity_handles);
                }
                foreach (var pair in manualContext.DuplicateKeys)
                {
                    List<string> generated;
                    generatedCoincidences.TryGetValue(pair.Key, out generated);
                    if (pair.Value.Count < 2 && generated == null) continue;
                    pair.Value.Sort(StringComparer.Ordinal);
                    string samples = string.Join(", ", pair.Value.GetRange(0, Math.Min(4, pair.Value.Count)));
                    result.Warnings.Add("Возможное совпадение отдельных деталей: ручных — " + pair.Value.Count +
                        (generated == null ? "" : ", созданных плагином — " + generated.Count) +
                        ". Ручные объекты: " + samples + (pair.Value.Count > 4 ? "…" : "") +
                        ". Все отдельные детали учтены; проверьте наложение. Это не полная проверка коллизий.");
                }
                bool hasManualReports = false;
                foreach (var pair in manualZones)
                {
                    var manual = pair.Value;
                    if (!manual.HasIndex) continue;
                    hasManualReports = true;
                    result.Reports.Add(manual.Report);
                    digestParts.Add("manual:" + pair.Key.Handle.ToString() + ":" + manual.Digest);
                    foreach (ObjectId id in manual.VerifiedIds) verifiedEntities.Add(id);
                    foreach (string warning in manual.Warnings) result.Warnings.Add(warning);
                }
                if (result.Reports.Count == 0)
                    throw new InvalidOperationException("В выборке нет подтверждённого состава. Создайте раскладку или зарегистрируйте ручные элементы через ATFTABLE → Ручные; неучтённые объекты не означают нулевой состав.");
                // One scan for generated copies and manual orphan/copy diagnostics.
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (verifiedEntities.Contains(id)) continue;
                    var e = Live(tr, id);
                    if (e == null || e.ExtensionDictionary.IsNull) continue;
                    var extension = (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead);
                    // A binding is evidence even when its NOD directory AND its
                    // zone anchor are absent. Inspect the existing single scan;
                    // do not add another ModelSpace or extension-dictionary pass.
                    if (extension.Contains(ManualKey)) VerifyManualOrphanAtScan(tr, db, e, kind, manualContext, extension);
                    if (hasManualReports && extension.Contains(ownerKey))
                    {
                        var ownerStamp = ParseStamp(ReadRecord(tr, extension, ownerKey, null));
                        if ((ownerStamp == null || !known.ContainsKey(ownerStamp.run_id ?? "")) && extension.Contains("ATFZONE"))
                        {
                            var zoneRefs = new List<ObjectId>();
                            ReadRecord(tr, extension, ZoneGeometryGuard.Key, zoneRefs);
                            // Filter by the existing soft link before validation.
                            // An unrelated owner's zone must not enter UsedZones
                            // and accidentally widen orphan checks to the whole DWG.
                            if ((zoneRefs.Count > 0 && manualZones.ContainsKey(zoneRefs[0])) ||
                                (ownerStamp != null && SelectedManualScope(ownerStamp.zone_ids, selectedZones)))
                            {
                                if (ownerStamp == null)
                                    throw new InvalidOperationException("Повреждён паспорт результата построения выбранной зоны. Повторите построение; ручной импорт не заменяет этот результат.");
                                var ownerZone = GetCanonicalZone(tr, db, e, manualContext);
                                if (manualZones.ContainsKey(ownerZone.HatchId))
                                    throw new InvalidOperationException("У выбранной зоны есть результат построения на другой исходной марке. " +
                                        "Выберите исходную марку зоны вместе с ручными деталями либо повторите " +
                                        (frame ? "ATFRAME" : "ATTILE или ATCLAD") + "; неполный состав не используется.");
                            }
                        }
                    }
                    if (!extension.Contains(elementKey)) continue;
                    var stamp = ParseStamp(ReadRecord(tr, extension, elementKey, null));
                    HashSet<string> expected;
                    if (stamp == null) throw new InvalidOperationException("Повреждён паспорт одной из деталей облицовки.");
                    if (known.TryGetValue(stamp.run_id ?? "", out expected) &&
                        (stamp.owner != e.Handle.ToString() || !expected.Contains(e.Handle.ToString())))
                        throw new InvalidOperationException("Обнаружена копия или лишняя деталь выбранной раскладки. Ведомость не создана; приведите состав в соответствие и повторите построение.");
                }
                foreach (string warning in manualContext.Warnings) result.Warnings.Add(warning);
                result.SelectedZoneIds.AddRange(selectedZones); result.SelectedZoneIds.Sort(StringComparer.Ordinal);
                result.VerificationMilliseconds = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                var validation = FacadeQuantitiesCore.BuildRows(result.Reports, result.SelectedZoneIds, byZone, includeCutting);
                result.AggregationMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (!validation.ok) throw new InvalidOperationException(FirstIssue(validation));
                if (result.Unaccounted.Count > 0)
                    result.Warnings.Add("Не учтены выбранные объекты: " + result.Unaccounted.Count + ". Причины приведены в проверке объектов; зарегистрируйте подходящие позиции через ATFTABLE → Ручные.");
                result.Rows = validation;
                digestParts.Sort(StringComparer.Ordinal);
                result.Warnings.Sort(StringComparer.Ordinal);
                result.Unaccounted.Sort((a, b) => StringComparer.Ordinal.Compare(a.handle, b.handle));
                result.Fingerprint = Hash(Serializer().Serialize(new object[] { digestParts, result.SelectedZoneIds, result.Warnings, result.Unaccounted, byZone, includeCutting }));
                result.Ok = true;
            }
            catch (Exception ex)
            {
                result.Ok = false; result.Reason = ex.Message;
                if (frame) result.Reason = result.Reason.Replace("облицовки", "подсистемы")
                    .Replace("облицовку", "подсистему").Replace("ATTILE или ATCLAD", "ATFRAME")
                    .Replace("ATTILE/ATCLAD", "ATFRAME");
                result.Reports.Clear(); result.SelectedZoneIds.Clear();
            }
            return result;
        }

        private static bool SelectedManualScope(List<string> zones, HashSet<string> selected)
        { if (zones != null) foreach (string zone in zones) if (selected.Contains(zone)) return true; return false; }

        private static bool HasGeneratedSource(Transaction tr, Entity entity, bool frame)
        { return frame ? Has(tr, entity, "ATFRAME") || Has(tr, entity, FrameUnavailableKey) :
            Has(tr, entity, "ATCLAD") || Has(tr, entity, "ATTILE") || Has(tr, entity, "ATLAYOUT_CURRENT"); }

        private static void AddUnaccounted(QuantitySelection result, Entity entity, string reason)
        { result.Unaccounted.Add(new QuantityUnaccountedItem { handle = entity.Handle.ToString(),
            type = entity.GetType().Name, layer = entity.Layer, reason = reason }); }

        private static void CollectManualZone(Transaction tr, Database db, Entity carrier, string kind,
            ManualReadContext context, Dictionary<ObjectId, ManualZoneResult> collected,
            Dictionary<string, ObjectId> names, HashSet<string> selectedZones)
        {
            var zone = GetCanonicalZone(tr, db, carrier, context);
            if (!zone.Ok) throw new InvalidOperationException(zone.Reason);
            ObjectId existing;
            if (names.TryGetValue(zone.ZoneId, out existing) && existing != zone.HatchId)
                throw new InvalidOperationException("Марка ручной зоны неоднозначна: «" + zone.ZoneId + "». Уточните марки зон.");
            names[zone.ZoneId] = zone.HatchId;
            ManualZoneResult manual;
            if (!collected.TryGetValue(zone.HatchId, out manual))
            {
                manual = ReadManualZone(tr, db, (Hatch)Live(tr, zone.HatchId), kind, context);
                collected.Add(zone.HatchId, manual);
            }
            if (manual.HasIndex) selectedZones.Add(zone.ZoneId);
        }

        private static void VerifyEntry(Transaction tr, Database db, Entry entry, QuantityReport report,
            string digest, List<ObjectId> refs, LayoutGeometryGuard.VerificationContext validationContext,
            Dictionary<string, FrameSource> frameObserved, Action<Entity, QuantityElement> verifiedElement)
        {
            bool frame = entry.layout_key == "ATFRAME";
            string ownerKey = frame ? FrameOwnerKey : OwnerKey, elementKey = frame ? FrameElementKey : ElementKey;
            int sourceCount = frame && entry.frame_sources != null && entry.frame_sources.sources != null ? entry.frame_sources.sources.Count : 0;
            if (refs.Count != entry.owners.Count + entry.entity_handles.Count + sourceCount)
                throw new InvalidOperationException("Неполные связи паспорта деталей.");
            int pos = 0;
            var ownerHandles = new HashSet<string>();
            foreach (Owner owner in entry.owners)
            {
                var e = Live(tr, refs[pos++]);
                if (e == null || e.Handle.ToString() != owner.handle || !ownerHandles.Add(owner.handle))
                    throw new InvalidOperationException("Владелец раскладки удалён или скопирован; повторите построение.");
                var stamp = ReadStamp(tr, e, ownerKey);
                if (!Matches(stamp, entry, digest, owner.handle) || !SameSet(stamp.zone_ids, owner.zone_ids))
                    throw new InvalidOperationException("Часть группы раскладки заменена или имеет другую ревизию. Повторите всю группу.");
                var current = frame ? FrameOwnerState(tr, e) : LayoutGeometryGuard.Verify(tr, db, e, entry.layout_key, validationContext);
                if (!current.Ok) throw new InvalidOperationException(current.Reason);
                if (current.Revision != owner.revision || current.Fingerprint != owner.fingerprint || current.LayoutDigest != owner.layout_digest)
                    throw new InvalidOperationException("Источник ведомости изменился после построения.");
            }
            var byHandle = new Dictionary<string, QuantityCadEntity>();
            var elementOf = new Dictionary<string, QuantityElement>();
            foreach (var element in report.elements)
                foreach (var link in element.cad_entities)
                {
                    if (byHandle.ContainsKey(link.handle)) throw new InvalidOperationException("Один объект отнесён к нескольким физическим деталям.");
                    byHandle.Add(link.handle, link); elementOf.Add(link.handle, element);
                }
            if (byHandle.Count != entry.entity_handles.Count)
                throw new InvalidOperationException("Состав паспорта и связи с чертежом различаются.");
            var seen = new HashSet<string>();
            var cache = DefinitionCaches.GetOrCreateValue(tr);
            foreach (string handle in entry.entity_handles)
            {
                var e = Live(tr, refs[pos++]);
                QuantityCadEntity link;
                if (!seen.Add(handle) || e == null || e.Handle.ToString() != handle || !byHandle.TryGetValue(handle, out link))
                    throw new InvalidOperationException("Деталь или её графика удалена/заменена. Повторите раскладку.");
                var element = elementOf[handle];
                var stamp = ReadStamp(tr, e, elementKey);
                if (!Matches(stamp, entry, digest, handle) || stamp.element_id != element.element_id || stamp.role != link.role ||
                    !SameSet(stamp.zone_ids, element.zone_ids))
                    throw new InvalidOperationException("Метка физической детали изменена или скопирована.");
                if (Fingerprint(tr, e, cache, new HashSet<ObjectId>()) != link.fingerprint)
                    throw new InvalidOperationException("Геометрия, свойства или марка детали изменены после раскладки. Повторите ATTILE/ATCLAD; старые количества не используются.");
                if (verifiedElement != null) verifiedElement(e, element);
            }
            if (frame)
            {
                for (int i = 0; i < sourceCount; i++)
                    if (refs[pos++].IsNull || refs[pos - 1].Handle.ToString() != entry.frame_sources.sources[i].handle)
                        throw new InvalidOperationException("Ссылка на источник подсистемы изменена или скопирована.");
                VerifyFrameSources(tr, db, entry.frame_sources, frameObserved);
            }
        }

        private static bool Matches(Stamp s, Entry e, string digest, string handle)
        { return s != null && s.schema == 1 && s.owner == handle && s.document_id == e.document_id &&
                 s.run_id == e.run_id && s.layout_key == e.layout_key && s.entry_digest == digest; }
        private static bool SameSet(List<string> a, List<string> b)
        { return a != null && b != null && a.Count == new HashSet<string>(a).Count && new HashSet<string>(a).SetEquals(b); }
        private static bool SafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 100) return false;
            foreach (char c in id) if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            return true;
        }
        private static string FirstIssue(QuantityResult result)
        {
            foreach (var issue in result.issues)
                if (issue.severity == "error") return issue.message;
            return "Неполный паспорт деталей облицовки.";
        }

        private static void RemoveUnusedRun(Transaction tr, Database db, DBDictionary root, string run)
        {
            string packed = ReadRecord(tr, root, Prefix + run, null);
            if (string.IsNullOrEmpty(packed)) return;
            Entry entry;
            try { entry = Serializer().Deserialize<Entry>(Decompress(packed)); }
            catch { return; } // A damaged record is retained for diagnosis, never silently adopted.
            if (entry == null || entry.owners == null) return;
            foreach (Owner owner in entry.owners)
            {
                var e = Live(tr, Resolve(db, owner.handle));
                var stamp = e == null ? null : ReadStamp(tr, e, entry.layout_key == "ATFRAME" ? FrameOwnerKey : OwnerKey);
                if (stamp != null && stamp.owner == owner.handle && stamp.run_id == run) return;
            }
            if (!root.IsWriteEnabled) root.UpgradeOpen();
            ObjectId id = root.GetAt(Prefix + run);
            root.Remove(Prefix + run);
            tr.GetObject(id, OpenMode.ForWrite).Erase();
        }

        private static string Fingerprint(Transaction tr, Entity e, Dictionary<ObjectId, string> cache, HashSet<ObjectId> visiting)
        {
            var s = new StringBuilder();
            Add(s, e.GetType().FullName); Add(s, e.Layer); Color(s, e.Color);
            if (!e.LayerId.IsNull)
            {
                var layer = tr.GetObject(e.LayerId, OpenMode.ForRead) as LayerTableRecord;
                if (layer != null) Color(s, layer.Color);
            }
            var pl = e as Polyline;
            if (pl != null)
            {
                Add(s, pl.Closed); Number(s, pl.Elevation); Vector(s, pl.Normal); Number(s, pl.ConstantWidth);
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    var p = pl.GetPoint2dAt(i); Number(s, p.X); Number(s, p.Y); Number(s, pl.GetBulgeAt(i));
                    Number(s, pl.GetStartWidthAt(i)); Number(s, pl.GetEndWidthAt(i));
                }
            }
            else if (e is BlockReference)
            {
                var br = (BlockReference)e;
                Point(s, br.Position); Vector(s, br.Normal); Number(s, br.Rotation);
                Number(s, br.ScaleFactors.X); Number(s, br.ScaleFactors.Y); Number(s, br.ScaleFactors.Z);
                Add(s, br.BlockTableRecord.Handle.ToString());
                Add(s, Definition(tr, br.BlockTableRecord, cache, visiting));
                if (br.IsDynamicBlock)
                {
                    Add(s, br.DynamicBlockTableRecord.Handle.ToString());
                    var props = new List<string>();
                    foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                        props.Add(p.PropertyName + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture));
                    props.Sort(StringComparer.Ordinal); foreach (string p in props) Add(s, p);
                }
                var attrs = new List<string>();
                foreach (ObjectId id in br.AttributeCollection)
                {
                    var ar = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                    if (ar != null) attrs.Add(ar.Tag + "=" + ar.TextString);
                }
                attrs.Sort(StringComparer.Ordinal); foreach (string a in attrs) Add(s, a);
            }
            else if (e is Line)
            { var line = (Line)e; Point(s, line.StartPoint); Point(s, line.EndPoint); Vector(s, line.Normal); }
            else if (e is Circle)
            { var c = (Circle)e; Point(s, c.Center); Number(s, c.Radius); Vector(s, c.Normal); }
            else if (e is Arc)
            { var a = (Arc)e; Point(s, a.Center); Number(s, a.Radius); Vector(s, a.Normal); Number(s, a.StartAngle); Number(s, a.EndAngle); }
            else if (e is Ellipse)
            { var a = (Ellipse)e; Point(s, a.Center); Vector(s, a.MajorAxis); Vector(s, a.MinorAxis); Number(s, a.StartAngle); Number(s, a.EndAngle); }
            else if (e is DBText)
            {
                var t = (DBText)e; Point(s, t.Position); Point(s, t.AlignmentPoint); Vector(s, t.Normal);
                Number(s, t.Height); Number(s, t.Rotation); Number(s, t.WidthFactor); Add(s, t.TextString);
                var ad = e as AttributeDefinition; if (ad != null) { Add(s, ad.Tag); Add(s, ad.Constant); }
            }
            else if (e is MText)
            { var t = (MText)e; Point(s, t.Location); Vector(s, t.Normal); Number(s, t.Rotation); Number(s, t.Width); Number(s, t.TextHeight); Add(s, t.Contents); }
            else if (e is DBPoint) { Point(s, ((DBPoint)e).Position); }
            else if (e is Solid) { for (short i = 0; i < 4; i++) Point(s, ((Solid)e).GetPointAt(i)); }
            else if (e is Hatch)
            {
                var h = (Hatch)e; Add(s, h.PatternName); Number(s, h.PatternScale); Number(s, h.PatternAngle);
                Number(s, h.Elevation); Vector(s, h.Normal); Add(s, h.NumberOfLoops);
                for (int i = 0; i < h.NumberOfLoops; i++)
                {
                    var loop = h.GetLoopAt(i); Add(s, (int)loop.LoopType);
                    if (!loop.IsPolyline) throw new InvalidOperationException("Ведомость пока не поддерживает криволинейную штриховку внутри образца блока. Используйте проверенный образец с полилинейными границами.");
                    foreach (BulgeVertex v in loop.Polyline)
                    { Number(s, v.Vertex.X); Number(s, v.Vertex.Y); Number(s, v.Bulge); }
                }
            }
            else throw new InvalidOperationException("Для ведомости не поддерживается объект образца: " + e.GetType().Name + ". Выберите проверенный образец облицовки.");
            return Hash(s.ToString());
        }

        private static string Definition(Transaction tr, ObjectId id, Dictionary<ObjectId, string> cache, HashSet<ObjectId> visiting)
        {
            string value;
            if (cache.TryGetValue(id, out value)) return value;
            if (!visiting.Add(id)) throw new InvalidOperationException("Циклическая структура блока облицовки.");
            var def = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var s = new StringBuilder(); Add(s, def.Name); Point(s, def.Origin);
            foreach (ObjectId child in def)
            {
                var e = Live(tr, child);
                if (e != null) { Add(s, child.Handle.ToString()); Add(s, Fingerprint(tr, e, cache, visiting)); }
            }
            visiting.Remove(id); value = Hash(s.ToString()); cache[id] = value; return value;
        }
        private static void Color(StringBuilder s, Autodesk.AutoCAD.Colors.Color c)
        { Add(s, (int)c.ColorMethod); Add(s, c.ColorIndex); Add(s, c.Red); Add(s, c.Green); Add(s, c.Blue); }
        private static void Add(StringBuilder s, object value)
        { string t = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; s.Append(t.Length).Append(':').Append(t).Append(';'); }
        private static void Number(StringBuilder s, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("Объект содержит неконечные координаты.");
            Add(s, value.ToString("R", CultureInfo.InvariantCulture));
        }
        private static void Point(StringBuilder s, Point3d p) { Number(s, p.X); Number(s, p.Y); Number(s, p.Z); }
        private static void Vector(StringBuilder s, Vector3d p) { Number(s, p.X); Number(s, p.Y); Number(s, p.Z); }
        private static string Hash(string text)
        {
            using (var h = SHA256.Create())
            {
                // Most fingerprints are tiny; reserve chunk buffers only for large ledgers.
                if (text.Length <= 4096) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
                EncodeUtf8(text, (bytes, count) => h.TransformBlock(bytes, 0, count, bytes, 0));
                h.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(h.Hash).Replace("-", "");
            }
        }
        private static void EncodeUtf8(string text, Action<byte[], int> consume)
        {
            var chars = new char[4096];
            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(chars.Length)];
            var encoder = Encoding.UTF8.GetEncoder();
            for (int offset = 0; offset < text.Length;)
            {
                int count = Math.Min(chars.Length, text.Length - offset);
                text.CopyTo(offset, chars, 0, count); offset += count;
                // Retain encoder state when a surrogate pair crosses a chunk boundary.
                int length = encoder.GetBytes(chars, 0, count, bytes, 0, offset == text.Length);
                consume(bytes, length);
            }
        }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 }; }

        private static ObjectId Resolve(Database db, string handle)
        {
            long n;
            if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return ObjectId.Null;
            ObjectId id; return db.TryGetObjectId(new Handle(n), out id) ? id : ObjectId.Null;
        }
        private static Entity Live(Transaction tr, ObjectId id)
        { return id.IsNull || id.IsErased ? null : tr.GetObject(id, OpenMode.ForRead) as Entity; }
        private static DBDictionary Root(Transaction tr, Database db, bool create)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, create ? OpenMode.ForWrite : OpenMode.ForRead);
            if (nod.Contains(RootKey)) return (DBDictionary)tr.GetObject(nod.GetAt(RootKey), create ? OpenMode.ForWrite : OpenMode.ForRead);
            if (!create) return null;
            var dict = new DBDictionary(); nod.SetAt(RootKey, dict); tr.AddNewlyCreatedDBObject(dict, true); return dict;
        }
        private static bool Has(Transaction tr, Entity e, string key)
        { return e != null && !e.ExtensionDictionary.IsNull && ((DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead)).Contains(key); }
        private static Stamp ReadStamp(Transaction tr, Entity e, string key)
        { return ParseStamp(Read(tr, e, key)); }
        private static Stamp ParseStamp(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return Serializer().Deserialize<Stamp>(json); }
            catch (ArgumentException)
            { throw new InvalidOperationException("Повреждён паспорт объекта облицовки. Восстановите его исходную раскладку и повторите ведомость."); }
            catch (InvalidOperationException)
            { throw new InvalidOperationException("Повреждён паспорт объекта облицовки. Восстановите его исходную раскладку и повторите ведомость."); }
        }
        private static string Read(Transaction tr, Entity e, string key)
        {
            if (e == null || e.ExtensionDictionary.IsNull) return null;
            return ReadRecord(tr, (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead), key, null);
        }
        private static string ReadRecord(Transaction tr, DBDictionary dict, string key, List<ObjectId> refs)
        {
            if (dict == null || !dict.Contains(key)) return null;
            var xr = tr.GetObject(dict.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (xr == null || xr.Data == null) return null;
            var s = new StringBuilder();
            foreach (TypedValue tv in xr.Data)
                if (tv.TypeCode == (int)DxfCode.Text) s.Append(Convert.ToString(tv.Value, CultureInfo.InvariantCulture));
                else if (refs != null && tv.TypeCode == (int)DxfCode.SoftPointerId && tv.Value is ObjectId) refs.Add((ObjectId)tv.Value);
            return s.ToString();
        }
        private static void Write(Transaction tr, Entity e, string key, string text)
        {
            if (!e.IsWriteEnabled) e.UpgradeOpen();
            if (e.ExtensionDictionary.IsNull) e.CreateExtensionDictionary();
            WriteRecord(tr, (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForWrite), key, text, new List<ObjectId>());
        }
        private static void WriteRecord(Transaction tr, DBDictionary dict, string key, string text, List<ObjectId> refs)
        {
            if (!dict.IsWriteEnabled) dict.UpgradeOpen();
            using (var rb = new ResultBuffer())
            {
                for (int i = 0; i < text.Length; i += 250) rb.Add(new TypedValue((int)DxfCode.Text, text.Substring(i, Math.Min(250, text.Length - i))));
                foreach (ObjectId id in refs) rb.Add(new TypedValue((int)DxfCode.SoftPointerId, id));
                Xrecord xr;
                if (dict.Contains(key)) { xr = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForWrite); xr.Data = rb; }
                else { xr = new Xrecord { Data = rb }; dict.SetAt(key, xr); tr.AddNewlyCreatedDBObject(xr, true); }
                xr.XlateReferences = true;
            }
        }
        private static void Remove(Transaction tr, Entity e, string key)
        {
            if (!Has(tr, e, key)) return;
            var dict = (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForWrite);
            ObjectId id = dict.GetAt(key); dict.Remove(key); tr.GetObject(id, OpenMode.ForWrite).Erase();
        }
        private static string Compress(string json)
        {
            CheckPayloadSize(Encoding.UTF8.GetByteCount(json));
            using (var stream = new MemoryStream())
            {
                using (var zip = new GZipStream(stream, CompressionMode.Compress, true))
                    EncodeUtf8(json, (bytes, count) => zip.Write(bytes, 0, count));
                return "gzip1:" + Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
        private static string Decompress(string packed)
        {
            if (!packed.StartsWith("gzip1:", StringComparison.Ordinal)) throw new InvalidOperationException("Неизвестный формат паспорта деталей.");
            using (var input = new MemoryStream(Convert.FromBase64String(packed.Substring(6))))
            using (var zip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[32768]; int count;
                while ((count = zip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    CheckPayloadSize(output.Length + count);
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            }
        }
        private static void CheckPayloadSize(long bytes)
        {
            if (bytes > MaxPayloadBytes)
                throw new InvalidOperationException("Паспорт деталей превышает поддержанный размер. Уменьшите область раскладки; прежний результат сохранён.");
        }
    }
}
