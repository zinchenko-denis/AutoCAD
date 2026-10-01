using System; using AFramePlugin; using Autodesk.AutoCAD.DatabaseServices;
internal static class NodeFreshnessPredicate {
internal sealed class Current { internal ObjectId ZoneId; internal string ZoneHandle,Fingerprint; internal FrameParameterResolution Resolution; }
internal sealed class Saved { internal ObjectId ZoneId; internal string ZoneHandle,ZoneFingerprint; internal FrameNodeSnapshot Snapshot; }
static void Fail(string code,string reason) { throw new InvalidOperationException(code+": "+reason); }
internal static bool Check(Current current,Saved saved) { try {
                if (current.ZoneId != saved.ZoneId || current.ZoneHandle != saved.ZoneHandle || current.Fingerprint != saved.ZoneFingerprint ||
                    !FrameParameterContext.Same(current.Resolution.Context, saved.Snapshot.Context) ||
                    !FrameSolutionSelection.Same(current.Resolution.Selection, saved.Snapshot.Selection))
                    Fail("E_NODE_SOURCE_STALE", "Источники схемы изменились. Старый снимок сохранён; создайте новую схему ATFNODE.");
return true; } catch(InvalidOperationException) { return false; } } }
