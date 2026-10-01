using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;

namespace FacadeSafety
{
    // DWG persistence only. Catalogue/override semantics belong to the pure
    // AFrame model; this layer never reads geometry or starts a transaction.
    internal static class FacadeProjectParameterStore
    {
        internal const string ProjectRootKey = "AFACADE_PROJECT";
        internal const string ProjectRecordKey = "PARAMETERS";
        internal const string ZoneKey = "AFACADE_ZONE_PARAMETERS";
        private const string ZoneSchema = "afacade_zone_parameters_record/1";
        private const int MaxRecordCharacters = 1024 * 1024;

        internal sealed class Record
        {
            internal bool Present;
            internal string Json, RecordDigest, Owner, ZoneFingerprint;
            internal Dictionary<string, object> Payload;
        }
        internal sealed class ReadContext
        {
            internal Record Project;
            internal bool ProjectRead;
            internal int ProjectReads;
            internal readonly Dictionary<ObjectId, Record> Zones = new Dictionary<ObjectId, Record>();
            internal int ZoneReads;
        }
        public sealed class ProjectDependency
        {
            public string project_id { get; set; }
            public long revision { get; set; }
            public string content_digest { get; set; }
            public string record_digest { get; set; }
        }

        internal static Record ReadProject(Transaction tr, Database db, ReadContext context = null)
        {
            if (context != null && context.ProjectRead) return context.Project;
            var root = ProjectRoot(tr, db, false);
            var result = ReadRecord(tr, root, ProjectRecordKey, null);
            if (result.Present) CheckProjectHeader(result.Payload);
            if (context != null)
            {
                context.Project = result; context.ProjectRead = true; context.ProjectReads++;
            }
            return result;
        }

