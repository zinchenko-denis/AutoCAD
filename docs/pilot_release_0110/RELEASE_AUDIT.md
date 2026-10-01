# Независимый аудит выпуска для проектного отдела — 01.10.2026

Проверен исходный срез `c4009838875aa0f225971153bf4b54fe288e8e79` после контрольной точки узла/ведомостей. Аудит read-only: продукт, workflow, манифесты и установленные программы не менялись; сборка/CI этим аудитом не запускались. Новая команда Дениса «Собираем» получена ведущим агентом. Ниже — условия именно проверочного установочного выпуска ограниченного пилота, не допуск полного инженерного комплекса.

## Вывод

Упаковочная схема подходит для имеющихся новых команд и AutoCAD 2024. Явного пропущенного runtime-файла по прочитанным исходникам не найдено. **До передачи нужны новый зелёный `check` на точном SHA выпуска, успешный `build-bundle`, скачивание и проверка архивов с расширенным перечнем новых типов/модулей.** Старый инспектор build-104 сам по себе не доказывает наличие изменений 2Б–2В и нового ATFNODE. Живой AutoCAD и запуск упакованных EXE пока не выполнены; статическая проверка ZIP не закрывает их.

## Версия Германа и совместимость

- В `README.md` прямо записано: Герман — **AutoCAD 2024**, Алексей — 2021. В `AClad/docs/NEXT.md`, запись 23.09j, повторено «у Германа 2024»; запись 23.09j/k описывает полученное видео окна ATTILE в живом AutoCAD 2024. Это подтверждённая история рабочего места, а не новая проверка текущего установленного обновления. Номер update/build AutoCAD в этих записях не указан; его фиксируют при новом человеческом проходе.
- Все три фасадных `.csproj`: `net48`, `PlatformTarget=x64`, AutoCAD.NET `24.0.0`, Microsoft.NETFramework.ReferenceAssemblies `1.0.3`. Все три фасадных XML: `OS=Win64`, `Platform=AutoCAD`, `SeriesMin=R24.0`, `SeriesMax=R24.3` — разрешённый диапазон AutoCAD 2021–2024.
- Autodesk официально указывает для AutoCAD 2024 поддержку Managed SDK 2021–2024 и .NET Framework 4.8: [About Managed .NET Compatibility](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm). Значит выбранные SDK/target и диапазон XML согласованы; это не гарантия поведения конкретного DLL без загрузки в host.
- .NET Framework 4.8 устанавливается AutoCAD 2024, если отсутствовал: [Installation Requirements](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-ReleaseNotes/files/installation/INSTALLATION_REQUIREMENTS_ONE_AUTOCAD_2024.html). На рабочем месте не требуется .NET SDK 8: он используется только Windows-сборщиком.
- Этот выпуск не заявляет поддержку Mac, AutoCAD LT или AutoCAD 2025+. Последний исключён действующим SeriesMax. Старые комментарии в XML «2013–2024» шире фактического фасадного диапазона и не должны переходить в новую инструкцию.

## Что изменилось после build-104 и как попадёт в ZIP

Build-104: исходный разрешённый срез `60f3b3587c8d09780e5c7a703f010de690f73e2b`; сборочный SHA `ec5160983f61ce5668369d2513313391db4d6c5d`; build run `36822697609`, check run `36822697554` — success. `docs/release_0110/receipt.json` подтверждает пять скачанных пар latest/build-104, одинаковых побайтно, состав ZIP, PE/CLR/PyInstaller. Сам EXE и DLL в AutoCAD тогда не запускались. Эта сборка содержит этап 2А, не последующие изменения.

