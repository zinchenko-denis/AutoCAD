#!/usr/bin/env python3
"""Public synthetic facade tutorial assets, generated from actual current engines.
No AutoCAD screenshots. DXF references deliberately have no plugin ownership tags.
"""
from pathlib import Path
import json, sys, hashlib, subprocess, textwrap, re
from datetime import datetime, timezone
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon, Rectangle, PathPatch
from matplotlib.path import Path as MplPath
from shapely.geometry import Polygon as GeoPolygon
from shapely.geometry.polygon import orient
import ezdxf
ROOT=Path(__file__).resolve().parents[2]; HERE=Path(__file__).parent; AS=HERE/'assets'; AS.mkdir(exist_ok=True)
for mod in ('Facades','AClad','AFrame'): sys.path.insert(0,str(ROOT/mod/'engine'))
import facades_engine, clad_engine, frame_engine
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':10,'figure.facecolor':'white','axes.spines.top':False,'axes.spines.right':False})
COL={'wall':'#f4efe6','tile':'#dcba86','cut':'#ac7352','hole':'#e1edf3','rail':'#344d60','shina':'#148a91','start':'#bd612b','end':'#845ca1'}
RECT=[[0,0],[6000,0],[6000,6000],[0,6000]]
GABLE=[[0,0],[6000,0],[6000,4500],[3000,6000],[0,4500]]
HOLES=[[[1000,1500],[2200,1500],[2200,3000],[1000,3000]],[[3600,1500],[4800,1500],[4800,3000],[3600,3000]]]
NAME='УЧЕБНАЯ бетонная плитка 290×82'
DXF_QA={}

def configs(poly,zid):
 zreq={'op':'zones','contours':[{'id':'outer','pts':poly},{'id':'W1','pts':HOLES[0]},{'id':'W2','pts':HOLES[1]}],'units':'mm','cladding':NAME,'zone_prefix':zid,'start_index':1,'merge':False,'opening_kinds':{},'parapets':[]}
 z=facades_engine.run(zreq); assert z['ok'] and len(z['zones_full'])==1, z
 zone=z['zones_full'][0]; zone['id']=zid+'1'
 zrec={'zone_id':zid+'1','zone':zone}
 treq={'op':'tile_pattern','tile':{'w':290,'h':82},'gap':{'v':7,'h':7},'axis':'rows','bond':{'kind':'alternate','dir':'+','units':'frac','value':.5},'anchor':{'h':'R','v':'B','center':'tile','ref':'bbox'},'gap_around':True,'shaped':'split_joint','min_piece':10,'warn_cut':30,'kerf':3,'merge_touching':False,'types':[NAME],'zones':[zrec]}
 t=clad_engine.run(treq); assert t['ok'],t
 pz=t['per_zone'][0]
 freq={'op':'frame','sub_type':'vertical','system':{'name':'Standart','bracket_step':600,'bracket_step_corner':600},'rail_profile':'ГП-40-40','nsp_type':None,'cladding':'concrete','exact_step':True,'floor_step':0,'parts':'frame','tile_step_x':600,'tile_whip':2500,'tile_rail_brand':'УЧЕБНАЯ','tile_row_step':89,'zones':[dict(zrec,joints_x=pz['joints_x'],rows_y=pz['rows_y'])],'floors_y':[],'corners_x':[]}
 f=frame_engine.run(freq)
 return zreq,z,treq,t,freq,f

