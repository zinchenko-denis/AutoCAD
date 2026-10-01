# Ограниченный приёмочный проход П1/П5

Из корня репозитория, с Python, Shapely, openpyxl и .NET compiler/runtime:

```sh
python3 tools/limited_pilot_0110/run_acceptance.py --out /tmp/limited-pilot
python3 tools/limited_pilot_0110/test_axis_cancel.py --out /tmp/axis-cancel
```

Первый запуск использует один persistent native процесс и одну базу
QuantityCadDoubles. Настоящие zone/layout guards, project resolver,
BondSettings/FrameSettings готовят запросы и снимки до расчёта; Python
выполняет настоящие движки. Их ответы проходят через дословно извлечённые
producer методы, настоящий quantity store, Read, модели таблиц и XLSX.
Проектное изменение, локальное исключение, устаревший отложенный ответ
и повторная выдача проверяются на тех же зонах, ID и зависимостях.

Вход: две отдельные стены1210×1820, плита600×600, шов10; manual800/800,
отдельные операции каждой зоны в общем проекте. Независимый эталон —
`docs/limited_pilot_0110/independent_oracle.json`. Первоначальный проход:
12 целых плит,4.32м²;2ГП/3.64м;6 условных кронштейнов. Проект230→240,
затем исключение наружной облицовки260 справа сохраняют эти количества,
но требуют новых зависимых результатов. Левый run после правого исключения
остаётся прежним. Не назначаются физические УК или подтверждённые изделия.

Схема узла исполняет настоящие Snapshot/Geometry/Drawing/Mounting и
дословный predicate актуальности из команды. Это не исполнение всего
ATFNODE command/store/renderer. Материализация CAD-сущностей — тестовый
мост; Transaction.Commit/Dispose здесь пустые. Настоящие DWG, native COPY,
SaveAs, Undo и AutoCAD не исполняются. Реальный файловый rollback XLSX
проверяется отдельно, без заявления о rollback CAD.

Второй запуск извлекает точный участок ручных осей FrameCommand и
существующую очистку rowsY для режима frame. Реальные FrameSettings и
движок отличают Enter от отмены после одной оси. Это отдельный вход без
ATTILE, его результат1ГП/4 кронштейна не относится к основным2ГП/6.
Полная команда и native клавиша Esc не исполняются. Старый исходник можно
передать через `--source` и проверить `--expect broken`; точные команды
baseline replay — `docs/limited_pilot_0110/axis_cancel/README.md`.

Результаты включают манифест, входы/ответы, ledger, строки таблиц, XLSX,
хеши исходников и отдельно generated extraction. Ожидание native/engine
событий ограничено45с, завершения15с; stderr читается отдельно. Отсутствие
runtime возвращает2, ошибка проверки — ненулевой exit code.

Обе команды входят в обязательные58 групп и Windows CI.
Артефакт CI — `limited-pilot-acceptance`, каталоги `workflow/` и
`axis_cancel/`. PASS относится к указанному адаптерному участку;
полный П5 и живой AutoCAD — NOT_RUN, полный2В/3А — BLOCKED.
Сохранённые доказательства — `docs/limited_pilot_0110/`;
контракт — `docs/LIMITED_PILOT_ACCEPTANCE_01.10.md`.