| Изменения | Механизм включения | Проверка нового архива |
|---|---|---|
| Исторический выбор Вектор-1, проект и зоны | Новые `.cs` автоматически входят в SDK-проект AFrame; `Common/*.cs` явно связан с тремя фасадными проектами | Типы `FrameSolutionSelection`, `FrameProjectCommand`, `FrameProjectParameters`, `FrameParameterResolver`; общий `FacadeProjectParameterStore` |
| `ATFPROJECT`, `ATFZONEPARAMS`, `ATFNODE` | Все три команды уже объявлены в AFrame `PackageContents.xml`; загрузка DLL при старте | Точный XML собранного SHA и типы `FrameNodeCommand`, `FrameNodeStore`, `FrameNodeRenderer`, `FrameNodeGeometry`, `FrameMountingAssessment` |
| Python-валидация исторического выбора | `frame_engine`/`frame_plan` прямо импортируют `frame_solution_selection`, тот — `frame_solution_catalog`; PyInstaller получает `--paths AFrame/engine` | Наличие обоих новых модулей в PYZ; контрольный запуск замороженного движка по известному JSON при Windows-проверке |
| Исторический каталог | В C# — `private const string CatalogJson` в `FrameSolutionSelection`; в Python — `_CATALOG` в `frame_solution_catalog.py` | Версия/идентичность каталога в собранных данных; экспорт `Common/catalogs/vector1_2015_type1_historical.json` не является runtime-файлом и отдельно в ZIP не нужен |
| Изменения связей и ведомостей, перевод ПСК→МСК | `Common` и новые версии `ConnectionTableData`/`FacadeQuantityTableCommand` входят в AFacades DLL | Наличие текущих типов и метода `VerifyInsertionUcs` в метаданных DLL, build-info с точным SHA; сценарий таблиц подтверждён исходниками/CI, отрисовка проверяется в host |
| AClad | Собственные src/engine после104 не изменились, но изменился связанный `Common` | AClad DLL также должна быть текущей; поэтому для фасадов ставятся **все три бандла** одного выпуска |
| `systems.json`, формы работ/значки | AFrame `systems.json` копируется отдельно; AFacades `Contents/templates/*` и значки копируются workflow | Побайтовая сверка с Git; для XML/JSON/YAML допускается только явно зарегистрированный LF/CRLF |

По сравнению с build-104 в `build.yml` отличается только снятие `build-trigger` из push-веток; сама схема сборки/копирования осталась прежней. `workflow_dispatch` сохранён. Никакого обязательного нового `--add-data` по этим исходникам не выявлено: статические импорты должны захватываться PyInstaller, но их наличие всё равно проверяется после скачивания.

## Зависимости и границы установки

Workflow использует Windows Server 2022, .NET SDK `8.0.x`, Python `3.12.10`, PyInstaller `6.22.3`, hooks `2026.7`, ezdxf `1.4.4`, PyYAML `6.0.3`, openpyxl `3.1.5`. В `test` дополнительно устанавливается shapely без фиксированной версии; `pip` обновляется. Это зависимости сборки/проверки, не инструкция устанавливать их на компьютер проектировщика.

Фасадные Python-движки используют стандартную библиотеку и упаковываются в onefile EXE со своим Python. Внешний Python/pip не нужен. Для ATableSpec сторонние библиотеки также замораживаются в EXE. В архиве должен находиться только наш DLL, не reference DLL AutoCAD; последние предоставляет сам AutoCAD.

