// Minimal persistence doubles for the actual project-parameter store.
// Rollback below is an explicit in-memory model. It does not verify AutoCAD
// Undo, COPY remapping, document locking, persistence or Windows host behavior.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

internal static class ProjectCadCounters
{
    internal static int ObjectReads, ObjectWrites, NodReads, DictionaryReads,
        DictionaryWrites, DataReads, DataWrites, BufferDisposals, ModelSpaceReads;
    internal static int FailAfterMutations = -1;
    internal static void Reset()
    {
        ObjectReads = ObjectWrites = NodReads = DictionaryReads = DictionaryWrites = 0;
        DataReads = DataWrites = BufferDisposals = ModelSpaceReads = 0;
        FailAfterMutations = -1;
    }
    internal static void Mutation()
    {
        if (FailAfterMutations == 0) throw new InvalidOperationException("Injected storage write failure");
        if (FailAfterMutations > 0) FailAfterMutations--;
    }
    internal static Dictionary<string, object> Snapshot()
    {
        return new Dictionary<string, object> {
            { "object_reads", ObjectReads }, { "object_writes", ObjectWrites },
            { "nod_reads", NodReads }, { "dictionary_reads", DictionaryReads },
            { "dictionary_writes", DictionaryWrites }, { "xrecord_data_reads", DataReads },
            { "xrecord_data_writes", DataWrites }, { "buffer_disposals", BufferDisposals },
            { "modelspace_reads", ModelSpaceReads }
        };
    }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    public enum OpenMode { ForRead, ForWrite }
    public enum DxfCode { Text = 1, SoftPointerId = 330, HardPointerId = 340, Int16 = 70 }
    public struct Handle
    {
        public long Value;
        public Handle(long value) { Value = value; }
        public override string ToString() { return Value.ToString("X"); }
    }
    public struct ObjectId : IEquatable<ObjectId>
    {
        internal DBObject Item;
        public bool IsNull { get { return Item == null; } }
        public bool IsErased { get { return Item != null && Item.IsErased; } }
        public bool IsValid { get { return Item != null && !Item.Removed; } }
        public Handle Handle { get { return Item == null ? new Handle(0) : Item.Handle; } }
        public Database Database { get { return Item == null ? null : Item.Database; } }
        public static ObjectId Null { get { return new ObjectId(); } }
        public bool Equals(ObjectId other) { return Object.ReferenceEquals(Item, other.Item); }
        public override bool Equals(object other) { return other is ObjectId && Equals((ObjectId)other); }
        public override int GetHashCode() { return Item == null ? 0 : Item.GetHashCode(); }
        public static bool operator ==(ObjectId first, ObjectId second) { return first.Equals(second); }
        public static bool operator !=(ObjectId first, ObjectId second) { return !first.Equals(second); }
    }
    public class DBObject : IDisposable
    {
        public ObjectId ObjectId;
        public Handle Handle;
        public Database Database;
        public bool IsErased, IsWriteEnabled;
        internal bool Removed;
        public void UpgradeOpen() { IsWriteEnabled = true; ProjectCadCounters.ObjectWrites++; }
        public void Erase() { ProjectCadCounters.Mutation(); IsErased = true; }
        public void Erase(bool value) { ProjectCadCounters.Mutation(); IsErased = value; }
        public void Dispose() { }
    }
    public class Entity : DBObject
    {
        public ObjectId ExtensionDictionary;
        public void CreateExtensionDictionary()
        {
            ProjectCadCounters.Mutation();
            ExtensionDictionary = Database.Add(new DBDictionary()).ObjectId;
        }
    }
    public sealed class Hatch : Entity { }
    public sealed class MText : Entity { }
    public sealed class Polyline : Entity { }
    public sealed class Database
    {
        internal readonly Dictionary<long, DBObject> Objects = new Dictionary<long, DBObject>();
        internal long Next;
        private readonly ObjectId nod;
        public Guid FingerprintGuid = Guid.NewGuid();
        public Database() { nod = Add(new DBDictionary()).ObjectId; }
        public ObjectId NamedObjectsDictionaryId { get { ProjectCadCounters.NodReads++; return nod; } }
        public ObjectId BlockTableId { get {
            ProjectCadCounters.ModelSpaceReads++;
            throw new InvalidOperationException("A parameter operation must not access ModelSpace");
        } }
        public T Add<T>(T value) where T : DBObject
        {
            if (!value.ObjectId.IsNull) return value;
            value.Database = this; value.Handle = new Handle(++Next);
            value.ObjectId = new ObjectId { Item = value }; Objects.Add(Next, value);
            return value;
        }
        public bool TryGetObjectId(Handle handle, out ObjectId id)
        {
            DBObject value;
            if (Objects.TryGetValue(handle.Value, out value) && !value.Removed)
            { id = value.ObjectId; return true; }
            id = ObjectId.Null; return false;
        }
        public ObjectId GetObjectId(bool createIfNotFound, Handle handle, int xrefId)
        {
            ObjectId id; if (TryGetObjectId(handle, out id)) return id;
            throw new InvalidOperationException("Unknown object handle");
        }
    }
    public sealed class Transaction : IDisposable
    {
        private sealed class Saved
        {
            internal DBObject Value;
            internal bool Erased;
            internal ObjectId Extension;
            internal Dictionary<string, ObjectId> Items;
            internal TypedValue[] Values;
            internal bool Xlate;
        }
        private readonly Database db;
        private readonly long next;
        private readonly List<Saved> saved = new List<Saved>();
        private bool committed, disposed;
        public Transaction(Database database)
        {
            db = database; next = db.Next;
            foreach (var item in db.Objects.Values) {
                var entity = item as Entity; var dictionary = item as DBDictionary; var record = item as Xrecord;
                saved.Add(new Saved { Value = item, Erased = item.IsErased,
                    Extension = entity == null ? ObjectId.Null : entity.ExtensionDictionary,
                    Items = dictionary == null ? null : new Dictionary<string, ObjectId>(dictionary.Items),
                    Values = record == null || record.Values == null ? null : record.Values.ToArray(),
                    Xlate = record != null && record.XlateReferences });
            }
        }
        public DBObject GetObject(ObjectId id, OpenMode mode) { return GetObject(id, mode, false); }
        public DBObject GetObject(ObjectId id, OpenMode mode, bool openErased)
        {
            if (mode == OpenMode.ForRead) ProjectCadCounters.ObjectReads++; else ProjectCadCounters.ObjectWrites++;
            if (id.IsNull || !id.IsValid || (!openErased && id.IsErased)) throw new InvalidOperationException("Absent or erased object");
            if (id.Item.Database != db) throw new InvalidOperationException("Foreign database object");
            if (mode == OpenMode.ForWrite) id.Item.IsWriteEnabled = true;
            return id.Item;
        }
        public void AddNewlyCreatedDBObject(DBObject value, bool add) { if (add) db.Add(value); }
        public void Commit() { committed = true; }
        public void Abort() { Rollback(); disposed = true; }
        private void Rollback()
        {
            foreach (var key in db.Objects.Keys.Where(key => key > next).ToArray()) {
                db.Objects[key].Removed = true; db.Objects.Remove(key);
            }
            db.Next = next;
            foreach (var item in saved) {
                item.Value.IsErased = item.Erased; item.Value.IsWriteEnabled = false;
                var entity = item.Value as Entity; if (entity != null) entity.ExtensionDictionary = item.Extension;
                var dictionary = item.Value as DBDictionary;
                if (dictionary != null) { dictionary.Items.Clear(); foreach (var pair in item.Items) dictionary.Items.Add(pair.Key, pair.Value); }
                var record = item.Value as Xrecord;
                if (record != null) { record.Values = item.Values == null ? null : item.Values.ToArray(); record.XlateReferences = item.Xlate; }
            }
        }
        public void Dispose() { if (disposed) return; if (!committed) Rollback(); disposed = true; }
    }
    public sealed class DBDictionary : DBObject
    {
        public readonly Dictionary<string, ObjectId> Items = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        public bool Contains(string key) { ProjectCadCounters.DictionaryReads++; return Items.ContainsKey(key); }
        public ObjectId GetAt(string key) { ProjectCadCounters.DictionaryReads++; return Items[key]; }
        public ObjectId SetAt(string key, DBObject value)
        {
            ProjectCadCounters.Mutation(); ProjectCadCounters.DictionaryWrites++;
            Items[key] = Database.Add(value).ObjectId; return value.ObjectId;
        }
        public ObjectId Remove(string key)
        {
            ProjectCadCounters.Mutation(); ProjectCadCounters.DictionaryWrites++;
            var previous = Items[key]; Items.Remove(key); return previous;
        }
    }
    public sealed class TypedValue
    {
        public int TypeCode; public object Value;
        public TypedValue(int code, object value) { TypeCode = code; Value = value; }
    }
    public sealed class ResultBuffer : IEnumerable<TypedValue>, IDisposable
    {
        private readonly List<TypedValue> values;
        internal bool Disposed;
        public ResultBuffer(params TypedValue[] initial) { values = new List<TypedValue>(initial); }
        public void Add(TypedValue value) { if (Disposed) throw new ObjectDisposedException("ResultBuffer"); values.Add(value); }
        public TypedValue[] AsArray() { if (Disposed) throw new ObjectDisposedException("ResultBuffer"); return values.ToArray(); }
        public IEnumerator<TypedValue> GetEnumerator() { if (Disposed) throw new ObjectDisposedException("ResultBuffer"); return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        public void Dispose() { if (!Disposed) ProjectCadCounters.BufferDisposals++; Disposed = true; }
    }
    public sealed class Xrecord : DBObject
    {
        internal TypedValue[] Values;
        public bool XlateReferences;
        public ResultBuffer Data {
            get { ProjectCadCounters.DataReads++; return Values == null ? null : new ResultBuffer(Values); }
            set { ProjectCadCounters.Mutation(); ProjectCadCounters.DataWrites++; Values = value == null ? null : value.AsArray(); }
        }
    }
}
