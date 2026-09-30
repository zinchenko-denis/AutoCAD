from pathlib import Path
p=Path(__file__).with_name('QuantityCadDoubles.cs')
s=p.read_text()
assert 'class CadCounters' not in s
s=s.replace('namespace Autodesk.AutoCAD.Geometry', '''internal static class CadCounters
{
    public static long ObjectReads, ObjectWrites, DictionaryContains, DictionaryGet, DictionarySet,
        XrecordReads, XrecordWrites, BufferEnumerations, BufferValues, BufferTextChars,
        PointsRead, BulgesRead, HatchLoopsRead, TypedValuesAllocated, ObjectsCreated, ModelSpaceVisits;
    public static void Reset() {
        ObjectReads=ObjectWrites=DictionaryContains=DictionaryGet=DictionarySet=XrecordReads=XrecordWrites=0;
        BufferEnumerations=BufferValues=BufferTextChars=PointsRead=BulgesRead=HatchLoopsRead=TypedValuesAllocated=ObjectsCreated=ModelSpaceVisits=0;
    }
    public static Dictionary<string,object> Snapshot() {
        return new Dictionary<string,object> {
            {"get_object_read",ObjectReads},{"get_object_write",ObjectWrites},
            {"dictionary_contains",DictionaryContains},{"dictionary_get",DictionaryGet},{"dictionary_set",DictionarySet},
            {"xrecord_data_get",XrecordReads},{"xrecord_data_set",XrecordWrites},
            {"buffer_enumerations",BufferEnumerations},{"buffer_values",BufferValues},{"buffer_text_chars",BufferTextChars},
            {"points_read",PointsRead},{"bulges_read",BulgesRead},{"hatch_loops_read",HatchLoopsRead},
            {"typed_values_allocated",TypedValuesAllocated},{"objects_created",ObjectsCreated},{"modelspace_visits",ModelSpaceVisits}
        };
    }
}
namespace Autodesk.AutoCAD.Geometry''',1)
s=s.replace('objects.Add(obj.Handle.Value, obj);','objects.Add(obj.Handle.Value, obj); CadCounters.ObjectsCreated++;')
s=s.replace('if (id.IsNull || id.IsErased) throw new InvalidOperationException("Missing/erased object");','if (mode == OpenMode.ForRead) CadCounters.ObjectReads++; else CadCounters.ObjectWrites++;\n            if (id.IsNull || id.IsErased) throw new InvalidOperationException("Missing/erased object");')
s=s.replace('return Items.ContainsKey(key);','CadCounters.DictionaryContains++; return Items.ContainsKey(key);').replace('return Items[key];','CadCounters.DictionaryGet++; return Items[key];').replace('Items[key] = Database.Add(value).ObjectId;','CadCounters.DictionarySet++; Items[key] = Database.Add(value).ObjectId;')
s=s.replace('TypeCode=typeCode; Value=value;','CadCounters.TypedValuesAllocated++; TypeCode=typeCode; Value=value;')
s=s.replace('public IEnumerator<TypedValue> GetEnumerator() { return values.GetEnumerator(); }', '''public IEnumerator<TypedValue> GetEnumerator() {
            CadCounters.BufferEnumerations++;
            foreach (var item in values) {
                CadCounters.BufferValues++;
                if (item.TypeCode == (int)DxfCode.Text && item.Value is string) CadCounters.BufferTextChars+=((string)item.Value).Length;
                yield return item;
            }
        }''')
s=s.replace('public class Xrecord : DBObject { public ResultBuffer Data; public bool XlateReferences; }', '''public class Xrecord : DBObject { private ResultBuffer data;
        public ResultBuffer Data { get { CadCounters.XrecordReads++; return data; } set { CadCounters.XrecordWrites++; data=value; } }
        public bool XlateReferences; }''')
s=s.replace('return Points[i]; }','CadCounters.PointsRead++; return Points[i]; }')
s=s.replace('return new Point3d(Points[i].X, Points[i].Y, Elevation);','CadCounters.PointsRead++; return new Point3d(Points[i].X, Points[i].Y, Elevation);')
s=s.replace('return Bulges[i];','CadCounters.BulgesRead++; return Bulges[i];')
s=s.replace('return Loops[i];','CadCounters.HatchLoopsRead++; return Loops[i];')
s=s.replace('public IEnumerator<ObjectId> GetEnumerator() { return Children.GetEnumerator(); }','''public IEnumerator<ObjectId> GetEnumerator() {
            foreach(var child in Children) { if (Name == ModelSpace) CadCounters.ModelSpaceVisits++; yield return child; }
        }''')
assert 'class CadCounters' in s
p.write_text(s)
print('instrumented',p,len(s))
