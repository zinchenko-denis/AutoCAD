# Ограниченный приёмочный поток П1/П5

Проверки адаптерного потока — **PASS: 1275** (146 native + 1129 Python), девять вызовов настоящих Python engines и девять файлов XLSX. Монтажный допуск 2В и расчёт 3А — **BLOCKED**. Полный П5 и живой AutoCAD — **NOT_RUN**.

Повторение из корня репозитория:

```sh
python tools/limited_pilot_0110/run_acceptance.py --out <пустая-папка-результатов>
```

Нужны Python/Shapely/openpyxl и .NET Framework compiler/runtime (Windows: dotnet/net48; Linux: mcs/mono). Один persistent native процесс держит одну managed CAD-double Database. JSON-lines bridge передаёт точный запрос после фактического capture sources и возвращает неизменённый ответ engine. Native events и engine имеют deadline 45 секунд, завершение native — 15 секунд.

`manifest.json` связывает 68 исходников, три извлечённых C# файла и 48 JSON артефактов. Извлечённые производители, SetProjectParameters и predicate актуальности ATFNODE взяты из действующих исходников; временные build/exe/dll здесь не публикуются. `engine_ledger.json` хранит хеши точных запросов/ответов, `ledger.json` — поколения источников и результаты отказов, `trace.json` — связи физических ID с декларациями и кандидатами. `xlsx_review.json` связывает девять XLSX; каждая фактическая ячейка сверена с соответствующим `*_rows.json` без подмены пустых строк.

Две зоны одного фасада проверяют независимые операции ATTILE/ATFRAME. Глобальное изменение выноса облицовки 230→240 делает оба старых результата подсистемы устаревшими; новый capture и engine run восстанавливают их. Затем локальное исключение правой зоны 260 меняет только её run; левый run остаётся тем же. Поздний ответ, вычисленный до изменения проекта, отклонён именно по старой project dependency с нулём записей. Раскладки облицовки от этого изменения декларации не устаревают.

Граница стенда: настоящий код stores/guards/settings/producers/core/table views/XLSX работает на `QuantityCadDoubles`. Материализацию объектов, owner metadata и CAD-link attachment выполняет явно тестовый мост, а не полноценные команды AutoCAD. Happy-flow мост создаёт и стирает отображения до StoreFrame; это не доказательство product prewrite или rollback CAD. Commit/Dispose транзакций doubles — no-op. Проверен настоящий файловый rollback QuantityXlsxFile; rollback чертежа, DWG save/open, COPY, Undo и native render не проверены. Ни одного фиктивного DWG здесь нет.

Для ATFNODE проверены фактические project resolver, pure snapshot, geometry, drawing data и дословно извлечённый source predicate команды. Полная команда, renderer и node store не исполнялись. Локальный зазор 20 мм во всех эпохах не подтверждает монтаж, регулировку, фиксированные/скользящие опоры или прочность. Новые producer runs получают новые физические ID; одинаковая геометрия не означает идентичность физических деталей между поколениями.

Вход синтетический; исходных приватных PDF нет. Independent oracle расположен на уровень выше. Новые файлы результата несут собственные GUID и fingerprint поколений, поэтому их байтовые хеши между повторениями различаются.
