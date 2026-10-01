# Приёмка ветви команды количественной таблицы

```sh
python tools/table_lifecycle_0110/run_lifecycle.py --out <папка-результатов>
```

Запускается целиком настоящая `FacadeQuantityTableCommand.Run` поверх одного persistent процесса/Database ограниченного пилота. Его настоящие zone/ATTILE/frame engines, producer, geometry guards, quantity/project stores и прежние 1275 проверок сохраняются. Частичный C# hook добавляет командные checkpoints без копии исходного сценария.

Проверяются создание/обновление таблиц облицовки, элементов и соединений; metadata и реальные Table.Cells/размеры/merges; полные прямоугольники XLSX без лишних значений/формул; сохранение owner ID, WCS позиции и примечания; два чтения источников; ранняя отмена; смена источников/target после preview. Координатные контроли исполняют команду при единичной, перенесённой и перенесённой с поворотом ПСК. Изменение ПСК проверяется после точки, в SaveFileDialog, перед lock и во время повторного чтения источников. Обновление и Excel-only не приобретают зависимость от ПСК.

Перед исправлением команда на исходнике `16c7307ce42715721ee7835d2dc90955e2229781` сохраняла `(3,4,0)` вместо `(96,203,0)` при origin `(100,200,0)`, повороте90° и введённой точке `(3,4,0)`. Исторический исходник воспроизводится без checkout/reset:

```sh
python -c "import pathlib,subprocess; pathlib.Path('table-before.cs').write_bytes(subprocess.check_output(['git','show','16c7307ce42715721ee7835d2dc90955e2229781:Facades/src/AFacadesPlugin/FacadeQuantityTableCommand.cs']))"
python tools/table_lifecycle_0110/run_lifecycle.py --out <before-results> --command-source table-before.cs --expect baseline
python tools/table_lifecycle_0110/run_lifecycle.py --out <sensitivity-results> --command-source table-before.cs --expect fixed
```

Второй запуск подтверждает известный дефект (`BASELINE_DEFECT_REPRODUCED`), третий обязан завершиться ненулевым кодом именно на неверной позиции. `command_source.cs` в результатах — точная копия исполнявшегося полного исходника. Manifest отдельно указывает его raw SHA256 и совпадение с историческим Git blob; override не выдаётся за текущий продукт.

Нужны те же Python/Shapely/openpyxl и .NET compiler/runtime, что у limited pilot. Prompt/form/host/Table APIs — явно ограниченные doubles; графический WinForms и внешний ATFTABLE dispatcher не исполняются. `ZoneCommand.ReadZoneData`, `StoreZoneData` и `SafeStr` извлекаются дословно. Xrecord read hook существует только в этой дополнительной сборке; в старом runner partial call удаляется компилятором.

Транзакции doubles по-прежнему **не откатывают CAD**. Cancel проверяется до записи таблицы; mid-Fill cancel, native rollback/Undo/COPY/DWG save/open/render — **NOT_RUN**. Состояние внешней законченной мутации не восстанавливается тестом: последний late-project control намеренно оставляет revision3, после уже проверенной финальной выдачи revision2. Примитивы полного ATFNODE renderer/store здесь не добавляются. Инженерный монтажный допуск и расчёт остаются **BLOCKED**.

Command counters снимаются сразу после возврата, до инспекции metadata/cells. Test inspection reads учитываются отдельно; случаи с внесённой внешней мутацией помечены. Editor UCS reads отделены от чтений исходных CAD объектов: новая таблица — capture + две проверки, update/Excel-only — ноль. Эти цифры характеризуют managed стенд, не скорость живого AutoCAD.
