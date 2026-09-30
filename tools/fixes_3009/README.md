# Проверки фасадных исправлений 30.09.2026

Из корня репозитория. Подробный результат — [docs/FIXES_30.09.md](../../docs/FIXES_30.09.md). Исполняемый код этих проверок не изменяет исходные DWG, не устанавливает плагины и не публикует выпуск.

## Регрессии

```bash
python -m pip install shapely openpyxl
python tools/fixes_3009/run_facade_regressions.py --out /tmp/facades-regressions
```

Для полного набора нужны `mono` и `mcs` в PATH: исполняются WorkStatement, методы связей контуров, метаданные направляющих, LayoutSafety и FrameSettings. Проверки C# используют CAD doubles там, где требуется база AutoCAD. Нет Mono — код возврата2, а не успешная проверка. Папка `--out` содержит журналы и JSON-манифест; отсутствие `--out` создаёт временную папку.

Windows CI отдельно компилирует плагины через их csproj, а этот runner запускает с `--python-only`. Этот флаг явно исключает C#-пробы, он не равен полной локальной проверке. Python-тесты используют независимую Shapely только как оракул; продуктовые фасадные движки остались на стандартной библиотеке.

В `test_supported_workflows.py` три положительных расчётных контроля проверяют нормальные построения. Отказы не засчитываются в них как успех. `test_frame_rail_metadata.py` исполняет реальные методы C# с минимальными заменами API. `CladSafetyCheck.cs` проверяет чистую логику, используемую обеими командами раскладки.

Дополнение по актуальности геометрии: `test_zone_geometry.py` исполняет чистое сравнение контуров; `test_zone_geometry_adapter.py` — настоящие guards с CAD API doubles и свежими ответами `facades_engine`; `test_frame_geometry_consumer.py` — проверку осей, частей зоны и полноты выбора в AFrame. `test_clad_label_cleanup.py` исполняет настоящий метод RemoveData и проверяет очистку старых снимков при переключении раскладки (41/41). Первые два набора также подключены отдельными .NET Framework шагами в Windows CI. Сборка плагинов включает linked sources `Common/*.cs`, их хеши входят в манифест компиляции. Подробнее: [контракт и ограничения](../../docs/fixes_3009/Geometry.md) и [геометрические случаи](test_zone_geometry_README.md).

## Компиляция с настоящими ссылками AutoCAD

Предпочтительный путь при установленном .NET SDK:

```bash
python tools/fixes_3009/compile_facades.py --out /tmp/facades-compile
```

Он вызывает существующие csproj трёх фасадных модулей. Проверенный альтернативный путь для Linux без dotnet:

```bash
python tools/fixes_3009/compile_facades.py --references /path/to/extracted-packages --out /tmp/facades-compile
```

Нужны Mono и распакованные официальные NuGet-пакеты в подкаталогах без номера версии:

| Каталог | Версия |
|---|---|
| autocad.net |24.0.0|
| autocad.net.core |24.0.0|
| autocad.net.model |24.0.0|
| microsoft.netframework.referenceassemblies.net48 |1.0.3|
| microsoft.net.compilers.toolset |4.8.0|

Используется Roslyn `tasks/net472/csc.exe`, net48 reference assemblies, настоящий AutoCAD API. Ссылки не подменяются заглушками. Манифест записывает SHA-256 всех скомпилированных исходников. Полученные DLL служат проверке компиляции; это не комплект бандлов и не подтверждение загрузки в AutoCAD. Пакеты и двоичные файлы инструментальной среды в репозиторий не включены.

## Полные сценарии

```bash
python Facades/tools/zones_synth.py
python AClad/tools/attile_synth.py
python AFrame/tools/frame_synth.py --seed 2909
python tools/roles_synth.py --seed 2909
```

Повторить последние две команды с seeds77 и5. Для ролей нужен Mono либо заранее собранный `AFrame/tools/roles/RolesDump.csproj` в переменной `ROLES_DUMP_EXE`. Штатные юнит-тесты и быстрые обязательные проверки перечислены в CLAUDE.md. Приватная фикстура не нужна для этих независимых регрессий; некоторые старые эталонные тесты требуют отдельно подключённый `atspec-testdata`.

Роли выводят `successful_frame_results` и каждый `safe_refusal/...` отдельно. Разрешены только точные поддержанные отказы с пустыми массивами элементов; неизвестная ошибка остаётся нарушением. Прежнее исключение F16 удалено. X6 в стыках относится к неизменённому ATSPEC и обозначается отдельно.
