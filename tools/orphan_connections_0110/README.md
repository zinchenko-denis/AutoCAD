# Проверка адресного предупреждения 2В2

Из корня репозитория, с Python, openpyxl и средой компиляции C# из
`tools/fixes_3009/README.md`:

```bash
python3 tools/orphan_connections_0110/reproduce_gap.py --out /tmp/orphan-check --expect fixed
```

Runner создаёт свежие запросы реального движка и отчёты действительного
`FrameQuantities`, проверяет их общим ядром, строит таблицу и XLSX.
Изменения typed DTO для retained/alias/legacy/границ строк — отдельные
контрольные варианты; исходные ответы движка не подправляются.

Для исходного состояния используйте отдельный checkout `1a6066d` и
сохранённую версию базового probe, не меняя текущую рабочую копию:

```bash
python3 docs/orphan_connections_0110/reproduction/baseline_reproduce.py --repo /path/to/baseline-checkout --out /tmp/orphan-before
```

Базовый replay проверяет хеши исходников и воспроизводит полный путь до
таблицы прежней версии. Итоговый runner поддерживает только исправленное
состояние: его новые диагностические счётчики не требуются старому продукту.

Windows UI получает свежий файл
`/tmp/orphan-check/two_same_coordinate_zones/report.json` через
`tools/mounting_contract_0110/test_ui.py --orphan-report` вместе с прежним
`--report` для общей опоры. Полный пример запуска находится в `check.yml`.
В CI сохраняются отчёты и снимки `connection-table-ui`.

Воспроизведение использует принятый движком резервный вход с null-шагами;
нынешний диалог ATFRAME такой ввод не создаёт. Числа теста и технические
ограничения длины ячейки не являются монтажными допусками. Ни один probe
не запускает живой AutoCAD и не подтверждает полный монтажный контракт.