def dump(name,obj): (AS/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8')
def wrap_panel(s):return '\n'.join(textwrap.fill(line,width=65,break_long_words=False) for line in s.split('\n'))
def fig(ax,title,poly=RECT,lims=None):
 title=re.sub(r'^\d+\.\s*','',title)
 ax.set_aspect('equal');ax.set_title(title,fontweight='bold',fontsize=13,pad=13)
 ax.set_xlabel('X, мм');ax.set_ylabel('Y, мм');ax.grid(alpha=.16);ax.set_axisbelow(True)
 if lims: ax.set_xlim(*lims[0]);ax.set_ylim(*lims[1])
 else:ax.set_xlim(-450,6450);ax.set_ylim(-420,6460)
 for h in HOLES:ax.add_patch(Polygon(h,facecolor=COL['hole'],edgecolor='#557389',lw=1.4,zorder=4))
 ax.add_patch(Polygon(poly,fill=False,edgecolor='#273b48',lw=1.6,zorder=5))
def save(f,name,caption='Учебная схема, не снимок AutoCAD. Геометрия — результат текущего движка.'):
 f.text(.5,.015,caption,ha='center',fontsize=8,color='#66717b'); f.tight_layout(rect=(0,.035,1,1));f.savefig(AS/name,dpi=190);plt.close(f)
def piece_rings(p):
 if p.get('rings'):return p['rings']
 for k in ('pts','poly','points','polygon'):
  if isinstance(p.get(k),list):return [p[k]]
 return [[[p['x'],p['y']],[p['x']+p['w'],p['y']],[p['x']+p['w'],p['y']+p['h']],[p['x'],p['y']+p['h']]]]
def piece_geometry(p):
 rings=piece_rings(p);return GeoPolygon(rings[0],rings[1:])
def piece_path(p):
 geom=orient(piece_geometry(p),sign=1.0);verts=[];codes=[]
 for ring in [geom.exterior,*geom.interiors]:
  pts=list(ring.coords);verts.extend(pts);codes.extend([MplPath.MOVETO]+[MplPath.LINETO]*(len(pts)-2)+[MplPath.CLOSEPOLY])
 return MplPath(verts,codes)
def tiles(ax,t):
 for p in t['pieces']:
  cut=not p.get('full',False)
  ax.add_patch(PathPatch(piece_path(p),facecolor=COL['cut'] if cut else COL['tile'],edgecolor='white',lw=.3,zorder=2))
def frame(ax,f):
 for r in f.get('rails',[]):
  ax.plot([r['x'],r['x']],[r['y0'],r['y1']],color=COL['rail'],lw=1.5,zorder=6)
 for r in f.get('hrails',[]):
  col=COL['shina']
  kind=str(r.get('kind',''))
  if 'старт' in kind:col=COL['start']
  elif 'конц' in kind:col=COL['end']
  ax.plot([r['x0'],r['x1']],[r['y'],r['y']],color=col,lw=.8,zorder=3)
 for b in f.get('brackets',[]):ax.scatter([b['x']],[b['y']],s=15,color=COL['rail'],marker='s',zorder=7)
def dxf(name,poly,t=None,f=None):
 d=ezdxf.new('R2013');d.units=4;d.header['$INSUNITS']=4;d.header['$MEASUREMENT']=1
 for lname,color in [('SOURCE_OUTER',7),('SOURCE_WINDOWS',4),('TUTORIAL_TILES',30),('TUTORIAL_CUT',32),('TUTORIAL_RAILS',5),('TUTORIAL_SHINA',3),('TUTORIAL_BRACKETS',1)]:d.layers.new(lname,dxfattribs={'color':color})
 m=d.modelspace();m.add_lwpolyline(poly,close=True,dxfattribs={'layer':'SOURCE_OUTER'})
 for h in HOLES:m.add_lwpolyline(h,close=True,dxfattribs={'layer':'SOURCE_WINDOWS'})
 if t:
  for p in t['pieces']:
   for ring in piece_rings(p):m.add_lwpolyline(ring,close=True,dxfattribs={'layer':'TUTORIAL_CUT' if not p.get('full',False) else 'TUTORIAL_TILES'})
 if f and f.get('ok'):
  for r in f.get('rails',[]):m.add_line((r['x'],r['y0']),(r['x'],r['y1']),dxfattribs={'layer':'TUTORIAL_RAILS'})
  for r in f.get('hrails',[]):m.add_line((r['x0'],r['y']),(r['x1'],r['y']),dxfattribs={'layer':'TUTORIAL_SHINA'})
  for b in f.get('brackets',[]):m.add_circle((b['x'],b['y']),30,dxfattribs={'layer':'TUTORIAL_BRACKETS'})
 d.saveas(AS/name)
 reread=ezdxf.readfile(AS/name);assert not reread.audit().has_errors
 assert reread.header['$INSUNITS']==4
 entities=list(reread.modelspace());assert all(not e.xdata for e in entities)
 DXF_QA[name]={'audit_errors':0,'units':'mm','entities':len(entities),'plugin_xdata':False,'source_polylines':sum(e.dxftype()=='LWPOLYLINE' and e.dxf.layer.startswith('SOURCE_') for e in entities)}
 assert DXF_QA[name]['source_polylines']==3

r=configs(RECT,'У-');g=configs(GABLE,'ФР-')
geometry_qa={}
for name,poly,objs in [('rect',RECT,r),('gable',GABLE,g)]:
 wall=GeoPolygon(poly,HOLES);worst_outside=0.;worst_area_delta=0.
 for p in objs[3]['pieces']:
  shape=piece_geometry(p);assert shape.is_valid and not shape.is_empty,(name,p)
  outside=shape.difference(wall).area;worst_outside=max(worst_outside,outside)
  assert outside<1e-6,(name,'tile outside zone or inside opening',p)
  if 'area' in p:
   delta=abs(shape.area-p['area']);worst_area_delta=max(worst_area_delta,delta)
   assert delta<1e-6,(name,'area differs from engine result',p)
 geometry_qa[name]={'checked_pieces':len(objs[3]['pieces']),'max_outside_zone_or_in_window_mm2':worst_outside,'max_area_delta_mm2':worst_area_delta,'nonrect_pieces':sum(bool(p.get('rings')) for p in objs[3]['pieces'])}
for prefix,objs in [('rect',r),('gable',g)]:
 for suffix,obj in zip(['zone_request','zone_result','tile_request','tile_result','frame_request','frame_result'],objs):dump(prefix+'_'+suffix+'.json',obj)
dxf('01_source_rect.dxf',RECT);dxf('02_source_gable.dxf',GABLE)
dxf('03_reference_rect.dxf',RECT,r[3],r[5]);dxf('04_reference_gable.dxf',GABLE,g[3],None)

f,ax=plt.subplots(figsize=(7.3,7.5));fig(ax,'1. Исходные контуры: стена и два окна')
ax.add_patch(Polygon(RECT,facecolor=COL['wall'],zorder=0))
for x,y,s in [(3000,5600,'Стена: 6000 × 6000 мм'),(1600,2250,'Окно 1\n1200 × 1500'),(4200,2250,'Окно 2\n1200 × 1500')]:ax.text(x,y,s,ha='center',va='center',zorder=8)
ax.annotate('(0, 0)',(0,0),xytext=(300,-220),fontsize=10);ax.text(3000,650,'Подоконник: Y = 1500 мм',ha='center',fontsize=10)
save(f,'01_source_rect.png','Учебная схема. В DXF — только три замкнутые полилинии; размеры здесь поясняющие.')

f,ax=plt.subplots(figsize=(7.3,7.5));fig(ax,'2. Зона У-1: площади и погонажи');ax.add_patch(Polygon(RECT,facecolor=COL['wall'],hatch='///',edgecolor='#d8c9ad',zorder=0))
ax.text(3000,5350,'S участка = 36,00 м²\nS проёмов = 3,60 м²\nS облицовки = 32,40 м²',ha='center',va='center',fontsize=16,bbox={'fc':'white','ec':'#dfc7a3','pad':14},zorder=10)
ax.text(3000,700,'Отливы: 2 × 1,20 = 2,40 м\nОткосы: 2 × (1,20 + 1,50 + 1,50) = 8,40 м',ha='center',fontsize=11)
save(f,'02_zone_area.png')

f,ax=plt.subplots(figsize=(7.3,7.5));tiles(ax,r[3]);fig(ax,'3. ATTILE: плитка 290 × 82, шов 7, разбежка 1/2');ax.scatter([6000],[0],color='#ad4238',s=50,zorder=9);ax.text(5940,250,'ПН',ha='right',color='#ad4238',fontweight='bold');save(f,'03_tile_rect.png')
f,ax=plt.subplots(figsize=(8,5.5));tiles(ax,r[3]);fig(ax,'4. Узел у окна: светлая — целая, тёмная — подрезка',lims=((650,2500),(1170,1800)));save(f,'04_tile_detail.png')

f,ax=plt.subplots(figsize=(7.3,7.5));fig(ax,'5. ATFRAME: вертикальная подсистема и шины');frame(ax,r[5]);save(f,'05_frame_rect.png','Учебная схема по движку. Ручные шаги — только для обучения, без расчёта несущей способности.')
f,ax=plt.subplots(figsize=(8,6));fig(ax,'6. Подсистема у окна: направляющие, шины, кронштейны',lims=((500,2800),(1000,3500)));frame(ax,r[5]);save(f,'06_frame_detail.png','Учебная схема. Точки — кронштейны; вертикали — направляющие; горизонтали — шины.')

f,axs=plt.subplots(1,2,figsize=(11.8,6));tiles(axs[0],r[3]);fig(axs[0],'Облицовка');fig(axs[1],'Подсистема');frame(axs[1],r[5]);save(f,'07_compare_layers.png','Учебные схемы одной геометрии: проверяйте облицовку и подсистему с раздельной видимостью слоёв.')
f,ax=plt.subplots(figsize=(7.3,7.5));tiles(ax,g[3]);fig(ax,'7. Фронтон: подрезка следует скатам',GABLE);save(f,'08_gable.png','Учебная схема из движка ATTILE. Высота стенки 4500 мм; конёк 6000 мм; ширина 6000 мм.')

# UI guide diagrams are deliberately informational panels, not fake screenshots.
f,ax=plt.subplots(figsize=(8,8.5));ax.axis('off')
sections=[('1. Облицовка','Материал → наименование → элемент\nВ примере: «Клинкер / кирпич / плитка»; авто-блоки'),('2. Формат и швы','290 × 82 мм; вертикальный 7; горизонтальный 7'),('3. Разбежка','Горизонтальные ряды; «Через ряд»; 1/2 доли модуля'),('4. Отсчёт','ПН — правая нижняя точка габарита зоны'),('5. Подрезка и проёмы','Руст вокруг проёмов; резать с рустом; минимум 10; пропил 3'),('6. Принудительные русты','В первом примере обе галки выключены'),('Разложить','Подтвердить; прочитать сводку и предупреждения')]
for i,(ttl,txt) in enumerate(sections):
 y=.94-i*.135; ax.add_patch(Rectangle((.02,y-.1),.96,.115,transform=ax.transAxes,facecolor='#f5f1e9',edgecolor='#d8c9ad'));ax.text(.045,y-.018,ttl,transform=ax.transAxes,fontweight='bold',fontsize=14);ax.text(.045,y-.047,wrap_panel(txt),transform=ax.transAxes,fontsize=12.5,va='top')
save(f,'09_attile_fieldmap.png','Схема разделов окна ATTILE. Учебные значения.')

f,ax=plt.subplots(figsize=(8,8.5));ax.axis('off')
sections=[('1. Облицовка','«бетонная плитка» — включает шины, кляммеры не создаются'),('2–3. Что раскладывать / Подсистема','Для плитки состав определяется автоматически; «вертикальная», ГП-40-40'),('4. Шаги кронштейнов','«вручную»: 600 / 600; старт 300; зазор стыка 10'),('5. Направляющие и шины','Шаг 600; угловой 0; хлыст 2500; марка «УЧЕБНАЯ»'),('6. Дополнительные точки','Первое упражнение — без внешних углов и отметок перекрытий'),('7. Знаки','«условные» — не нужны внешние блоки-образцы'),('Что будет → Разложить','Прочитать итоговое описание перед построением')]
for i,(ttl,txt) in enumerate(sections):
 y=.94-i*.135;ax.add_patch(Rectangle((.02,y-.1),.96,.115,transform=ax.transAxes,facecolor='#edf3f4',edgecolor='#b8cbd1'));ax.text(.045,y-.018,ttl,transform=ax.transAxes,fontweight='bold',fontsize=14);ax.text(.045,y-.047,wrap_panel(txt),transform=ax.transAxes,fontsize=12.5,va='top')
save(f,'10_atframe_fieldmap.png','Схема разделов окна ATFRAME. Учебные значения.')

base_commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
working_tree_modified=bool(subprocess.check_output(['git','diff','HEAD','--','Facades','AClad','AFrame'],cwd=ROOT,text=True).strip())
engine_files=[p for mod in ['Facades','AClad','AFrame'] for p in (ROOT/mod/'engine').glob('*') if p.suffix in ['.py','.json']]
summary={'rect':{'zone':r[1]['summary'],'tile':r[3]['summary'],'frame':r[5].get('summary'),'frame_ok':r[5].get('ok'),'notes_frame':r[5].get('notes')},'gable':{'zone':g[1]['summary'],'tile':g[3]['summary'],'frame':g[5].get('summary'),'frame_ok':g[5].get('ok'),'notes_frame':g[5].get('notes')},'base_commit':base_commit,'working_tree_modified':working_tree_modified,'engine_sha256':{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(engine_files)},'note':'Generated from the current working tree; base_commit alone does not identify the inputs. File SHA-256 values identify the code. Python engine diagrams, not AutoCAD screenshots or live AutoCAD acceptance.'}
dump('verification.json',summary)
dump('geometry_qa.json',geometry_qa)
dump('dxf_qa.json',DXF_QA)
source_files=[]
for folder in ['Facades/engine','AClad/engine','AFrame/engine','Facades/src/AFacadesPlugin','AClad/src/ACladPlugin','AFrame/src/AFramePlugin']:
 source_files.extend(p for p in (ROOT/folder).glob('*') if p.suffix in ['.py','.cs','.json'])
dump('source_manifest.json',{'generated_at_utc':datetime.now(timezone.utc).isoformat(),'base_commit':base_commit,'working_tree_modified':working_tree_modified,'note':'Current working tree snapshot; use file SHA-256 values, not base_commit alone. This is not a release identity.','files':{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(source_files)}})
print(json.dumps(summary,ensure_ascii=False,indent=2))
