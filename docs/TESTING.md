# Проверки фасадных модулей

Команды запускаются из корня репозитория. Состав обязательных Windows
проверок задаёт [check.yml](../.github/workflows/check.yml).
Проверяйте изменения на конкретном воспроизводимом примере. Зелёный
стенд подтверждает программный результат, но не живую работу AutoCAD.

## Локальный набор перед коммитом кода

```bash
export PYTHONUTF8=1
(cd Facades/engine && python3 test_facade_zones.py && python3 test_facades_engine.py)
(cd AClad/engine && python3 test_cladding_plan.py && python3 test_clad_engine.py && python3 test_tile_pattern.py && python3 audit_clad.py)
(cd AFrame/engine && python3 test_frame_plan.py && python3 test_frame_calc.py && python3 test_frame_beam.py && python3 test_frame_cutting.py && python3 test_frame_engine.py && python3 test_frame_topology.py && python3 test_frame_topology_index.py && python3 test_frame_connections.py && python3 test_frame_solution_selection.py && python3 audit_frame.py)
python3 tools/facades/test_bundle_versions.py
python3 Facades/tests/test_zone_migration.py
python3 tools/xmod_check.py --no-fixture      # стыки модулей (ATSPEC X6 — не фасады, известен)
python3 AFrame/tools/frame_synth.py --quick   # F16 исправлен 30.09; исключение больше не требуется
python3 Facades/tools/zones_synth.py --quick
python3 AClad/tools/attile_synth.py --quick
python3 tools/roles_synth.py --quick          # «в ролях конструктора»: ATFZONE → раскладка → ATFRAME (нужен mono)
python3 AFrame/engine/test_frame_typical_facades.py # пять типовых фасадов: обязательное построение и адресные пометки; настоящий FrameSettings, нужен mono/mcs
python3 AFrame/tests/test_local_issues.py --out <папка> # пометки → сохранение → ведомость/Excel, нужен mono/mcs или Windows .NET
python3 tools/fixes_3009/run_facade_regressions.py  # shapely/openpyxl + mono/mcs; --python-only — неполный набор
python3 tools/atr_review_3009/run_checks.py --out <папка> # полный фасадный набор
```
C# фасадов можно проверить полной компиляцией через
`tools/fixes_3009/compile_facades.py` (dotnet либо Roslyn/Mono с официальными
net48/AutoCAD references; инструкция — `tools/fixes_3009/README.md`).
Проверка синтаксиса `mcs --parse` не заменяет компиляцию. Окна
ATTILE/ATFRAME/ATFZONE — прогон под mono (`AClad/tools/attile_ui`, `AFrame/tools/frame_ui`,
`Facades/tools/zone_ui`, команды сборки в шапке UiCheck.cs). Запрос движку из окна ATFRAME —
`FrameSettings.EngineParams` (его же зовёт `tools/roles_synth.py` через
`AFrame/tools/roles/RolesDump.cs`): новые поля окна — туда, не в FrameCommand.
После успешного push также дождаться зелёного CI `check`; при красном —
прочитать аннотации. Компиляция и CAD doubles не заменяют живой AutoCAD.

Пять типовых фасадов проверяются и в `check`, и перед выпуском. Отказ в этих
сценариях — ошибка теста, даже если сообщение отказа корректно. Построение
с непроверенными участками учитывается отдельно от прохождения ограниченного
расчёта. В Windows тест использует уже собранный `RolesDump.exe` через
`ROLES_DUMP_EXE`; при его отсутствии локально нужны `mono` и `mcs`.


## Где сохранять результаты

В Git сохраняются вход минимального воспроизведения, тест и одна краткая
таблица результатов с проверенным SHA, ссылкой на CI и ограничениями.
Подробные логи, снимки промежуточных прогонов и повторяющиеся квитанции —
в артефактах CI; для локального прогона — вне рабочего дерева. Не создавать
очередной комплект журналов и копий статуса для каждой малой правки.
Существующий архив проверок сохранён для разбора прошлых решений.

Не повторять неизменные прогоны без конкретного риска или обязательного
требования CI. Если результат переиспользуется, явно указать это и проверить
неизменность зависимостей. Не выдавать проверку адаптера за запуск AutoCAD.
