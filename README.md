# AutoCAD — надстройки и автоматизация

Сборник инструментов автоматизации AutoCAD. Каждая надстройка — в своей подпапке,
самодостаточна (исходники, сборка, инструкция).

## Инструменты

### ATableSpec — спецификации из блоков без СПДС
Подпапка [`ATableSpec/`](ATableSpec/). Плагин AutoCAD: читает выбранные блоки
(стойки, ригели, заполнения, створки, кронштейны, ограждения), агрегирует их
данные внешним Python-движком и вставляет ведомость таблицей в чертёж.
Команда — `ATSPEC`.

- Сборка и установка: [`ATableSpec/docs/ATableSpec_install.pdf`](ATableSpec/docs/ATableSpec_install.pdf)
- Краткое описание: [`ATableSpec/README.md`](ATableSpec/README.md)
- Архитектура и контракт обмена: [`ATableSpec/docs/HYBRID.md`](ATableSpec/docs/HYBRID.md)

Целевые версии AutoCAD: 2013–2024 (сборка net48); ветка под 2025+ (.NET 8)
заготовлена.

## Как скачать
Кнопка **Code → Download ZIP** вверху страницы, либо:
```
git clone https://github.com/zinchenko-denis/AutoCAD.git
```

## Статус
Пилот. Новые надстройки добавляются отдельными подпапками.
