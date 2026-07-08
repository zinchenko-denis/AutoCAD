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

33 проверки: 4 секции, «Отчёт N из 4», источники, фильтр-строки ШТ_СТЫК 0/0/1,
Стойки=RF-стойки, def (фильтры/Слой/beads.layer/beads.source), самопроверка
BuildDef без ложных срабатываний, дамп в TEMP. mono ≠ .NET Fx под AutoCAD —
хост-специфику не ловит,но мёртвые ветки и грид→def ловит. Негативный контроль:
вернуть в FormPatched.cs старый гард `tpl > 3` — тест обязан упасть (cards=1).

Хвост: прогонять на CI windows-runner (настоящий .NET Fx) — не подключено.
