# Воспроизведение аудита АТР

Итог: `docs/ATR_REVIEW_30.09.md`. Рабочие источники — фасадные модули, исторические альбомы и статические расчёты в отдельном частном репозитории. Пробы синтетические; настоящего AutoCAD не запускают.

```bash
python3 tools/atr_review_3009/run_checks.py --out /absolute/path/checks
python3 tools/fixes_3009/compile_facades.py --references /absolute/path/references --out /absolute/path/compile
```

Нужны зависимости основного проекта и Mono/mcs для чистых C#-проб; путь к официальным compile references описан в `tools/fixes_3009/README.md`. Выпуск и сетевые изменения эти команды не выполняют.

Отдельные адресные проверки:

```bash
python3 AFrame/engine/test_frame_rules.py
python3 tools/atr_review_3009/vector1_topology.py --out /tmp/topology.json
python3 tools/atr_review_3009/architecture_probe.py --out /tmp/architecture
python3 tools/atr_review_3009/vector5_mass_review.py --evidence /tmp/architecture/evidence.json --out /tmp/mass.json
python3 tools/atr_review_3009/vector5_contract_probe.py --out /tmp/vector5
```

`nordfox_contract_probe.py --revision 5cdd34e --output /tmp/nordfox_before.json` воспроизводит **исторические наблюдения** старого контракта, включая его ошибки. Этот набор не является тестом ожидаемого исправленного поведения. `--revision` использует `git archive` во временном каталоге и не меняет рабочую копию. Аналогично `architecture_probe.py --revision 5cdd34e --observe --out /tmp/architecture_before` сохраняет baseline.

JSON-каталог обновляется только из канонического Python-реестра:

```bash
python3 AFrame/engine/frame_rules.py --export-catalog AFrame/engine/assembly_catalog.json
```

Сравнение учебных примеров хранится в `docs/atr_review_3009/logs/manual_examples.json`; запросы и исходные результаты — в `docs/facades_beginner_3009/assets/`. Проверка означает совпадение геометрии и количеств, не утверждение монтажного решения.
