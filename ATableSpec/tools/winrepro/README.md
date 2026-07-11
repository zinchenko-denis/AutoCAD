# winrepro — прогон построителя без AutoCAD (mono + xvfb)

Регресс мёртвого выбора «Штапики» (гард ApplyTemplate отсекал tpl=4, 07–08.07)
и сквозная проверка пути клик→пересев→грид→BuildDef→def.

```
apt-get install -y mono-complete xvfb
python3 patch_form.py
cp ../../src/AtSpecPlugin/ValueComboCell.cs .
mcs -warn:0 -r:System.Windows.Forms.dll -r:System.Drawing.dll \
    -r:System.Web.Extensions.dll -out:repro2.exe \
    FormPatched.cs ValueComboCell.cs ReproShim.cs Repro2.cs
xvfb-run -a mono repro2.exe   # exit 0 = все проверки
```

99 проверок: 4 секции, «Отчёт N из 4», источники, СКРЫТЫЕ auto-фильтры ШТ_СТЫК
0/0/1 (10.07-2: из грида убраны, в сводке видны), Стойки=RF-стойки, def
(фильтры/Слой/beads.layer/beads.source), самопроверка BuildDef без ложных
срабатываний, «Взять с табл.» (литерал/развёртка/слияние по артикулу),
геометрия терморазрыва HasBreakJoint (зеркало _beads_expand: стык/вплотную/
не прилегает), пресет и Take без стыков (секции 3–4 не сеются/пропущены),
▼-подсказки у «Выражения», дамп в TEMP. mono ≠ .NET Fx под AutoCAD —
хост-специфику не ловит, но мёртвые ветки и грид→def ловит. Негативный контроль:
вернуть в FormPatched.cs старый гард `tpl > 3` — тест обязан упасть (cards=1).

CutDump.cs — e2e-драйвер: живая форма headless, пресет «Штапики» (боксы СО
стыком) → spec/cut def'ы + пара nobrk (боксы БЕЗ стыка: пресет 2 секции, Take
из полной спецификации без разрезных) → /tmp/atspec_*_def.json; их гоняет
e2e_1007.py на живых DXF atspec-testdata (22 проверки, вкл. NOBRK3–6).

Хвост: прогонять на CI windows-runner (настоящий .NET Fx) — не подключено.
