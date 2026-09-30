// Independent synthetic cases around the actual manual mapper, CAD reader,
// persistence and existing zone/layout guards. No private drawing geometry.
// Doubles do not implement AutoCAD transactions, dynamic evaluation or Undo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

internal static class ManualCadCheck
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static readonly List<Dictionary<string, object>> Results = new List<Dictionary<string, object>>();
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Test(string name, Action run)
    {
        try { run(); Results.Add(new Dictionary<string, object> { { "name", name }, { "status", "PASS" } }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { Results.Add(new Dictionary<string, object> { { "name", name }, { "status", "FAIL" }, { "detail", e.ToString() } }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    private static void Refuses(Action run, string message)
    { bool stopped=false; try { run(); } catch (InvalidOperationException) { stopped=true; } Require(stopped,message); }
    private static Polyline Rect(Database db, double x, double y, double w=620.25, double h=410.5)
    {
        var p=db.Add(new Polyline());
        p.AddVertexAt(0,new Point2d(x,y),0,0,0);p.AddVertexAt(1,new Point2d(x+w,y),0,0,0);
        p.AddVertexAt(2,new Point2d(x+w,y+h),0,0,0);p.AddVertexAt(3,new Point2d(x,y+h),0,0,0);return p;
    }
    private sealed class BlockFixture
    {
        public readonly Database Db; public readonly Transaction Tr;
        public readonly BlockTableRecord Definition; public readonly BlockReference Insert;
        public readonly Polyline Boundary;
        public BlockFixture(Database db=null, bool cassette=true)
        {
            Db=db??new Database();Tr=new Transaction(Db);
            Definition=Db.Add(new BlockTableRecord { Name=cassette?"Synthetic cassette":"Synthetic clamp" });
            Boundary=Rect(Db,0,0);Definition.Children.Add(Boundary.ObjectId);
            if(cassette) for(int i=0;i<12;i++) {
                var hatch=Db.Add(new Hatch { Associative=false,Area=620.25*410.5 });
                var loop=new HatchLoop { LoopType=HatchLoopTypes.External };
                foreach(var p in Boundary.Points) loop.Polyline.Add(new BulgeVertex(p,0));
                hatch.Loops.Add(loop);Definition.Children.Add(hatch.ObjectId);
            }
            Definition.Children.Add(Db.Add(new AttributeDefinition { Tag="МАРКИРОВКА",TextString="",Position=new Point3d(9000,9000,0),Height=130 }).ObjectId);
            Definition.Children.Add(Db.Add(new AttributeDefinition { Tag="ЗАХВАТКА",TextString="",Position=new Point3d(-9000,-9000,0),Height=50 }).ObjectId);
            Insert=Db.Add(new BlockReference { BlockTableRecord=Definition.ObjectId,DynamicBlockTableRecord=Definition.ObjectId,
                IsDynamicBlock=cassette,Position=new Point3d(2000,2000,0) });
            Insert.AttributeCollection.Add(Db.Add(new AttributeReference { Tag="МАРКИРОВКА",TextString="Synthetic mark",Position=new Point3d(9500,9500,0),Height=130 }).ObjectId);
            Insert.AttributeCollection.Add(Db.Add(new AttributeReference { Tag="ЗАХВАТКА",TextString="Zone display label" }).ObjectId);
            if(cassette) {
                Insert.DynamicBlockReferencePropertyCollection.Add(new DynamicBlockReferenceProperty { PropertyName="ширина",Value=620.25,UnitsType=DynamicBlockReferencePropertyUnitsType.Distance });
                Insert.DynamicBlockReferencePropertyCollection.Add(new DynamicBlockReferenceProperty { PropertyName="высота",Value=410.5,UnitsType=DynamicBlockReferencePropertyUnitsType.Distance });
                Insert.DynamicBlockReferencePropertyCollection.Add(new DynamicBlockReferenceProperty { PropertyName="Видимость",Value="Synthetic state",UnitsType=DynamicBlockReferencePropertyUnitsType.NoUnits });
            }
        }
    }
    private static ManualQuantityObservation Observe(Transaction tr, Entity e)
    { return ManualQuantityGeometry.Extract(tr,e,new ManualQuantityGeometry.Cache()); }
    private static ManualQuantityRule Rule(ManualQuantityObservation observation,string kind="cladding",string role="cladding",string adapter="boundary",string axis=null)
    {
        return new ManualQuantityRule { rule_id="synthetic-rule",revision="1",kind=kind,role=role,adapter=adapter,axis=axis,
            one_physical_part=true,geometry_unit="mm",sample_key=observation.sample_key,
            fields=new Dictionary<string,ManualQuantityField> {
                {"type",new ManualQuantityField {source="literal",value="Synthetic mapped type"}},
                {"mark",new ManualQuantityField {source="literal",value="Synthetic explicit mark"}}
            } };
    }
    private static ManualQuantityEvaluation Evaluate(Transaction tr, Entity e,string kind="cladding",string role="cladding",string adapter="boundary",string axis=null)
    { var observation=Observe(tr,e);return ManualQuantitiesCore.Evaluate(Rule(observation,kind,role,adapter,axis),observation,"Z-test"); }
    private static void Accepted(ManualQuantityEvaluation result)
    { Require(result.status=="accepted" && result.element!=null,"not accepted: "+result.status+" "+result.reason); }
    private static void Rejected(ManualQuantityEvaluation result)
    { Require(result.status!="accepted" && result.element==null && !string.IsNullOrWhiteSpace(result.reason),"unsupported geometry silently accepted"); }
    private static void GeometryCases()
    {
        Test("cassette_one_outline_twelve_hatches_two_attributes_is_one_physical_piece",()=>{
            var f=new BlockFixture();var o=Observe(f.Tr,f.Insert);var result=ManualQuantitiesCore.Evaluate(Rule(o),o,"Z-test");Accepted(result);
            Require(result.element.cad_entities.Count==1,"auxiliary hatches/attributes were counted as parts");
            Require(Math.Abs(result.element.area_mm2.Value-620.25*410.5)<1e-7,"attribute/hatch bounds polluted material area");
            Require(o.attributes["МАРКИРОВКА"]=="Synthetic mark","actual attribute not read");
            Require(Convert.ToDouble(o.dynamic_properties["ширина"])==620.25,"actual dynamic property not read");
        });
        foreach(double angle in new[]{Math.PI/2,Math.PI/7}) Test("rotated_cassette_keeps_true_area_"+angle,()=>{
            var f=new BlockFixture();f.Insert.Rotation=angle;var result=Evaluate(f.Tr,f.Insert);Accepted(result);
            Require(Math.Abs(result.element.area_mm2.Value-620.25*410.5)<1e-6,"axis-aligned world bbox replaced physical face");
        });
        Test("mirrored_nonuniform_cassette_uses_actual_transformed_face",()=>{
            var f=new BlockFixture();f.Insert.ScaleFactors=new Scale3d(-2,0.5,1);f.Insert.Rotation=Math.PI/5;
            var result=Evaluate(f.Tr,f.Insert);Accepted(result);Require(Math.Abs(result.element.area_mm2.Value-620.25*410.5)<1e-6,"scale determinant ignored");
        });
        Test("block_origin_is_applied_before_scale_rotation",()=>{
            var f=new BlockFixture();f.Definition.Origin=new Point3d(100,50,0);f.Insert.Rotation=Math.PI/2;
            var o=Observe(f.Tr,f.Insert);Require(o.boundary_points.Any(p=>Math.Abs(p[0]-2050)<1e-8&&Math.Abs(p[1]-1900)<1e-8),"block base offset lost");
        });
        Test("reading_dynamic_properties_performs_no_setter",()=>{
            var f=new BlockFixture();int before=f.Insert.DynamicBlockReferencePropertyCollection.Sum(p=>p.ValueWrites);
            Observe(f.Tr,f.Insert);Require(before==f.Insert.DynamicBlockReferencePropertyCollection.Sum(p=>p.ValueWrites),"reader changed dynamic state");
        });
        Test("two_visible_boundaries_do_not_become_one_bbox_piece",()=>{
            var f=new BlockFixture();f.Definition.Children.Add(Rect(f.Db,40,50,80,90).ObjectId);Rejected(Evaluate(f.Tr,f.Insert));
        });
        Test("hidden_auxiliary_outline_does_not_replace_active_boundary",()=>{
            var f=new BlockFixture();var extra=Rect(f.Db,-900,-900,3000,3000);extra.Visible=false;f.Definition.Children.Add(extra.ObjectId);
            var r=Evaluate(f.Tr,f.Insert);Accepted(r);Require(Math.Abs(r.element.area_mm2.Value-620.25*410.5)<1e-7,"invisible graphics entered active shape");
        });
        foreach(string adapter in new[]{"boundary","linear","symbol"}) Test("nested_assembly_refused_"+adapter,()=>{
            var f=new BlockFixture();var inner=f.Db.Add(new BlockTableRecord { Name="nested assembly" });
            f.Definition.Children.Add(f.Db.Add(new BlockReference {BlockTableRecord=inner.ObjectId,DynamicBlockTableRecord=inner.ObjectId}).ObjectId);
            Rejected(Evaluate(f.Tr,f.Insert,adapter=="boundary"?"cladding":"frame",adapter=="boundary"?"cladding":adapter=="linear"?"rail":"clamp",adapter,adapter=="linear"?"X":null));
        });
        Test("explicit_dynamic_width_height_match_rotated_scaled_geometry",()=>{
            var f=new BlockFixture();f.Insert.Rotation=Math.PI/7;f.Insert.ScaleFactors=new Scale3d(-2,0.5,1);
            var o=Observe(f.Tr,f.Insert);var rule=Rule(o);
            rule.fields["width"]=new ManualQuantityField {source="dynamic",name="ширина"};
            rule.fields["height"]=new ManualQuantityField {source="dynamic",name="высота"};
            Accepted(ManualQuantitiesCore.Evaluate(rule,o,"Z-test"));
        });
        Test("wrong_dynamic_unit_cannot_confirm_linear_dimension",()=>{
            var f=new BlockFixture();f.Insert.DynamicBlockReferencePropertyCollection[0].UnitsType=DynamicBlockReferencePropertyUnitsType.Angular;
            var o=Observe(f.Tr,f.Insert);var rule=Rule(o);rule.fields["width"]=new ManualQuantityField {source="dynamic",name="ширина"};
            Rejected(ManualQuantitiesCore.Evaluate(rule,o,"Z-test"));
        });
        Test("nominal_dynamic_value_does_not_override_actual_shape",()=>{
            var f=new BlockFixture();f.Insert.DynamicBlockReferencePropertyCollection[0].Value=900.0;
            var o=Observe(f.Tr,f.Insert);var rule=Rule(o);var measured=ManualQuantitiesCore.Evaluate(rule,o,"Z-test");Accepted(measured);
            Require(Math.Abs(measured.element.area_mm2.Value-620.25*410.5)<1e-7,"nominal property replaced geometry");
            rule.fields["width"]=new ManualQuantityField {source="dynamic",name="ширина"};Rejected(ManualQuantitiesCore.Evaluate(rule,o,"Z-test"));
        });
        Test("missing_dimension_binding_is_not_guessed_by_largest_double",()=>{
            var f=new BlockFixture();var o=Observe(f.Tr,f.Insert);var rule=Rule(o);
            rule.fields["width"]=new ManualQuantityField {source="dynamic",name="несуществующее свойство"};
            Rejected(ManualQuantitiesCore.Evaluate(rule,o,"Z-test"));
        });
        Test("unknown_attribute_remains_unknown_with_explicit_issue",()=>{
            var f=new BlockFixture();var o=Observe(f.Tr,f.Insert);var rule=Rule(o);
            rule.fields["mark"]=new ManualQuantityField {source="attribute",name="MISSING_MARK"};
            var r=ManualQuantitiesCore.Evaluate(rule,o,"Z-test");Accepted(r);
            Require(r.element.mark==null&&r.issues.Any(i=>i.code=="MQ_FIELD_UNKNOWN"),"missing mark was fabricated or hidden");
        });
        Test("duplicate_attribute_tags_are_ambiguous",()=>{
            var f=new BlockFixture();f.Insert.AttributeCollection.Add(f.Db.Add(new AttributeReference {Tag="маркировка",TextString="Other"}).ObjectId);
            Rejected(Evaluate(f.Tr,f.Insert));
        });
        Test("duplicate_dynamic_property_names_are_ambiguous",()=>{
            var f=new BlockFixture();f.Insert.DynamicBlockReferencePropertyCollection.Add(new DynamicBlockReferenceProperty {PropertyName="ШИРИНА",Value=620.25});
            Rejected(Evaluate(f.Tr,f.Insert));
        });
        foreach(double scale in new[]{0.0,double.NaN,double.PositiveInfinity}) Test("invalid_scale_refused_"+scale,()=>{
            var f=new BlockFixture();f.Insert.ScaleFactors=new Scale3d(scale,1,1);Rejected(Evaluate(f.Tr,f.Insert));
        });
        Test("nonplanar_insert_refused",()=>{var f=new BlockFixture();f.Insert.Normal=new Vector3d(0,1,0);Rejected(Evaluate(f.Tr,f.Insert));});
        Test("thick_boundary_refused",()=>{var f=new BlockFixture();f.Boundary.Thickness=1;Rejected(Evaluate(f.Tr,f.Insert));});
        Test("straight_world_line_exact_length",()=>{
            var db=new Database();var tr=new Transaction(db);var line=db.Add(new Line {StartPoint=new Point3d(10,20,0),EndPoint=new Point3d(13,24,0)});
            var r=Evaluate(tr,line,"frame","rail","linear");Accepted(r);Require(r.element.length_mm==5,"line length is not endpoint distance");
        });
        Test("zero_length_line_refused",()=>{var db=new Database();var tr=new Transaction(db);Rejected(Evaluate(tr,db.Add(new Line()),"frame","rail","linear"));});
        Test("symbol_counts_one_without_invented_length_or_area",()=>{
            var f=new BlockFixture(cassette:false);var r=Evaluate(f.Tr,f.Insert,"frame","clamp","symbol");Accepted(r);
            Require(r.element.length_mm==null&&r.element.area_mm2==null,"symbol bbox became physical dimensions");
        });
        Test("raw_polygon_with_arc_refused",()=>{var db=new Database();var tr=new Transaction(db);var p=Rect(db,100,100);p.Bulges[0]=0.2;Rejected(Evaluate(tr,p));});
        Test("raw_open_boundary_refused",()=>{var db=new Database();var tr=new Transaction(db);var p=Rect(db,100,100);p.Closed=false;Rejected(Evaluate(tr,p));});
    }
    private static QuantityStoreCheck.Fixture Zone() { var z=new QuantityStoreCheck.Fixture("single");z.Fresh();return z; }
    private static FacadeQuantityStore.ManualImportPreview Preview(QuantityStoreCheck.Fixture z,ManualQuantityRule rule,params Entity[] pieces)
    { return FacadeQuantityStore.PreviewImport(z.Tr,z.Db,z.Hatch.ObjectId,rule,pieces.Select(p=>p.ObjectId)); }
    private static void Import(QuantityStoreCheck.Fixture z,ManualQuantityRule rule,params Entity[] pieces)
    { var preview=Preview(z,rule,pieces);Require(FacadeQuantityStore.ApplyImport(z.Tr,z.Db,preview)==pieces.Select(p=>p.ObjectId).Distinct().Count(),"import count mismatch"); }
    private static QuantitySelection Read(QuantityStoreCheck.Fixture z,bool frame=false,params Entity[] selected)
    { var ids=(selected.Length==0?new Entity[]{z.Hatch}:selected).Select(e=>e.ObjectId);return frame?FacadeQuantityStore.ReadFrame(z.Tr,z.Db,ids):FacadeQuantityStore.ReadCladding(z.Tr,z.Db,ids); }
    private static List<QuantityElement> Physical(QuantitySelection r,int count)
    { Require(r.Ok,"selection refused: "+r.Reason);var elements=r.Reports.SelectMany(x=>x.elements).ToList();Require(elements.Count==count,"physical count "+elements.Count+", expected "+count);Require(r.Rows.rows.Where(x=>x.basis=="installed").Sum(x=>x.quantity)==count,"aggregated physical count mismatch");return elements; }
    private static void Refused(QuantitySelection r)
    { Require(!r.Ok&&!string.IsNullOrWhiteSpace(r.Reason)&&r.Reports.Count==0,"invalid selection published stale reports"); }
    private static DBDictionary Ext(Transaction tr,Entity e) { return (DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForWrite); }
    private static Xrecord Record(Transaction tr,Entity e,string key) { return (Xrecord)tr.GetObject(Ext(tr,e).GetAt(key),OpenMode.ForWrite); }
    private static DBDictionary Root(QuantityStoreCheck.Fixture z)
    { var nod=(DBDictionary)z.Tr.GetObject(z.Db.NamedObjectsDictionaryId,OpenMode.ForRead);return (DBDictionary)z.Tr.GetObject(nod.GetAt("AFACADE_QUANTITIES"),OpenMode.ForWrite); }
    private static DBDictionary Indexes(QuantityStoreCheck.Fixture z) { return (DBDictionary)z.Tr.GetObject(Root(z).GetAt("MANUAL_INDEXES"),OpenMode.ForWrite); }
    private static void CopyManual(QuantityStoreCheck.Fixture z,Entity source,Entity target)
    {
        var old=Record(z.Tr,source,FacadeQuantityStore.ManualKey);target.CreateExtensionDictionary();
        Ext(z.Tr,target).SetAt(FacadeQuantityStore.ManualKey,new Xrecord { Data=new ResultBuffer(old.Data.Select(v=>new TypedValue(v.TypeCode,v.Value)).ToArray()),XlateReferences=true });
    }
    private static void Put(Transaction tr,Entity e,string key,string json)
    { if(e.ExtensionDictionary.IsNull)e.CreateExtensionDictionary();var ext=Ext(tr,e);var data=new ResultBuffer(new TypedValue((int)DxfCode.Text,json));if(ext.Contains(key))((Xrecord)tr.GetObject(ext.GetAt(key),OpenMode.ForWrite)).Data=data;else ext.SetAt(key,new Xrecord{Data=data}); }
    private static void ChangeRecordText(Xrecord record,Func<string,string> change)
    { var values=record.Data.ToArray();string text=string.Concat(values.Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value));var altered=new List<TypedValue>{new TypedValue((int)DxfCode.Text,change(text))};altered.AddRange(values.Where(v=>v.TypeCode!=(int)DxfCode.Text).Select(v=>new TypedValue(v.TypeCode,v.Value)));record.Data=new ResultBuffer(altered.ToArray()); }
    private sealed class Generated
    {
        internal readonly QuantityStoreCheck.Fixture Z;internal readonly Polyline Piece;internal readonly QuantityReport Report;
        internal Generated(bool persist=true, bool markOnly=false) {
            var type=typeof(QuantityStoreCheck).GetNestedType("QuantityFixture",BindingFlags.NonPublic);
            var instance=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{"single","ATTILE",persist&&!markOnly},null);
            Z=(QuantityStoreCheck.Fixture)type.GetField("Zone").GetValue(instance);Piece=(Polyline)type.GetField("Piece").GetValue(instance);Report=(QuantityReport)type.GetField("Report").GetValue(instance);
            if(markOnly) { Report.source_revisions["owner_zone:"+Z.Mark.Handle]= (string)Z.Data["zone_id"];FacadeQuantityStore.Store(Z.Tr,Z.Db,new Entity[]{Z.Mark},"ATTILE",Report); }
        }
    }
    private static void StoreCases()
    {
        Test("manual_cladding_roundtrip_owner_mark_piece_selection_equivalent",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);Import(z,Rule(Observe(z.Tr,p)),p);
            var a=Read(z);var e=Physical(a,1)[0];var b=Read(z,false,p);var c=Read(z,false,z.Mark);
            Physical(b,1);Physical(c,1);Require(a.Fingerprint==b.Fingerprint&&b.Fingerprint==c.Fingerprint,"carrier choice changed same-zone bill");
            Require(e.product_id==null&&e.origin.StartsWith("manual:")&&a.Rows.source_engineering_coverage.Contains("manual_geometry_and_user_mapping_only"),"manual import fabricated engineering confirmation");
            Require(Math.Abs(e.area_mm2.Value-620.25*410.5)<1e-6,"persisted area changed");
        });
        Test("manual_frame_line_and_symbol_are_independent_physical_roles",()=>{
            var z=Zone();var line=z.Db.Add(new Line{StartPoint=new Point3d(0,0,0),EndPoint=new Point3d(3000,4000,0)});
            var rule=Rule(Observe(z.Tr,line),"frame","rail","linear");Import(z,rule,line);
            var block=new BlockFixture(z.Db,false);var sr=Rule(Observe(z.Tr,block.Insert),"frame","clamp","symbol");sr.rule_id="symbol-rule";Import(z,sr,block.Insert);
            var e=Physical(Read(z,true),2);Require(e.Single(x=>x.role=="rail").length_mm==5000&&e.Single(x=>x.role=="clamp").length_mm==null,"manual quantities guessed symbol length");
        });
        Test("reimport_preserves_identity_and_deduplicates_selected_handles",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,p));Import(z,rule,p,p);string id=Physical(Read(z),1)[0].element_id;
            Import(z,rule,p);Require(Physical(Read(z),1)[0].element_id==id,"reimport became new physical piece");
        });
        Test("same_geometry_two_objects_are_both_counted_with_duplicate_warning",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,100,100);Import(z,Rule(Observe(z.Tr,a)),a,b);
            var r=Read(z);var e=Physical(r,2);Require(e.Select(x=>x.element_id).Distinct().Count()==2&&r.Warnings.Any(x=>x.Contains("совпад")),"possible duplicate silently deleted or concealed");
        });
        Test("generated_and_manual_same_zone_add_counts_without_overriding",()=>{
            var g=new Generated();var p=Rect(g.Z.Db,1000,1000);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);
            var r=Read(g.Z);var e=Physical(r,2);Require(e.Any(x=>x.origin.StartsWith("generated:"))&&e.Any(x=>x.origin.StartsWith("manual:")),"one contribution replaced another");
            Physical(Read(g.Z,false,p),2);Physical(Read(g.Z,false,g.Piece),2);
        });
        Test("manual_generated_same_geometry_warns_and_counts_both",()=>{
            var g=new Generated();var p=Rect(g.Z.Db,0,0,600,600);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);var r=Read(g.Z);Physical(r,2);
            Require(r.Warnings.Any(x=>x.Contains("совпад")),"manual/generated duplicate was not exposed");
        });
        Test("legacy_generated_layout_missing_passport_is_not_bypassed_by_import",()=>{
            var z=Zone();Put(z.Tr,z.Hatch,"ATTILE","{}");var p=Rect(z.Db,100,100);Import(z,Rule(Observe(z.Tr,p)),p);Refused(Read(z));Refused(Read(z,false,p));
        });
        Test("missing_generated_owner_passport_cannot_be_hidden_by_manual",()=>{
            var g=new Generated();var p=Rect(g.Z.Db,1000,1000);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);Ext(g.Z.Tr,g.Z.Hatch).Remove(FacadeQuantityStore.OwnerKey);Refused(Read(g.Z));
        });
        Test("unregistered_selected_object_is_visible_and_not_silently_counted",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var other=Rect(z.Db,1800,100);Import(z,Rule(Observe(z.Tr,p)),p);var r=Read(z,false,p,other);Physical(r,1);
            Require(r.Unaccounted.Count==1&&r.Unaccounted[0].handle==other.Handle.ToString()&&!string.IsNullOrWhiteSpace(r.Unaccounted[0].reason),"unknown object omitted from inventory");
        });
        Test("unknown_only_selection_has_no_fabricated_zero_bill",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var r=Read(z,false,p);Refused(r);Require(r.Unaccounted.Count==1,"unknown-only inventory lost");
        });
        Test("copied_manual_stamp_is_not_counted_until_explicit_adoption",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,a));Import(z,rule,a);var original=Physical(Read(z),1)[0].element_id;
            var b=Rect(z.Db,1800,100);CopyManual(z,a,b);var r=Read(z);Physical(r,1);Require(r.Warnings.Any(x=>x.Contains("копия")),"uncounted metadata copy hidden");Refused(Read(z,false,b));
            Import(z,rule,b);var e=Physical(Read(z),2);Require(e.Select(x=>x.element_id).Distinct().Count()==2&&e.Any(x=>x.element_id==original),"adoption merged original/copy identity");
        });
        Test("detach_copied_binding_does_not_remove_original",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);Import(z,Rule(Observe(z.Tr,a)),a);var b=Rect(z.Db,1800,100);CopyManual(z,a,b);
            Require(FacadeQuantityStore.RemoveManualSelected(z.Tr,z.Db,new[]{b.ObjectId})==1,"copy not detached");Physical(Read(z),1);Require(!a.IsErased&&!b.IsErased&&FacadeQuantityStore.HasManual(z.Tr,a)&&!FacadeQuantityStore.HasManual(z.Tr,b),"detach modified physical/original object");
        });
        Test("explicit_remove_selected_keeps_geometry_and_other_members",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,1800,100);Import(z,Rule(Observe(z.Tr,a)),a,b);
            Require(FacadeQuantityStore.RemoveManualSelected(z.Tr,z.Db,new[]{a.ObjectId,a.ObjectId})==1,"same object detached twice");Physical(Read(z),1);Require(!a.IsErased&&!FacadeQuantityStore.HasManual(z.Tr,a),"detach erased physical geometry");
        });
        Test("remove_last_registered_piece_preserves_known_empty_zone",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);Import(z,Rule(Observe(z.Tr,a)),a);FacadeQuantityStore.RemoveManualSelected(z.Tr,z.Db,new[]{a.ObjectId});Physical(Read(z),0);
        });
        Test("preview_rule_is_frozen_from_original_form_instance",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,p));var approved=Preview(z,rule,p);rule.fields["mark"].value="Edited after preview";
            FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved);Require(Physical(Read(z),1)[0].mark=="Synthetic explicit mark","form mutable rule changed approved snapshot");
        });
        Test("altered_approved_rule_refuses_before_any_binding_write",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var approved=Preview(z,Rule(Observe(z.Tr,p)),p);approved.Preview.rule.fields["mark"].value="changed";
            Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved),"changed rule accepted");Require(!FacadeQuantityStore.HasManual(z.Tr,p)&&!FacadeQuantityStore.HasManualIndex(z.Tr,z.Db,z.Hatch,"cladding"),"failed preflight wrote a partial ledger");
        });
        foreach(string mutation in new[]{"shape","attribute","dynamic","units","visible","rotation","scale"}) Test("mutation_after_preview_refuses_"+mutation,()=>{
            var z=Zone();var f=new BlockFixture(z.Db);var approved=Preview(z,Rule(Observe(z.Tr,f.Insert)),f.Insert);
            if(mutation=="shape")f.Boundary.Points[0]=new Point2d(10,10);
            if(mutation=="attribute")((AttributeReference)z.Tr.GetObject(f.Insert.AttributeCollection[0],OpenMode.ForWrite)).TextString="changed";
            if(mutation=="dynamic")f.Insert.DynamicBlockReferencePropertyCollection[0].Value=900.0;
            if(mutation=="units")f.Insert.DynamicBlockReferencePropertyCollection[0].UnitsType=DynamicBlockReferencePropertyUnitsType.Angular;
            if(mutation=="visible")f.Insert.Visible=false;
            if(mutation=="rotation")f.Insert.Rotation=0.25;
            if(mutation=="scale")f.Insert.ScaleFactors=new Scale3d(2,1,1);
            Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved),"changed candidate accepted");Require(!FacadeQuantityStore.HasManual(z.Tr,f.Insert),"failure left binding");
        });
        Test("changing_rejected_selected_object_invalidates_whole_preview",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var bad=z.Db.Add(new Line{EndPoint=new Point3d(100,0,0)});var approved=Preview(z,Rule(Observe(z.Tr,a)),a,bad);
            Require(approved.Preview.accepted_count==1&&approved.Preview.items.Count==2,"fixture inventory wrong");bad.EndPoint=new Point3d(200,0,0);
            Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved),"rejected-object mutation was ignored");Require(!FacadeQuantityStore.HasManual(z.Tr,a),"partial bindings written before snapshot check");
        });
        Test("mixed_preview_only_persists_accepted_objects",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var bad=z.Db.Add(new Line{EndPoint=new Point3d(100,0,0)});var approved=Preview(z,Rule(Observe(z.Tr,a)),a,bad);
            Require(approved.Preview.items.Count==2&&approved.Preview.accepted_count==1,"mixed inventory dropped rejection");Require(FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved)==1,"rejected object registered");Physical(Read(z),1);Require(!FacadeQuantityStore.HasManual(z.Tr,bad),"rejected candidate got passport");
        });
        Test("zero_accepted_import_refuses_without_writing_index",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var bad=z.Db.Add(new Line{EndPoint=new Point3d(100,0,0)});var approved=Preview(z,Rule(Observe(z.Tr,a)),bad);
            Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved),"empty accepted import persisted");Require(!FacadeQuantityStore.HasManualIndex(z.Tr,z.Db,z.Hatch,"cladding"),"empty rejected import created zero claim");
        });
        Test("changed_rule_id_content_requires_new_version",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,p));Import(z,rule,p);rule.fields["mark"].value="Changed mapping";
            Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,Preview(z,rule,p)),"immutable rule overwritten");Require(Physical(Read(z),1)[0].mark=="Synthetic explicit mark","old mapping modified by failed import");
            rule.rule_id="synthetic-rule-v2";rule.revision="2";Import(z,rule,p);Require(Physical(Read(z),1)[0].mark=="Changed mapping","new mapping version not applied");
        });
        Test("stale_unselected_member_remains_refused_after_partial_reimport",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,1800,100);var rule=Rule(Observe(z.Tr,a));Import(z,rule,a,b);b.Points[0]=new Point2d(1801,101);Refused(Read(z));Import(z,rule,a);Refused(Read(z));
        });
        Test("zone_geometry_change_invalidates_preview_and_saved_manual_scope",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,a));Import(z,rule,a);var approved=Preview(z,rule,a);z.Outers[0].Points[0]=new Point2d(20,20);
            Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved),"changed zone accepted after preview");Refused(Read(z));
        });
        Test("registered_deleted_member_refuses_and_clear_repairs_remaining_bindings",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,1800,100);Import(z,Rule(Observe(z.Tr,a)),a,b);a.Erase();Refused(Read(z));
            Require(FacadeQuantityStore.ClearManualZone(z.Tr,z.Db,z.Hatch.ObjectId,"cladding")==1,"clear did not repair surviving member");Require(!b.IsErased&&!FacadeQuantityStore.HasManual(z.Tr,b)&&!FacadeQuantityStore.HasManualIndex(z.Tr,z.Db,z.Hatch,"cladding"),"repair erased drawing or retained index");
        });
        foreach(string corruption in new[]{"binding_removed","binding_json","binding_ref","index_removed","anchor_removed","index_refs","rule_removed","rule_changed"}) Test("persisted_corruption_refused_"+corruption,()=>{
            var z=Zone();var p=Rect(z.Db,100,100);Import(z,Rule(Observe(z.Tr,p)),p);
            if(corruption=="binding_removed")Ext(z.Tr,p).Remove(FacadeQuantityStore.ManualKey);
            if(corruption=="binding_json")ChangeRecordText(Record(z.Tr,p,FacadeQuantityStore.ManualKey),s=>s.Replace("Synthetic explicit mark","Tampered"));
            if(corruption=="binding_ref") { var xr=Record(z.Tr,p,FacadeQuantityStore.ManualKey);xr.Data=new ResultBuffer(xr.Data.Select(v=>v.TypeCode==(int)DxfCode.SoftPointerId?new TypedValue(v.TypeCode,z.Mark.ObjectId):v).ToArray()); }
            if(corruption=="index_removed")Indexes(z).Remove("cladding_"+z.Hatch.Handle);
            if(corruption=="anchor_removed")Ext(z.Tr,z.Hatch).Remove(FacadeQuantityStore.ManualAnchorPrefix+"cladding");
            if(corruption=="index_refs") { var xr=(Xrecord)z.Tr.GetObject(Indexes(z).GetAt("cladding_"+z.Hatch.Handle),OpenMode.ForWrite);xr.Data=new ResultBuffer(xr.Data.Where(v=>v.TypeCode!=(int)DxfCode.SoftPointerId).ToArray()); }
            if(corruption=="rule_removed")Root(z).Remove("MANUAL_RULE_synthetic-rule");
            if(corruption=="rule_changed")ChangeRecordText((Xrecord)z.Tr.GetObject(Root(z).GetAt("MANUAL_RULE_synthetic-rule"),OpenMode.ForWrite),s=>s.Replace("Synthetic explicit mark","Changed"));
            Refused(Read(z));
        });
        Test("losing_index_and_anchor_does_not_hide_manual_orphans",()=>{
            var g=new Generated();var p=Rect(g.Z.Db,1000,1000);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);Indexes(g.Z).Remove("cladding_"+g.Z.Hatch.Handle);Ext(g.Z.Tr,g.Z.Hatch).Remove(FacadeQuantityStore.ManualAnchorPrefix+"cladding");Refused(Read(g.Z));
            Require(FacadeQuantityStore.ClearManualZone(g.Z.Tr,g.Z.Db,g.Z.Hatch.ObjectId,"cladding")==1,"orphan clear unavailable");Physical(Read(g.Z),1);
        });
        foreach(string key in new[]{"AFACADE_QUANTITY_OWNER","AFACADE_QUANTITY_ELEMENT","AFACADE_FRAME_QUANTITY_OWNER","AFACADE_FRAME_QUANTITY_ELEMENT","ATFZONE","ATFZONE_GEOMETRY","ATTILE","ATCLAD","ATFRAME","ATFRAME_RAIL","ATLAYOUT_CURRENT","ATTILE_GEOMETRY","ATCLAD_GEOMETRY"}) Test("generated_marker_cannot_be_reclassified_manual_"+key,()=>{
            var z=Zone();var p=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,p));Put(z.Tr,p,key,"{}");var approved=Preview(z,rule,p);
            Require(approved.Preview.accepted_count==0,"generated marker ignored");Refuses(()=>FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved),"generated object adopted as manual");
        });
        Test("loss_of_whole_manual_directory_and_anchor_cannot_hide_registered_items",()=>{
            var g=new Generated();var p=Rect(g.Z.Db,1000,1000);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);
            Root(g.Z).Remove("MANUAL_INDEXES");Ext(g.Z.Tr,g.Z.Hatch).Remove(FacadeQuantityStore.ManualAnchorPrefix+"cladding");Refused(Read(g.Z));
        });
        Test("legacy_mark_only_generated_owner_manual_selection_refuses_precisely",()=>{
            var g=new Generated(markOnly:true);
            var p=Rect(g.Z.Db,1000,1000);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);
            var mark=Read(g.Z,false,g.Z.Mark);Physical(mark,2);var piece=Read(g.Z,false,p);Refused(piece);Require(piece.Reason.Contains("паспорт")||piece.Reason.Contains("марку"),"legacy refusal lacks recovery guidance");
        });
        Test("same_transaction_visible_definition_edit_invalidates_stored_snapshot",()=>{
            var z=Zone();var f=new BlockFixture(z.Db);var auxiliary=Rect(z.Db,40,50,80,90);auxiliary.Visible=false;f.Definition.Children.Add(auxiliary.ObjectId);
            Import(z,Rule(Observe(z.Tr,f.Insert)),f.Insert);Physical(Read(z),1);auxiliary.Visible=true;Refused(Read(z));
        });
        Test("erased_zone_selected_detach_repairs_only_requested_members",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,1800,100);Import(z,Rule(Observe(z.Tr,a)),a,b);string key="cladding_"+z.Hatch.Handle;z.Hatch.Erase();
            Require(FacadeQuantityStore.RemoveManualSelected(z.Tr,z.Db,new[]{a.ObjectId})==1,"deleted-zone detach failed");Require(!FacadeQuantityStore.HasManual(z.Tr,a)&&FacadeQuantityStore.HasManual(z.Tr,b),"detach removed unselected binding");
            var xr=(Xrecord)z.Tr.GetObject(Indexes(z).GetAt(key),OpenMode.ForRead);var index=Json.Deserialize<FacadeQuantityStore.ManualIndex>(string.Concat(xr.Data.Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value)));
            Require(index.members.Count==1&&index.members[0].handle==b.Handle.ToString(),"repair lost remaining index member");
            Require(FacadeQuantityStore.RemoveManualSelected(z.Tr,z.Db,new[]{b.ObjectId})==1&&!Indexes(z).Contains(key),"empty orphan index not removed");Require(!a.IsErased&&!b.IsErased,"detach erased drawings");
        });
        Test("erased_zone_and_missing_index_detach_is_selected_only",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,1800,100);Import(z,Rule(Observe(z.Tr,a)),a,b);Indexes(z).Remove("cladding_"+z.Hatch.Handle);z.Hatch.Erase();
            Require(FacadeQuantityStore.RemoveManualSelected(z.Tr,z.Db,new[]{a.ObjectId})==1,"missing-zone/index repair refused");Require(!FacadeQuantityStore.HasManual(z.Tr,a)&&FacadeQuantityStore.HasManual(z.Tr,b),"explicit selected detach affected another binding");
        });
        Test("current_generated_frame_and_manual_rail_share_canonical_hatch_scope",()=>{
            var type=typeof(QuantityStoreCheck).GetNestedType("FrameFixture",BindingFlags.NonPublic);var instance=Activator.CreateInstance(type,new object[]{true});
            var cladding=type.GetField("Cladding").GetValue(instance);var z=(QuantityStoreCheck.Fixture)cladding.GetType().GetField("Zone").GetValue(cladding);
            var line=z.Db.Add(new Line{StartPoint=new Point3d(300,0,0),EndPoint=new Point3d(300,1000,0)});Import(z,Rule(Observe(z.Tr,line),"frame","rail","linear"),line);
            var owner=Read(z,true);var mark=Read(z,true,z.Mark);var manual=Read(z,true,line);Physical(owner,2);Physical(mark,2);Physical(manual,2);
            Require(owner.Fingerprint==mark.Fingerprint&&mark.Fingerprint==manual.Fingerprint,"current generated frame selected via different carriers changes scope");
            Require(owner.Warnings.Any(x=>x.Contains("совпад")),"same manual/generated rail geometry not diagnosed");
        });
        Test("manual_duplicates_across_batches_are_detected_without_erasure",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var b=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,a));Import(z,rule,a);Import(z,rule,b);var read=Read(z);Physical(read,2);Require(read.Warnings.Any(x=>x.Contains("совпад")),"separate import batches hid duplicate");
        });
        Test("manual_duplicates_across_selected_zones_are_visible",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,a));Import(z,rule,a);var other=new QuantityStoreCheck.Fixture("single",z.Db,"OtherZone");other.Fresh();
            var b=Rect(z.Db,100,100);Import(other,rule,b);var read=Read(z,false,z.Hatch,other.Hatch);Physical(read,2);Require(read.SelectedZoneIds.Count==2&&read.Warnings.Any(x=>x.Contains("совпад")),"cross-zone duplicate hidden or merged");
        });
        Test("preview_cancellation_leaves_no_manual_state",()=>{
            var z=Zone();var p=Rect(z.Db,100,100);bool cancelled=false;
            try { FacadeQuantityStore.PreviewImport(z.Tr,z.Db,z.Hatch.ObjectId,Rule(Observe(z.Tr,p)),new[]{p.ObjectId},()=>{throw new OperationCanceledException();}); }
            catch(OperationCanceledException){cancelled=true;}
            Require(cancelled&&!FacadeQuantityStore.HasManual(z.Tr,p)&&!FacadeQuantityStore.HasManualIndex(z.Tr,z.Db,z.Hatch,"cladding"),"cancelled preview wrote metadata");
        });
        Test("apply_cancellation_during_revalidation_preserves_existing_index",()=>{
            var z=Zone();var old=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,old));Import(z,rule,old);string prior=Read(z).Fingerprint;
            var added=Rect(z.Db,1800,100);var approved=Preview(z,rule,added);int callbacks=0;bool cancelled=false;
            CadCounters.Reset();try { FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved,()=>{if(++callbacks==2)throw new OperationCanceledException();}); }
            catch(OperationCanceledException){cancelled=true;}
            Require(cancelled&&CadCounters.XrecordWrites==0,"prewrite cancellation wrote a partial ledger");Require(!FacadeQuantityStore.HasManual(z.Tr,added)&&Read(z).Fingerprint==prior,"cancelled import changed existing quantity snapshot");
        });
        Test("unrelated_stale_generated_owner_does_not_expand_selected_manual_scope",()=>{
            var g=new Generated();var p=Rect(g.Z.Db,1000,1000);Import(g.Z,Rule(Observe(g.Z.Tr,p)),p);
            var other=new QuantityStoreCheck.Fixture("single",g.Z.Db,"OtherZone");other.Fresh();other.Outers[0].Points[0]=new Point2d(20,20);
            string stamp=string.Concat(Record(g.Z.Tr,g.Z.Mark,FacadeQuantityStore.OwnerKey).Data.Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value));
            var value=Json.Deserialize<Dictionary<string,object>>(stamp);value["run_id"]="unrelated-legacy-run";value["owner"]=other.Mark.Handle.ToString();value["zone_ids"]=new[]{"OtherZone"};
            Put(g.Z.Tr,other.Mark,FacadeQuantityStore.OwnerKey,Json.Serialize(value));Physical(Read(g.Z),2);Physical(Read(g.Z,false,p),2);
        });
        Test("moving_assignment_to_another_zone_requires_explicit_detach",()=>{
            var z=Zone();var a=Rect(z.Db,100,100);var rule=Rule(Observe(z.Tr,a));Import(z,rule,a);
            var other=new QuantityStoreCheck.Fixture("single",z.Db,"OtherZone");other.Fresh();Refuses(()=>FacadeQuantityStore.ApplyImport(other.Tr,other.Db,Preview(other,rule,a)),"one object assigned to two zones");Physical(Read(z),1);
        });
    }
    private static Dictionary<string,object> Measure(string name,Action action)
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long before=GC.GetTotalMemory(false);CadCounters.Reset();
        var timer=System.Diagnostics.Stopwatch.StartNew();action();timer.Stop();var result=CadCounters.Snapshot();result["phase"]=name;
        result["seconds"]=timer.Elapsed.TotalSeconds;result["managed_bytes_before"]=before;result["managed_bytes_after"]=GC.GetTotalMemory(false);
        using(var process=System.Diagnostics.Process.GetCurrentProcess()) { result["working_set_bytes"]=process.WorkingSet64;result["peak_working_set_bytes"]=process.PeakWorkingSet64;result["resident_memory_basis"]="Process"; }
        if(File.Exists("/proc/self/status")) {
            foreach(string line in File.ReadAllLines("/proc/self/status")) {
                string[] words=line.Split(new[]{' ','\t'},StringSplitOptions.RemoveEmptyEntries);
                if(words.Length>1&&words[0]=="VmRSS:")result["working_set_bytes"]=long.Parse(words[1])*1024;
                if(words.Length>1&&words[0]=="VmHWM:")result["peak_working_set_bytes"]=long.Parse(words[1])*1024;
            }
            result["resident_memory_basis"]="/proc/self/status VmRSS/VmHWM";
        }
        if(Convert.ToInt64(result["working_set_bytes"])==0)result["working_set_bytes"]=null;
        if(Convert.ToInt64(result["peak_working_set_bytes"])==0)result["peak_working_set_bytes"]=null;
        return result;
    }
    private static int Performance(int n,string mode,string output)
    {
        var phases=new List<Dictionary<string,object>>();bool mixed=mode=="mixed";bool generated=mode=="generated";
        var g=(mixed||generated)?new Generated(persist:false):null;var z=g==null?Zone():g.Z;var pieces=new List<Entity>();
        if(g!=null) {
            var template=g.Report.elements[0];g.Report.elements.Clear();
            for(int i=0;i<n;i++) {
                Polyline p=i==0?g.Piece:Rect(z.Db,i*900.0,0,600,600);var e=Json.Deserialize<QuantityElement>(Json.Serialize(template));
                e.element_id="generated-perf-"+i;e.cad_entities=new List<QuantityCadEntity>{FacadeQuantityStore.CaptureEntity(z.Tr,p,"outer")};g.Report.elements.Add(e);
            }
            foreach(var e in new Entity[]{z.Hatch,z.Mark})g.Report.source_revisions["owner_zone:"+e.Handle]=(string)z.Data["zone_id"];
            FacadeQuantityStoreStore(z,g.Report);
        }
        if(!generated) for(int i=0;i<n;i++)pieces.Add(Rect(z.Db,i*900.0,0,600,600));
        FacadeQuantityStore.ManualImportPreview approved=null;
        if(!generated) {
            var rule=Rule(Observe(z.Tr,pieces[0]));
            phases.Add(Measure("preview",()=>{approved=Preview(z,rule,pieces.ToArray());Require(approved.Preview.accepted_count==n,"preview lost physical objects");}));
            phases.Add(Measure("apply",()=>Require(FacadeQuantityStore.ApplyImport(z.Tr,z.Db,approved)==n,"import lost physical objects")));
        }
        QuantitySelection owner=null;int expected=mixed?2*n:n;
        phases.Add(Measure("read_owner",()=>{owner=Read(z);Physical(owner,expected);if(mixed)Require(owner.Warnings.Any(x=>x.Contains("совпад")),"matching generated/manual geometry not diagnosed");}));
        if(!generated) phases.Add(Measure("read_all_manual",()=>{var all=Read(z,false,pieces.ToArray());Physical(all,expected);Require(owner.Fingerprint==all.Fingerprint,"owner and all-selected fingerprints differ");}));
        var violations=new List<string>();int totalPhysical=expected;
        foreach(var phase in phases) {
            string name=(string)phase["phase"];long reads=Convert.ToInt64(phase["get_object_read"]),points=Convert.ToInt64(phase["points_read"]);
            if(reads>120L*totalPhysical+3000)violations.Add(name+": CAD reads exceed linear bound");
            if(points>100L*totalPhysical+3000)violations.Add(name+": point reads exceed linear bound");
            if(Convert.ToInt64(phase["xrecord_data_get"])>15L*totalPhysical+1000)violations.Add(name+": Xrecord reads exceed linear bound");
            if(Convert.ToInt64(phase["hatch_loops_read"])>30)violations.Add(name+": repeated zone verification");
            if(name.StartsWith("read_")&&Convert.ToInt64(phase["modelspace_visits"])>totalPhysical+10)violations.Add(name+": more than one modelspace scan");
            if(!name.StartsWith("read_")&&Convert.ToInt64(phase["modelspace_visits"])!=0)violations.Add(name+": import scans modelspace");
            if(name.StartsWith("read_")&&Convert.ToInt64(phase["xrecord_data_set"])!=0)violations.Add(name+": read writes metadata");
        }
        var report=new {status=violations.Count==0?"PASS":"FAIL",mode,count=n,expected_physical=expected,phases,violations,
            limits="Operation bounds apply to this synthetic planar-polyline fixture. Time and process memory are observations, not native AutoCAD performance or CI time thresholds."};
        File.WriteAllText(output,Json.Serialize(report));Console.WriteLine("Manual CAD performance "+mode+" "+n+": "+report.status);return violations.Count==0?0:1;
    }
    private static void FacadeQuantityStoreStore(QuantityStoreCheck.Fixture z,QuantityReport report)
    { FacadeQuantityStore.Store(z.Tr,z.Db,new Entity[]{z.Hatch,z.Mark},"ATTILE",report); }

    public static int Main(string[] args)
    {
        if((args.Length!=4&&args.Length!=7)||args[0]!="--fixtures"||args[2]!="--report") return 2;
        typeof(QuantityStoreCheck).GetField("Fixtures",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,Json.DeserializeObject(File.ReadAllText(args[1])));
        if(args.Length==7&&args[4]=="--performance")return Performance(int.Parse(args[5]),args[6],args[3]);
        GeometryCases();StoreCases();int failed=Results.Count(r=>(string)r["status"]!="PASS");
        File.WriteAllText(args[3],Json.Serialize(new { total=Results.Count,failed,cases=Results }));
        Console.WriteLine("Manual CAD: "+(Results.Count-failed)+"/"+Results.Count);return failed==0?0:1;
    }
}
