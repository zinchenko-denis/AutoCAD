# Проверка применимости статической схемы

Из корня репозитория, Python 3.12+; продуктовые движки используют только стандартную библиотеку. Приватные PDF для синтетических воспроизведений не нужны.

```bash
python tools/static_review_3009/reproduce_model_gap.py --revision ad2a0a2 --out /tmp/model-before.json
python tools/static_review_3009/reproduce_model_gap.py --out /tmp/model-after.json
python AFrame/engine/test_frame_topology.py
python tools/atr_review_3009/run_checks.py --out /tmp/facade-checks
python tools/fixes_3009/compile_facades.py --references /path/to/official-references --out /tmp/facade-compile
```

Первый запуск должен воспроизвести старый ошибочный успех; второй требует только точного отказа нового кода. Проверка чувствительности временно выбирает уже существующую строку коэффициентов в памяти и восстанавливает функцию через `finally`. Это не новый проектный расчёт. Подробности источников и ограничения — `docs/static_review_3009/Source_findings.md`.

`test_frame_topology.py` проверяет реальные ответы движка: опоры, однопролётные куски, класс коэффициентов, превышенный орто-пролёт, раздельность стыков с зазорами 0/10 мм, сохранение старого отказа свободных концов, ручные положительные контроли и атомарный отказ нескольких зон. Успешные ограниченные цепочки обязаны сохранять `static_model.status=not_verified`.

Агрегат содержит 24 группы. Для C#-проб нужен Mono/mcs либо предусмотренный инструментом Windows/.NET путь; зависимости и официальные references — в `tools/fixes_3009/README.md`. Отсутствующая среда не является успешным тестом. Исключение X6 в общей стыковой проверке относится к неизменённому ATSPEC; фасадное F16 не исключается.

Полные геометрические и ролевые сценарии:

```bash
python Facades/tools/zones_synth.py
python AClad/tools/attile_synth.py
python AFrame/tools/frame_synth.py --seed 2909
python AFrame/tools/frame_synth.py --seed 77
python AFrame/tools/frame_synth.py --seed 5
python tools/roles_synth.py --seed 2909
python tools/roles_synth.py --seed 77
python tools/roles_synth.py --seed 5
python docs/facades_beginner_3009/replay_examples.py
```

Роли воспроизводят действия через настоящий `FrameSettings`. Расчётная межэтажная роль ожидает точное объяснение в `Validate` и `Describe`; отдельные ручные роли проходят геометрический цикл. Положительные расчётные контроли vertical/ortho не заменяются отказами. Счётчики построений, ожидаемых отказов и проверочных контролей выводятся раздельно.

При параллельном запуске ролей каждому процессу нужен отдельный `TMPDIR`, иначе используется один `/tmp/roles_dump.exe`. Последовательный запуск этой проблемы не имеет. Это исходные проверки, не AutoCAD runtime, не установка и не публикация бандлов.
