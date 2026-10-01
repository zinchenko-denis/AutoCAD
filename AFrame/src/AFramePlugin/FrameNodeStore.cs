using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;

namespace AFramePlugin
{
    internal sealed class FrameNodeStored
    {
        internal FrameNodeSnapshot Snapshot;
        internal ObjectId OwnerId, ZoneId, DefinitionId;
        internal string OwnerHandle, ZoneHandle, ZoneFingerprint, RecordDigest, CadContentDigest, UnitDeclaration, DrawingDigest;
        internal UnitsValue DrawingUnits;
    }
    internal static class FrameNodeStore
    {
        internal const string Key = "AFRAME_NODE_GEOMETRY";
        private const string Schema = "aframe_node_cad/1";
        private const int MaxCharacters = 1024 * 1024;
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaxCharacters, RecursionLimit = 128 }; }
        internal static void Write(Transaction tr, BlockReference owner, Hatch zone, string zoneFingerprint,
            FrameNodeSnapshot snapshot, string unitDeclaration, UnitsValue drawingUnits, string drawingDigest)
        {
            if (owner == null || owner.ObjectId.IsNull || zone == null || zone.ObjectId.IsNull || zone.IsErased ||
                string.IsNullOrEmpty(zoneFingerprint) || snapshot == null)
                Fail("E_NODE_OWNER", "Не определены владелец схемы и проверенная зона.");
            CheckUnits(unitDeclaration, drawingUnits); HashText(drawingDigest);
            if (snapshot.Context.zone.owner_handle != zone.Handle.ToString()) Fail("E_NODE_OWNER", "Снимок относится к другому владельцу зоны.");
            if (!owner.ExtensionDictionary.IsNull)
            {
                var existing = tr.GetObject(owner.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
                if (existing == null || existing.Contains(Key)) Fail("E_NODE_OWNER", "Команда создания не заменяет существующий паспорт схемы.");
            }
            var payload = new Dictionary<string, object> { { "schema", Schema }, { "renderer", FrameNodeRenderer.Revision },
                { "owner", owner.Handle.ToString() }, { "definition", owner.BlockTableRecord.Handle.ToString() }, { "zone", zone.Handle.ToString() },
                { "zone_fingerprint", zoneFingerprint }, { "snapshot", snapshot.ToDict() }, { "unit_declaration", unitDeclaration },
                { "drawing_units", (int)drawingUnits }, { "drawing_digest", drawingDigest }, { "cad_content_digest", FrameNodeRenderer.CadContentDigest(tr, owner) } };
            string json = Json().Serialize(payload);
            if (json.Length > MaxCharacters) Fail("E_NODE_CAPACITY", "Паспорт схемы превышает техническую ёмкость записи.");
            var values = new List<TypedValue> { new TypedValue((int)DxfCode.SoftPointerId, owner.ObjectId),
                new TypedValue((int)DxfCode.SoftPointerId, owner.BlockTableRecord), new TypedValue((int)DxfCode.SoftPointerId, zone.ObjectId) };
            for (int i = 0; i < json.Length; i += 250) values.Add(new TypedValue((int)DxfCode.Text, json.Substring(i, Math.Min(250, json.Length - i))));
            owner.UpgradeOpen(); if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            var ext = (DBDictionary)tr.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite);
            using (var data = new ResultBuffer(values.ToArray()))
            {
                var record = new Xrecord { Data = data, XlateReferences = true }; ext.SetAt(Key, record); tr.AddNewlyCreatedDBObject(record, true);
            }
        }
        internal static FrameNodeStored Read(Transaction tr, BlockReference owner)
        {
            if (owner == null || owner.IsErased || owner.ExtensionDictionary.IsNull) Fail("E_NODE_NOT_OWNED", "У выбранного блока нет паспорта ATFNODE.");
            var ext = tr.GetObject(owner.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (ext == null || !ext.Contains(Key)) Fail("E_NODE_NOT_OWNED", "У выбранного блока нет паспорта ATFNODE.");
            var record = tr.GetObject(ext.GetAt(Key), OpenMode.ForRead) as Xrecord;
            if (record == null || !record.XlateReferences) Fail("E_NODE_RECORD", "Повреждена запись паспорта схемы или режим её ссылок.");
            var refs = new List<ObjectId>(); var text = new StringBuilder();
            using (var data = record.Data)
            {
                if (data == null) Fail("E_NODE_RECORD", "Паспорт схемы пуст.");
                foreach (TypedValue value in data)
                {
                    if (value.TypeCode == (int)DxfCode.SoftPointerId && value.Value is ObjectId) refs.Add((ObjectId)value.Value);
                    else if (value.TypeCode == (int)DxfCode.Text && value.Value is string) text.Append((string)value.Value);
                    else Fail("E_NODE_RECORD", "Неизвестный тип данных в паспорте схемы.");
                    if (text.Length > MaxCharacters || refs.Count > 3) Fail("E_NODE_CAPACITY", "Паспорт схемы превышает техническую ёмкость записи.");
                }
            }
            Dictionary<string, object> d;
            try { d = FrameNodeJson.Object(Json().DeserializeObject(text.ToString()), "schema", "renderer", "owner", "definition", "zone", "zone_fingerprint", "snapshot", "unit_declaration", "drawing_units", "drawing_digest", "cad_content_digest"); }
            catch (FrameNodeGeometryException) { throw; }
            catch (Exception) { Fail("E_NODE_RECORD", "Паспорт схемы не читается; отсутствие параметров не подставляется."); return null; }
            FrameNodeJson.Equal(d["schema"], Schema); FrameNodeJson.Equal(d["renderer"], FrameNodeRenderer.Revision);
            if (refs.Count != 3 || refs[0] != owner.ObjectId || refs[1] != owner.BlockTableRecord ||
                Text(d, "owner") != owner.Handle.ToString() || Text(d, "definition") != owner.BlockTableRecord.Handle.ToString())
                Fail("E_NODE_COPY", "Схема скопирована или её определение подменено. Создайте новый экземпляр ATFNODE.");
            if (refs[2].IsNull || refs[2].IsErased) Fail("E_NODE_SOURCE_STALE", "Исходная зона схемы удалена.");
            var zone = tr.GetObject(refs[2], OpenMode.ForRead) as Hatch;
            if (zone == null || zone.Handle.ToString() != Text(d, "zone")) Fail("E_NODE_SOURCE_STALE", "Исходная ссылка зоны повреждена.");
            if (!(d["drawing_units"] is int)) Fail("E_NODE_RECORD", "Некорректные единицы паспорта.");
            var units = (UnitsValue)(int)d["drawing_units"]; CheckUnits(Text(d, "unit_declaration"), units);
            string expectedBody = HashText(Text(d, "cad_content_digest")), drawingDigest = HashText(Text(d, "drawing_digest"));
            var snapshot = FrameNodeSnapshot.FromDict(d["snapshot"]);
            if (snapshot.Context.zone.owner_handle != zone.Handle.ToString()) Fail("E_NODE_OWNER", "Владелец снимка не соответствует зоне.");
            var drawing = FrameNodeDrawingBuilder.Build(snapshot.Result, snapshot.CaptionLines());
            if (drawing.Digest != drawingDigest) Fail("E_NODE_BODY_CHANGED", "Состав размерной схемы не соответствует её снимку.");
            if (FrameNodeRenderer.CadContentDigest(tr, owner) != expectedBody) Fail("E_NODE_BODY_CHANGED", "Геометрия, подписи или оформление схемы изменены. Создайте новую схему; прежняя не подтверждена.");
            return new FrameNodeStored { Snapshot = snapshot, OwnerId = owner.ObjectId, DefinitionId = refs[1], ZoneId = refs[2],
                OwnerHandle = owner.Handle.ToString(), ZoneHandle = zone.Handle.ToString(), ZoneFingerprint = Text(d, "zone_fingerprint"),
                UnitDeclaration = Text(d, "unit_declaration"), DrawingUnits = units, DrawingDigest = drawingDigest,
                CadContentDigest = expectedBody, RecordDigest = RawHash(text.ToString()) };
        }
        private static void CheckUnits(string declaration, UnitsValue units)
        {
            if (!((units == UnitsValue.Millimeters && declaration == "database_mm") || (units == UnitsValue.Undefined && declaration == "unitless_explicit_mm")))
                Fail("E_NODE_UNITS", "Паспорт не содержит согласованное решение об единицах миллиметров.");
        }
        private static string Text(Dictionary<string, object> d, string key)
        { var s = d[key] as string; if (string.IsNullOrEmpty(s)) Fail("E_NODE_RECORD", "Пустое поле паспорта: " + key + "."); return s; }
        private static string HashText(string hash)
        {
            if (hash == null || hash.Length != 64) Fail("E_NODE_RECORD", "Неверный digest паспорта.");
            foreach (char c in hash) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) Fail("E_NODE_RECORD", "Неверный digest паспорта.");
            return hash;
        }
        private static string RawHash(string text)
        { using (var sha = SHA256.Create()) { var b = sha.ComputeHash(Encoding.UTF8.GetBytes(text)); var s = new StringBuilder(64); foreach (byte x in b) s.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return s.ToString(); } }
        private static void Fail(string code, string message) { throw new FrameNodeGeometryException(code, message); }
    }
}
