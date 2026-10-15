# Проверка выпуска 15.10

Дополнение к двум прежним инспекторам проверяет новые файлы и метаданные,
которые они ещё не охватывают: встроенный модуль `frame_corners`, обработчики
ожидания меню и реальные IL-присваивания условных 50/0 в `FrameSettings`.
DLL/EXE не исполняются. Эта проверка не заменяет нативный AutoCAD.

Зависимости отдельного окружения проверяющего: Python 3.12,
`pyinstaller==6.22.3`, `dnfile==0.18.0`, `dncil==1.0.2`.

Сначала дождаться успешных `build-bundle` и `check` на одном точном SHA.
Запуск сборки — только штатный `workflow_dispatch` на `feat/auto-reactor`
после новой команды Дениса «Собираем». В `publication_evidence.json`
сохранить наблюдения GitHub API по схеме прежнего инспектора:
`build_workflow`, `check_workflow`, `build_tag_sha`, `main_before`, `main_after`,
`latest_release` и `immutable_release` с именами/размерами/digest активов.

Команды из корня репозитория (номер, SHA и пути передать фактические):

```sh
python tools/release_1510/download_release.py --build 112 --out /path/to/downloads
python tools/release_0110/verify_bundles.py --downloads /path/to/downloads --build 112 --sha FULL_SHA --source-ref FULL_SHA --ref feat/auto-reactor --publication-evidence /path/to/publication_evidence.json --out /path/to/base_receipt.json
python tools/pilot_release_0110/verify_release_extension.py --downloads /path/to/downloads --build 112 --source-sha FULL_SHA --base-receipt /path/to/base_receipt.json --out /path/to/pilot_receipt.json
python tools/release_1510/verify_current_release.py --downloads /path/to/downloads --build 112 --source-sha FULL_SHA --base-receipt /path/to/base_receipt.json --pilot-receipt /path/to/pilot_receipt.json --publication-evidence /path/to/publication_evidence.json --out /path/to/current_receipt.json
```

Скачивание использует публичные официальные release URL через `curl` и
сохраняет HTTP-код, размер, SHA-256 и исходный URL каждого файла. Секреты и
временные подписанные redirect URL не сохраняются. Получаются пять ZIP,
две DOCX и `Fixes_2026-10-15_buildN.txt` из обоих тегов; архивы остаются вне Git.

Дополнение принимает только два прежних PASS для тех же байтов и SHA:

- документы `latest`/`build-N` совпадают, соответствуют исходному коммиту
  и API digest; DOCX проходят CRC, содержат ссылку на этот выпуск;
- в трёх DLL действительно присутствуют реализации `Init`, `OnIdle`,
  `CancelPending`; в AClad — setter с сохранением ошибки, в AFrame —
  методы проверки/геометрии швеллера;
- в `EngineParams` декодируются инструкции `ldstr` → `ldc.r8` →
  `box System.Double` → `set_Item`: ключи наружного свеса и смещения по Y
  имеют значения 50.0 и 0.0. Наличие этих присваиваний не утверждает
  правильность всех ветвлений; поведение проверяется отдельно стендом;
- `frame_corners` имеется в PYZ настоящего EXE и извлекается непустым
  с `raw=True`, без импорта/исполнения модуля. Хеш байткода не называется
  доказательством его эквивалентности исходному `.py`.

Ошибки дают FAIL и ненулевой код. Положительный самоконтроль инспектора
выполнен на DLL текущего кода; DLL до согласования 50/0 отклонена из-за
отсутствующих присваиваний. Подробные квитанции хранить вне исходников либо
в артефактах, краткий итог выпуска — в `docs/CURRENT_MILESTONES.md`.