Установка для пилота: закрыть AutoCAD; сохранить предыдущие три папки фасадных бандлов вне `ApplicationPlugins`; распаковать **AFacades.bundle, AClad.bundle, AFrame.bundle одного номера** непосредственно в `%APPDATA%\Autodesk\ApplicationPlugins\`; не создавать лишний уровень вложенности и не оставлять рядом вторую копию того же бандла; запустить AutoCAD и проверить `build-info.json`/загрузку команд. При блокировке доверия использовать утверждённый доверенный путь/проверку издателя согласно настройкам отдела. Отключение `SECURELOAD` не является частью маршрута.

## Существенные риски выпуска

1. **Build не заменяет полный check.** Задача `test` в build запускает ограниченные engine/quick/Python-only проверки; в ней нет новых managed сценариев проекта, узла и таблиц из `check.yml`. Перед окончательной приёмкой нужен `check` точного SHA выпуска, а не только прежний check219 или похожий продуктовый SHA. `check.yml` допускает ручной запуск для документационного HEAD.
2. **Прежний инспектор недостаточен для нового состава.** `tools/release_0110/verify_bundles.py` в просмотренном срезе проверяет AFrame-модули только до `frame_catalog`; не требует `frame_solution_selection`/`frame_solution_catalog`, новые Project/Node/Mounting-типы, метод `VerifyInsertionUcs`. Его проверки ZIP/PE/источников нужно сохранить и дополнить адресными требованиями выше либо отдельным проверяющим шагом. Изменять продукт ради этого не требуется.
3. **Workflow всегда публикует пять бандлов.** `git diff ec516098..c400983 -- ATableSpec ABlockGen` пуст; их проекты не включают `Common/*.cs`, значит фасадные правки в их DLL не внедряются. Но повторная упаковка меняет build-info, временные метки и потенциально бинарные байты/транзитивные сборочные зависимости. Нельзя писать «архивы ATSPEC/ABlockGen побайтно прежние». Проектному отделу для этого фасадного пилота выдаются/устанавливаются три фасадных бандла; действующие ATableSpec/ABlockGen у пользователей обновления не требуют. Перезапись их latest — наблюдаемый побочный эффект общего workflow, исходники при этом не меняются.
4. **latest изменяемый, build-N — снимок.** `latest` исторически не указывает тегом на собранный код; источник определяют build-info/SHA и тег build-N. Новый номер узнать из API, не предполагать105. Скачивать обе публикации и сравнивать все пять пар.
5. **Не повторять номер ради «исправления» выпуска.** В workflow публикация immutable build-N выполняется только при `run_attempt == 1`. Rerun того же номера может обновить latest, оставив прежний build-N; тогда пары уже не обязаны совпасть. После исправления предпочтителен новый ручной запуск с новым номером. Не объявлять приёмку до успешной проверки совпадения.
6. **Границы доказательства.** ZIP/CLR/PYZ подтверждают комплектность, не работу загрузчика, антивируса, onefile-распаковки и AutoCAD API. Нужен конечный человеческий маршрут `docs/node_lifecycle_0110/host_acceptance.md`, включая save/open, COPY, SaveAs, Undo, отмены и замеры стадий. Монтаж2В, расчёт3А, автоподбор и полная комплектация остаются закрыты.

## Команды оператора выпуска и приёмки

Ни одна команда ниже в рамках аудита не запускалась. Выполняются ведущим агентом после фиксации точного среза и разрешения выпуска, которое уже получено. Если нужен ручной запуск, текущая схема позволяет рабочую ветку без изменения main/build-trigger:

```sh
gh workflow run check.yml --repo zinchenko-denis/AutoCAD --ref feat/auto-reactor
gh workflow run build.yml --repo zinchenko-denis/AutoCAD --ref feat/auto-reactor
gh run list --repo zinchenko-denis/AutoCAD --workflow build.yml --limit 5 --json databaseId,number,headSha,status,conclusion
gh run watch <BUILD_RUN_ID> --repo zinchenko-denis/AutoCAD --exit-status
```

Перед запуском и по API `head_sha` после запуска убедиться, что оба workflow взяли одни и те же необходимые исходники. Не двигать ветку во время выбора среза. Если API dispatch отсутствует, использовать уже разрешённую пользователем браузерную кнопку Run workflow. `main` не менять.

После успеха, пример PowerShell (переменные задаются по фактически полученным API-данным):

```powershell
$releaseNumber = <ФАКТИЧЕСКИЙ_НОМЕР>
$releaseSha = '<ПОЛНЫЙ_SHA_ВЫПУСКА>'
$downloadsRoot = 'C:\facade-release-check'
gh release download "build-$releaseNumber" --repo zinchenko-denis/AutoCAD --pattern '*.bundle.zip' --dir "$downloadsRoot\build-$releaseNumber"
gh release download latest --repo zinchenko-denis/AutoCAD --pattern '*.bundle.zip' --dir "$downloadsRoot\latest"
python tools/release_0110/verify_bundles.py --downloads $downloadsRoot --build $releaseNumber --sha $releaseSha --source-ref $releaseSha --ref feat/auto-reactor --publication-evidence publication_evidence.json --out release_receipt.json
```

Для данного инспектора собрать `publication_evidence.json`: `build_workflow`, `check_workflow` с success/точным SHA, метаданные двух релизов с assets/size/digest, `build_tag_sha`, `main_before`/`main_after`. При рабочей ветке обязательно передать `--ref feat/auto-reactor`, поскольку старое значение по умолчанию — build-trigger. Дополнительные проверки новых модулей/типов обязательны независимо от PASS старого инспектора.

Для Windows smoke можно использовать уже сохранённый вход без разработки нового сценария:

```powershell
& '.\AFrame.bundle\Contents\engine\frame_engine.exe' '.\docs\node_lifecycle_0110\after\engines\initial_frame_П-2\request.json' '.\frame_smoke_result.json'
```

Ожидается код0, `ok=true`, одна направляющая, три кронштейна этой одной зоны и `solution_report.physical_assignment=not_asserted`; сопоставить численные поля с сохранённым response.json, не GUID/временем. Это запуск движка, не AutoCAD. При невозможности выполнить его здесь оставить честный NOT_RUN и включить в маршрут на рабочем месте, а не объявлять выполненным.

## Критерий передачи

Передать отделу точные неизменяемые ссылки build-N на три фасадных ZIP, короткую инструкцию установки, текущий учебный DWG/DXF и конечный журнал host-приёмки. В квитанции сохранить SHA источников, номера обоих workflow, SHA256 скачанных файлов, состав и новые типы/модули, результаты smoke (либо NOT_RUN), исходный/конечный main, неизменность исходников ATableSpec/ABlockGen. Выпуск готов к проверочному использованию только после этих упаковочных gates; готовность полного инженерного комплекса этим не устанавливается.