        internal static Record ReadZone(Transaction tr, Hatch hatch, string verifiedFingerprint = null, ReadContext context = null)
        {
            CheckOwner(hatch);
            Record cached;
            if (context != null && context.Zones.TryGetValue(hatch.ObjectId, out cached))
            {
                CheckFingerprint(cached, verifiedFingerprint); return cached;
            }
            var ext = hatch.ExtensionDictionary.IsNull ? null :
                tr.GetObject(hatch.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (!hatch.ExtensionDictionary.IsNull && ext == null)
                throw Fail("Словарь параметров зоны повреждён; привязка не подменяется отсутствием.");
            var refs = new List<ObjectId>();
            var result = ReadRecord(tr, ext, ZoneKey, refs);
            if (!result.Present)
            {
                if (context != null) { context.Zones.Add(hatch.ObjectId, result); context.ZoneReads++; }
                return result;
            }
            var envelope = result.Payload;
            if (envelope.Count != 4 || Text(envelope, "schema") != ZoneSchema ||
                Text(envelope, "owner") != hatch.Handle.ToString() ||
                string.IsNullOrEmpty(Text(envelope, "zone_fingerprint")) ||
                refs.Count != 1 || refs[0] != hatch.ObjectId || refs[0].IsErased)
                throw Fail("Привязка параметров зоны скопирована или повреждена. Повторите явную привязку исходной зоны.");
            object payload;
            if (!envelope.TryGetValue("payload", out payload) || !(payload is Dictionary<string, object>))
                throw Fail("Отсутствуют сохранённые параметры зоны.");
            result.Owner = Text(envelope, "owner");
            result.ZoneFingerprint = Text(envelope, "zone_fingerprint");
            CheckFingerprint(result, verifiedFingerprint);
            result.Payload = (Dictionary<string, object>)payload;
            if (context != null) { context.Zones.Add(hatch.ObjectId, result); context.ZoneReads++; }
            return result;
        }

        private static void CheckFingerprint(Record record, string verifiedFingerprint)
        {
            if (record.Present && verifiedFingerprint != null && verifiedFingerprint != record.ZoneFingerprint)
                throw Fail("Поколение зоны изменилось после привязки параметров. Параметры не переносятся автоматически.");
        }

        internal static void WriteProject(Transaction tr, Database db, Record expected,
            Dictionary<string, object> payload)
        {
            CheckProjectHeader(payload);
            var current = ReadProject(tr, db);
            Compare(expected, current, "Параметры проекта изменились после открытия окна. Повторите команду.");
            string json = Serialize(payload);
            if (current.Present && current.Json == json) return;
            WriteRecord(tr, ProjectRoot(tr, db, true), ProjectRecordKey, json, null);
        }

        internal static void WriteZone(Transaction tr, Hatch hatch, string verifiedFingerprint,
            Record expected, Dictionary<string, object> payload)
        {
            if (payload == null || string.IsNullOrEmpty(verifiedFingerprint)) throw Fail("Нет проверенного снимка параметров зоны.");
            // Explicit editor rebind may replace an old generation only after
            // the caller verifies the new geometry and the user reviews it.
            // Owner/self-pointer and compare-and-swap still apply unchanged.
            var current = ReadZone(tr, hatch);
            Compare(expected, current, "Параметры зоны изменились после открытия окна. Повторите команду.");
            string json = Serialize(new Dictionary<string, object> {
                { "schema", ZoneSchema }, { "owner", hatch.Handle.ToString() },
                { "zone_fingerprint", verifiedFingerprint }, { "payload", payload }
            });
            if (current.Present && current.Json == json) return;
            if (!hatch.IsWriteEnabled) hatch.UpgradeOpen();
            if (hatch.ExtensionDictionary.IsNull) hatch.CreateExtensionDictionary();
            var ext = (DBDictionary)tr.GetObject(hatch.ExtensionDictionary, OpenMode.ForWrite);
            WriteRecord(tr, ext, ZoneKey, json, hatch.ObjectId);
        }

        internal static void ClearZone(Transaction tr, Hatch hatch, string verifiedFingerprint, Record expected)
        {
            if (string.IsNullOrEmpty(verifiedFingerprint)) throw Fail("Нет проверенного снимка параметров зоны.");
            var current = ReadZone(tr, hatch);
            Compare(expected, current, "Параметры зоны изменились после открытия окна. Повторите команду.");
            if (!current.Present) return;
            var ext = (DBDictionary)tr.GetObject(hatch.ExtensionDictionary, OpenMode.ForWrite);
            ObjectId id = ext.GetAt(ZoneKey); ext.Remove(ZoneKey);
            tr.GetObject(id, OpenMode.ForWrite).Erase();
        }

        internal static ProjectDependency Dependency(Record project)
        {
            if (project == null || !project.Present) throw Fail("Параметры проекта отсутствуют. Выполните ATFPROJECT.");
            CheckProjectHeader(project.Payload);
            return new ProjectDependency {
                project_id = Text(project.Payload, "project_id"), revision = Revision(project.Payload),
                content_digest = Text(project.Payload, "content_digest"), record_digest = project.RecordDigest
            };
        }

        internal static void VerifyProject(Transaction tr, Database db, ProjectDependency expected,
            ReadContext context = null)
        {
            if (expected == null) return; // Published unbound results remain independent.
            if (!GuidValue(expected.project_id) || expected.revision < 1 ||
                !Digest(expected.content_digest) || !Digest(expected.record_digest))
                throw Fail("Повреждена зависимость результата от параметров проекта.");
            var current = ReadProject(tr, db, context);
            if (!current.Present) throw Fail("Параметры проекта удалены. Выполните полное построение ATFRAME; прежняя ведомость устарела.");
            var observed = Dependency(current);
            if (observed.project_id != expected.project_id || observed.revision != expected.revision ||
                observed.content_digest != expected.content_digest || observed.record_digest != expected.record_digest)
                throw Fail("Параметры проекта изменились. Выполните полное построение ATFRAME; прежняя ведомость устарела.");
        }

        internal static void Compare(Record expected, Record current, string message)
        {
            if (expected == null || current == null || expected.Present != current.Present ||
                (expected.Present && expected.RecordDigest != current.RecordDigest)) throw Fail(message);
        }

        private static Record ReadRecord(Transaction tr, DBDictionary dictionary, string key, List<ObjectId> refs)
        {
            if (dictionary == null || !dictionary.Contains(key)) return new Record();
            var record = tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (record == null) throw Fail("Сохранённая запись параметров существует, но её данные повреждены.");
            var text = new StringBuilder();
            using (var data = record.Data)
            {
                if (data == null) throw Fail("Сохранённая запись параметров пуста; она не подменяется умолчаниями.");
                foreach (TypedValue value in data)
                {
                    if (value.TypeCode == (int)DxfCode.Text)
                    {
                        text.Append(Convert.ToString(value.Value, CultureInfo.InvariantCulture));
                        if (text.Length > MaxRecordCharacters) throw Fail("Запись параметров превышает допустимый размер.");
                    }
                    else if (refs != null && value.TypeCode == (int)DxfCode.SoftPointerId && value.Value is ObjectId)
                        refs.Add((ObjectId)value.Value);
                    else throw Fail("Запись параметров содержит неизвестные данные или ссылки.");
                }
            }
            if (text.Length == 0) throw Fail("Сохранённая запись параметров не содержит текста.");
            string json = text.ToString(); Dictionary<string, object> payload;
            try { payload = Serializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception) { throw Fail("Не читается сохранённая запись параметров. Автоматическая подмена запрещена."); }
            if (payload == null) throw Fail("Сохранённая запись параметров не является объектом.");
            return new Record { Present = true, Json = json, RecordDigest = Hash(json), Payload = payload };
        }

        private static DBDictionary ProjectRoot(Transaction tr, Database db, bool create)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, create ? OpenMode.ForWrite : OpenMode.ForRead);
            if (nod.Contains(ProjectRootKey))
            {
                var root = tr.GetObject(nod.GetAt(ProjectRootKey), create ? OpenMode.ForWrite : OpenMode.ForRead) as DBDictionary;
                if (root == null) throw Fail("Словарь параметров проекта повреждён.");
                return root;
            }
            if (!create) return null;
            var result = new DBDictionary(); nod.SetAt(ProjectRootKey, result); tr.AddNewlyCreatedDBObject(result, true); return result;
        }
        private static void WriteRecord(Transaction tr, DBDictionary dictionary, string key, string json, ObjectId? reference)
        {
            if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
            using (var data = new ResultBuffer())
            {
                for (int i = 0; i < json.Length; i += 250)
                    data.Add(new TypedValue((int)DxfCode.Text, json.Substring(i, Math.Min(250, json.Length - i))));
                if (reference.HasValue) data.Add(new TypedValue((int)DxfCode.SoftPointerId, reference.Value));
                Xrecord record;
                if (dictionary.Contains(key)) record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
                else { record = new Xrecord(); dictionary.SetAt(key, record); tr.AddNewlyCreatedDBObject(record, true); }
                record.Data = data; record.XlateReferences = true;
            }
        }
        private static void CheckOwner(Hatch hatch)
        { if (hatch == null || hatch.ObjectId.IsNull || hatch.IsErased) throw Fail("Нет действующей подтверждённой штриховки зоны."); }
        private static void CheckProjectHeader(Dictionary<string, object> payload)
        {
            if (payload == null || Text(payload, "schema") != "aframe_project_parameters/1" ||
                !GuidValue(Text(payload, "project_id")) || !Digest(Text(payload, "content_digest")) || Revision(payload) < 1)
                throw Fail("Неизвестная или повреждённая запись параметров проекта.");
        }
        private static long Revision(Dictionary<string, object> payload)
        {
            object value; if (payload == null || !payload.TryGetValue("revision", out value) ||
                (!(value is int) && !(value is long))) return 0;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        private static string Text(Dictionary<string, object> payload, string key)
        { object value; return payload != null && payload.TryGetValue(key, out value) ? value as string : null; }
        private static bool GuidValue(string value) { Guid parsed; return Guid.TryParseExact(value, "N", out parsed); }
        private static bool Digest(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private static string Serialize(object value)
        { string json = Serializer().Serialize(value); if (json.Length > MaxRecordCharacters) throw Fail("Запись параметров превышает допустимый размер."); return json; }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaxRecordCharacters, RecursionLimit = 100 }; }
        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text)); var result = new StringBuilder(64);
                foreach (byte value in bytes) result.Append(value.ToString("x2", CultureInfo.InvariantCulture)); return result.ToString();
            }
        }
        private static InvalidOperationException Fail(string message) { return new InvalidOperationException(message); }
    }
}
