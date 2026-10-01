# Воспроизведение табличной команды

Итог текущего исходника: **4886 PASS**. Тот же окончательный стенд на полном исходном `FacadeQuantityTableCommand.cs` из `16c7307ce42715721ee7835d2dc90955e2229781` даёт **4870 BASELINE_DEFECT_REPRODUCED**. Последующее ожидание исправленной координаты на старом исходнике закономерно падает; точная причина сохранена в `sensitivity/`.

```sh
python tools/table_lifecycle_0110/run_lifecycle.py --out <after-results>
python tools/table_lifecycle_0110/run_lifecycle.py --out <before-results> --command-source docs/table_lifecycle_0110/before/command_source.cs --expect baseline
python tools/table_lifecycle_0110/run_lifecycle.py --out <sensitivity-results> --command-source docs/table_lifecycle_0110/before/command_source.cs --expect fixed
```

Последняя команда должна завершиться ненулевым кодом на `actual new table UCS placement matches expected fixed`. Runtime не требует Git или доступности старой истории: точный baseline проверяется закреплённым LF SHA256 `198cb83d4fb7c99cc24da4fba81c6cf4c6c14d20d34b320b2da57b72f4fde922`. Разрешается только отличие LF/CRLF. В каждом `command_source.cs` лежит реально исполнявшийся полный продуктовый исходник; raw и нормализованный SHA указаны отдельно.

Числа **4886 = 146 + 1129 + 115 + 3496**: прежние native/Python проверки пилота и новые native/Python проверки команды соответственно. Прежний отдельный runner также сохранил **1275 PASS**; `base_1275_manifest.json` и `base_1275_receipt.json` — явно ограниченная совместимостная квитанция без второго полного набора артефактов.

Обе основные папки `before/` и `after/` содержат все файлы, связанные `artifact_sha256`, и четыре дословных extraction source, связанные `generated_extraction_sha256`. Все manifest хеши сверены; `evidence_index.json` хранит размеры и SHA256 каждого опубликованного файла. EXE/DLL и прочие build-файлы не сохранены. В `after/` —106 файлов до индекса, 1 303 200 байт; в `before/` —102 файла, 1 282 766 байт.

За один проход реально выдаются **19 XLSX**: девять прежнего пилота, девять командных выдач по трём видам/эпохам и одна отдельная выдача «Ексель». Дополнительно сохранены исходные файлы контроля отказов: десять в after и шесть в before. Эти sentinel-копии не считаются новыми выданными ведомостями. Итого файлов XLSX: 29 и25 соответственно. Для командных выдач проверен весь прямоугольник, отсутствие лишних значений/формул, точные числовые/строковые значения. Проверены настоящие данные Table.Cells, размерность, объединения и экранирование примечания; metadata не подменяет проверку тела таблицы.

Три независимых координатных примера: единичная ПСК, перенос100/200 и перенос с поворотом90°. Последний переводит введённую точку3/4 в МСК96/203. Update сохраняет существующую WCS позицию/owner ID и не запрашивает новую точку. Изменение ПСК воспроизводится после GetPoint, в SaveFileDialog, перед lock и внутри повторного чтения источников. При отмене второго preview, неправильном виде/типе target, внешней законченной правке metadata или изменении проекта старые таблицы и файлы сохранены.

`performance_counts.json` сравнивает counters сразу после возвращения настоящей команды, до test inspections. Source GetObject reads и ModelSpace visits before/fixed совпали; новая таблица добавляет три чтения Editor.CurrentUserCoordinateSystem. Update и Excel-only — ноль. Объём прочитанного текста слегка меняется из-за разных GUID и сжатия отчётов; реальные различия сохранены. Времена относятся только к стенду.

Границы: одна persistent модель Database с настоящими engines/guards/stores/producers и настоящей ветвью `FacadeQuantityTableCommand.Run`; внешний ATFTABLE dispatcher, WinForms и native host не исполнялись. Prompt/host/Table APIs — ограниченные doubles. Транзакции doubles по-прежнему no-op: mid-Fill cancel, CAD rollback/Undo, native COPY, Save/Open DWG, UCS/DUCS и визуальный render — **NOT_RUN**. Полный ATFNODE renderer/store в общей базе остаётся следующим шагом.

Последний late-project case намеренно оставляет внешнюю revision3; стенд не откатывает её и не выдаёт прежние результаты revision2 за свежие. Инженерный монтажный допуск2В и расчёт3А остаются **BLOCKED**. Ни DWG, ни физические УК/монтажные интерфейсы не фабрикуются; приватных PDF здесь нет.
