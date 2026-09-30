# Проверки ведомости облицовки

Запуск из корня репозитория. Выход 0 означает выполненную проверку; отсутствие требуемой среды даёт отказ, а не пропуск с успехом. Созданные исполняемые файлы — проверочные, не установочный выпуск.

```bash
python tools/quantities_3009/test_quantities_core.py --out /tmp/quantity-core
python tools/quantities_3009/test_cladding_producer.py --out /tmp/quantity-producer
python tools/quantities_3009/test_cladding_table.py --out /tmp/quantity-table
python tools/quantities_3009/test_quantity_store.py --out /tmp/quantity-store
```

Для Linux нужны Mono/mcs в PATH; для Python — Shapely и openpyxl. Три первые проверки в Windows используют установленный .NET SDK и временный проект net48. Проверка адаптера в Windows использует отдельный проект:

```powershell
dotnet build tools/quantities_3009/QuantityStoreCheck.csproj -c Release -o build_quantity_store
python tools/quantities_3009/test_quantity_store.py --exe build_quantity_store/QuantityStoreCheck.exe --out quantity_store_results
```

Все четыре группы включены в `tools/atr_review_3009/run_checks.py` и GitHub Actions `check`. В `--out` сохраняются журналы, манифесты с хешами источников и только синтетические результаты.

| Группа | Что проверяется |
|---|---|
| Core | Идентичность деталей и форм, кольца и отверстия, точные площади, группировка, конфликт паспортов, повторный выбор, неполнота, общая область раскроя, подтверждённый нулевой результат |
| Producer | Настоящие ответы `clad_engine` → неизменённый участок `BuildReport` → общее ядро; отверстие внутри одной детали, фигурные скаты, типы и подрезки, объединённые и отдельные зоны, нулевые результаты ATTILE/ATCLAD, отказ при потере состава или несовпадении площади |
| Table/XLSX | Настоящие `CladdingTableData`, `XlsxWriter`, `QuantityXlsxFile`; точность чисел через openpyxl, неизвестные значения, отдельный раскрой, формы, примечания, подтверждённый ноль, отмена и откат записи файла, отказ для некорректного XML |
| Store | Настоящий `FacadeQuantityStore` с CAD API doubles и геометрией реального движка зон; происхождение и актуальность паспортов, удаления/изменения/копии объектов, полный состав выбора, сохранение нулевого состава |

Producer извлекает CAD-независимые методы из исходного файла без замены их реализации. Его условные ссылки CAD нужны только для проверки агрегации. Store исполняет настоящий адаптер с явными тестовыми заменами API. Эти проверки **не подтверждают** транзакции AutoCAD, Undo, COPY, сохранение/повторное открытие DWG, окна или расположение таблицы на листе. Это остаётся отдельной живой приёмкой после ревизии кода.

Проверка компиляции трёх фасадных плагинов с настоящими AutoCAD/net48 references выполняется отдельно через `tools/fixes_3009/compile_facades.py`. Она также не заменяет загрузку плагинов в AutoCAD.

Опциональная проверка объёма чистого ядра (150 000 синтетических DTO, восемь типоразмеров; без DWG) сохранена вместе с наблюдениями в `docs/quantities_3009/checks/core/`. Повтор под Mono:

```bash
mcs -r:System.Web.Extensions.dll -out:/tmp/quantity-perf.exe Common/FacadeQuantities.cs docs/quantities_3009/checks/core/CorePerformanceProbe.cs
mono /tmp/quantity-perf.exe 150000
mono /tmp/quantity-perf.exe 150000 rings
```

Время и память зависят от среды; это не гарантированный предел производительности AutoCAD. Фактическое хранение большого DWG проверяется отдельно в хосте.
