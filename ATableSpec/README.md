# ATableSpec — спецификации из блоков AutoCAD без СПДС

Плагин AutoCAD: читает выбранные блоки (стойки, ригели, заполнения, створки,
кронштейны, ограждения), агрегирует во внешнем Python-движке и вставляет
ведомость таблицей в чертёж. Команда — **`ATSPEC`**.

Архитектура «гибрид»: тонкий C#-плагин в AutoCAD + движок `dxf_spec` на Python.
Вся прикладная логика (какие слои, какие атрибуты, какие ведомости) — в
`engine/mapping.yaml`, а не в коде. Подробности — в `docs/`.

## Состав

```
AutoCAD/
  src/AtSpecPlugin/      C#-плагин (.NET Framework 4.8, AutoCAD 2013–2024)
  engine/                движок dxf_spec.py + mapping.yaml
  bundle/.../PackageContents.xml   манифест автозагрузки
  build.ps1              сборка: DLL + exe -> готовый бандл + zip
  docs/                  инструкция по установке (PDF)
```

## Сборка (делает разработчик, один раз, на Windows)

Нужны: AutoCAD 2021, .NET SDK (+ таргет-пак .NET Framework 4.8), Python 3.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```
Если AutoCAD стоит не по стандартному пути:
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -AcadDir "D:\...\AutoCAD 2021"
```
Результат: `dist\ATableSpec.bundle.zip` — это и есть файл, который отдаётся
конструктору.

## Установка (делает конструктор, копированием)

1. Распаковать `ATableSpec.bundle.zip`.
2. Папку `ATableSpec.bundle` положить в
   `%APPDATA%\Autodesk\ApplicationPlugins\`.
3. Запустить AutoCAD, в чертеже набрать `ATSPEC`, выбрать блоки, указать точку
   вставки таблицы.

Полная пошаговая инструкция с картинками путей — `docs/`.

## Поддержка версий AutoCAD

- **2013–2024** (.NET Framework) — текущая сборка `net48`, одна DLL на весь
  диапазон.
- **2025+** (.NET 8) — добавляется вторая сборка `net8`; ветка в
  `PackageContents.xml` уже заготовлена. Движок `dxf_spec.exe` — один на все
  версии (отдельный процесс).

## Изменения под новые задачи

- Новая ведомость / группировка / сортировка → блок в `reports` в `mapping.yaml`.
- Новый тип элемента, слой, теги атрибутов → `element_layers` / `field_map` там же.
- Принципиально новая задача → отдельный модуль Python поверх того же JSON-контракта;
  C# и установка не меняются.

Менять `mapping.yaml` можно без пересборки DLL — но при заморозке движка
PyInstaller'ом конфиг кладётся в бандл рядом с `dxf_spec.exe`
(`Contents/engine/mapping.yaml`); правят именно его.
