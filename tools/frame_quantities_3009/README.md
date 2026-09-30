# Проверки ведомости подсистемы и производительности

Из корня репозитория. Эти проверки не устанавливают плагины, не публикуют выпуск и не заменяют живую приёмку AutoCAD.

```bash
python tools/frame_quantities_3009/test_frame_core.py --out /tmp/frame-core
python tools/frame_quantities_3009/test_frame_core_perf.py --quick --out /tmp/frame-core-perf-quick
python tools/frame_quantities_3009/test_frame_producer.py --out /tmp/frame-producer
python tools/frame_quantities_3009/test_frame_table.py --out /tmp/frame-table
python tools/frame_quantities_3009/test_frame_engine_perf.py --targets 1000,3000 --out /tmp/frame-engine-quick
python tools/quantities_3009/test_quantity_store_perf.py --quick --out /tmp/quantity-store-perf-quick
python tools/frame_quantities_3009/test_store_encoding.py --out /tmp/quantity-store-encoding
```

Нужны Python с openpyxl и Mono/mcs в Linux; в Windows чистые C# probes используют .NET SDK и временный проект net48. Общий `tools/quantities_3009/probe_runtime.py` выбирает подходящий компилятор. Отсутствие среды даёт отказ, а не успешный пропуск.

| Проверка | Независимый контроль |
|---|---|
| Core | Раздельные категории, количество физических элементов, длина одного отрезка и общий погонаж, неизвестные свойства, область оценок, конфликты идентичности |
| Producer | Настоящие `frame_engine` → неизменённые CAD-независимые методы `FrameQuantities.BuildReport` → общее ядро; ответ сверяется с массивами деталей и геометрическими концами каждого отрезка |
| Table/XLSX | Настоящие строки DWG/XLSX, точные числа через openpyxl, отдельные итоги по категориям, отдельная оценка хлыстов, инженерные ограничения и примечания |
| Engine stage | Только Python-движок: ручная расстановка и ограниченная расчётная цепочка, число зон/выданных объектов, время вызова и пиковая память отдельного процесса |
| Store/Read | Настоящий адаптер с инструментированными CAD doubles: ограниченный рост обращений к объектам и геометрии зон, один обход ModelSpace; не время работы AutoCAD |
| Store encoding | Настоящие Hash/Compress/Decompress: SHA-256 целого UTF-8, границы буфера/суррогатов и чтение прежнего GZip формата |

Синтетические примеры включают стойку из трёх кусков 3000+3000+80 мм при оценке двух хлыстов, дробные длины, проём, ручную межэтажную и ортогональную схемы, шины клинкера/бетона, АКП без узла крепления, только кляммеры, независимые зоны и подтверждённый пустой состав. Эти размеры служат проверкам кода и не являются универсальными инженерными нормами.

Для полной характеристики масштабирования:

```bash
python tools/frame_quantities_3009/test_frame_core_perf.py --out /tmp/frame-core-perf
python tools/frame_quantities_3009/test_frame_engine_perf.py --out /tmp/frame-engine-perf
python tools/quantities_3009/test_quantity_store_perf.py --large --out /tmp/quantity-store-perf
```

В Windows проверка Store использует `dotnet build tools/quantities_3009/QuantityStorePerfCheck.csproj -c Release -o build_quantity_store_perf`, затем тот же Python runner с `--exe build_quantity_store_perf/QuantityStorePerfCheck.exe`. В CI выполняются только четыре коротких случая (облицовка/подсистема, 10/40 зон по 3 элемента). Полный локальный прогон отдельно измеряет 10/50/100 зон по 300 деталей и одну зону с 10 000/50 000/150 000 деталей. Подготовительная запись прежних паспортов раскладки (`legacy_layout_store`) измеряется отдельно: её повторные снимки источников не входят в новую гарантию ограниченного роста Store/Read ведомости.

Основные размеры — 10 000, 50 000 и 150 000 элементов. У движка количество округляется вверх до целого числа одинаково заданных зон; манифест хранит и целевой, и фактический размер. Core проверяет ограниченный рост числа операций: полная валидация и проверка ссылок сохраняются, уникальные детали не должны требовать повторного построения канонических строк сравнения.

Время и память являются описанием конкретного прогона. В CI нет жёсткого порога в секундах. Производительность Python, чистой C# агрегации и чтения CAD-паспортов измеряется отдельно; результат любого из этих этапов не следует называть скоростью команды внутри AutoCAD. Живой пилот должен дополнительно проверить построение, запись/чтение DWG, отмену, Undo/Redo, повторное открытие и нагрузку на реальных образцах блоков.
