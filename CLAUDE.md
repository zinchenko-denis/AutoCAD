# AutoCAD — плагины для фасадов

Этот файл Claude Code читает сам в начале каждой сессии (локальной и облачной).
Общение — по-русски, простыми словами, без внутренних кодов; числа сначала
проверить, потом писать. Честность важнее лести.

## Что где
- Рабочая ветка — `feat/auto-reactor` (`main` сильно отстаёт; слияние в `main`
  — ТОЛЬКО по слову Дениса «Пушим»).
- Фасадные модули (в работе): `Facades/` (ATFZONE — зоны), `AClad/` (ATTILE —
  раскладка облицовки; ATCLAD — старая, только для старых чертежей),
  `AFrame/` (ATFRAME — подсистема НВФ и кляммеры).
- НЕ трогать без прямой просьбы: `ATableSpec/`, `ABlockGen/` (светопрозрачные,
  конструктор Алексей). `Facades/` — по замечаниям Германа или по просьбе Дениса.
- Журнал и договорённости модуля — `<модуль>/docs/NEXT.md`, раздел «Статус»
  (новое — сверху). Начинать сессию с него. Разбор кода 23.09 и что
  исправлено — `docs/REVIEW_23.09.md`.
- Тестовые данные — отдельный репо `zinchenko-denis/atspec-testdata` (в
  облачной сессии подключить его вторым репозиторием; без него часть тестов
  пропускается).

## Проверки (перед каждым коммитом кода)
```bash
export PYTHONUTF8=1
(cd Facades/engine && python3 test_facade_zones.py && python3 test_facades_engine.py)
(cd AClad/engine && python3 test_cladding_plan.py && python3 test_clad_engine.py && python3 test_tile_pattern.py && python3 audit_clad.py)
(cd AFrame/engine && python3 test_frame_plan.py && python3 test_frame_calc.py && python3 test_frame_engine.py && python3 audit_frame.py)
python3 tools/xmod_check.py --no-fixture      # стыки модулей (ATSPEC X6 — не фасады, известен)
python3 AFrame/tools/frame_synth.py --quick   # синтетика подсистемы (F16 — известное, «только кляммеры»)
python3 Facades/tools/zones_synth.py --quick
python3 AClad/tools/attile_synth.py --quick
```
C# (AutoCAD) здесь не компилируется: только `mcs --parse <файл>`; окна
ATTILE/ATFRAME — прогон под mono (`AClad/tools/attile_ui`, `AFrame/tools/frame_ui`,
команды сборки в шапке UiCheck.cs). Настоящая компиляция — CI `check` на
каждый push; дождаться зелёного, при красном — прочитать аннотации.

## Сборка («Собираем» — только по слову Дениса)
- Облако (Claude Code cloud): `gh workflow run build.yml --ref <ветка>`, затем
  `gh run list --workflow build.yml --limit 1` → `gh run watch <id> --exit-status`.
  Пушить в другие ветки (build-trigger, main) из облака нельзя — прокси пускает
  push только в рабочую ветку сессии. Если запуск workflow прокси отклонит —
  попросить Дениса нажать «Run workflow» в GitHub Actions (build-bundle, ветка).
- Локально / claude.ai с токеном: ветка `build-trigger` = рабочая ветка + патч
  `branches: [ main, build-trigger ]` в build.yml, force-push (подробно —
  `AClad/docs/NEXT.md`, записи «Собираем»).
- Выкладку проверять СКАЧИВАНИЕМ: `gh release download build-<N>` и `latest`,
  архивы должны совпасть байт в байт; DLL и движки внутри — новые.
- К сборке — PDF Герману (генераторы `*/docs/make_build*_*.py`), в конце
  записи в NEXT.md.

## Коммиты
`<область> (ДД.ММx): что сделано` — область: движок / форма / нвф / docs / ci.
Подписи не ставить. Токены, пароли — никогда в файлы, коммиты и логи.

## Облачная сессия: GitHub
- Токен в облаке НЕ нужен и не поможет: весь GitHub идёт через прокси Anthropic,
  он пускает только репозитории, подключённые к сессии при старте, а push —
  только в рабочую ветку сессии.
- Ошибка «not in this session's authorized repository set» / «Use add_repo» —
  репозиторий не подключён к сессии. Изнутри не лечится: попросить Дениса
  открыть новую сессию с этим репозиторием (и atspec-testdata) в списке.
- Работа облачной сессии приходит отдельной веткой — её сливают в
  `feat/auto-reactor` (PR или локально).
- Окружение: `tools/cloud_setup.sh` (вставить в поле Setup script окружения).
