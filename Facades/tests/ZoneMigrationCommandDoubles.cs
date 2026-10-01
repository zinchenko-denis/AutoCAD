// Restricted host seams for the actual command. In-memory rollback is explicit;
// native AutoCAD transactions/Undo and dialogs still require the live pilot.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace Autodesk.AutoCAD.DatabaseServices
{
    public partial class Database
    {
        public string Filename;
        public BlockTableRecord Model;
        internal IEnumerable<DBObject> AllObjects { get { return objects.Values.ToArray(); } }
        public TransactionManager TransactionManager { get { return new TransactionManager(this); } }
        public int Commits;
        public bool FailCommit;
        public Action OnFailedCommit;
    }
    public sealed class TransactionManager
    {
        private readonly Database db;
        public TransactionManager(Database value) { db = value; }
        public Transaction StartTransaction() { return new Transaction(db); }
    }
    public sealed class Transaction : IDisposable
    {
        private readonly Database db;
        private readonly List<Action> restore = new List<Action>();
        private bool committed;
        public Transaction(Database value)
        {
            db = value;
            foreach (var item in db.AllObjects)
            {
                var entity = item as Entity;
                if (entity != null) { var old = entity.ExtensionDictionary; restore.Add(() => entity.ExtensionDictionary = old); }
                var mark = item as MText;
                if (mark != null) { var old = mark.Contents; restore.Add(() => mark.Contents = old); }
                var record = item as Xrecord;
                if (record != null) { var old = record.Data; restore.Add(() => record.Data = old); }
                var dict = item as DBDictionary;
                if (dict != null)
                {
                    var old = new Dictionary<string, ObjectId>(dict.Items);
                    restore.Add(() => { dict.Items.Clear(); foreach (var pair in old) dict.Items.Add(pair.Key, pair.Value); });
                }
            }
        }
        public DBObject GetObject(ObjectId id, OpenMode mode)
        { if (id.IsNull || id.IsErased) throw new InvalidOperationException("Missing object"); return id.Item; }
        public void AddNewlyCreatedDBObject(DBObject value, bool add) { if (add) db.Add(value); }
        public void Commit()
        {
            if (db.FailCommit)
            {
                if (db.OnFailedCommit != null) db.OnFailedCommit();
                throw new InvalidOperationException("simulated commit failure");
            }
            committed = true; db.Commits++;
        }
        public void Dispose() { if (!committed) foreach (var action in restore) action(); }
    }
    public sealed class BlockTableRecord : DBObject, IEnumerable<ObjectId>
    {
        public readonly List<ObjectId> Ids = new List<ObjectId>();
        public IEnumerator<ObjectId> GetEnumerator() { return Ids.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
    public static class SymbolUtilityServices
    { public static ObjectId GetBlockModelSpaceId(Database db) { return db.Model.ObjectId; } }
}
namespace Autodesk.AutoCAD.EditorInput
{
    public enum PromptStatus { OK, Cancel, None }
    public sealed class PromptEntityResult { public PromptStatus Status; public ObjectId ObjectId; }
    public sealed class PromptResult { public PromptStatus Status; public string StringResult; }
    public sealed class PromptKeywordOptions { public PromptKeywordOptions(string message, string keywords) { } }
    public sealed class Editor
    {
        public PromptEntityResult Entity;
        public PromptResult Keywords;
        public Action OnKeywords;
        public string Messages = "";
        public PromptEntityResult GetEntity(string message) { return Entity; }
        public PromptResult GetKeywords(PromptKeywordOptions options) { if (OnKeywords != null) OnKeywords(); return Keywords; }
        public void WriteMessage(string message) { Messages += message; }
    }
}
namespace Autodesk.AutoCAD.ApplicationServices
{
    public sealed class Document
    {
        public Database Database;
        public readonly Autodesk.AutoCAD.EditorInput.Editor Editor = new Autodesk.AutoCAD.EditorInput.Editor();
        public Action BeforeLock;
        public IDisposable LockDocument() { if (BeforeLock != null) BeforeLock(); return new Empty(); }
        private sealed class Empty : IDisposable { public void Dispose() { } }
    }
    public sealed class Documents { public Document MdiActiveDocument; }
    public static class Application { public static readonly Documents DocumentManager = new Documents(); }
}
namespace Autodesk.AutoCAD.Runtime
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class CommandClassAttribute : Attribute { public CommandClassAttribute(Type value) { } }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CommandMethodAttribute : Attribute { public CommandMethodAttribute(string name, CommandFlags flags) { } }
    public enum CommandFlags { Modal = 1 }
}
