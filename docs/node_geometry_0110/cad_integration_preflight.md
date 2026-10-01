# ATFNODE: предварительная проверка CAD-интеграции

Дата: 01.10.2026. База: `75eb369ac8231a1cf6311238ea12785a82abba43`.
Только исследование; продуктовый код не изменён. Реализация ожидает закрытия CI 2Б2.

## Координаты: первичные источники Autodesk

- [Define a User Coordinate System (.NET)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-096085E3-5AD5-4454-BF10-C9177FDB5979.htm): конкретный C# пример после `Editor.GetPoint` преобразует `PromptPointResult.Value` из текущей UCS в WCS. Опорные строки опубликованного примера: 78–105.
- [Editor.GetPoint(PromptPointOptions)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_EditorInput_Editor_GetPoint_PromptPointOptions.html) и [PromptPointResult.Value](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_EditorInput_PromptPointResult_Value.html): справочные описания метода и возвращаемого значения сами не уточняют систему координат. Поэтому основание выбранной реализации — конкретный пример UCS выше, а не предположение по имени типа Point3d.
- [Editor.CurrentUserCoordinateSystem](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_EditorInput_Editor_CurrentUserCoordinateSystem.html): доступ к матрице текущей пользовательской системы.
- [BlockReference.Position](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BlockReference_Position.html): положение вставки задаётся в WCS.
- [BlockReference.BlockTransform](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BlockReference_BlockTransform.html): преобразование связывает локальную систему определения блока с WCS чертежа, включая вставку, нормаль, масштаб и поворот.
- [Convert Coordinates (.NET)](https://help.autodesk.com/cloudhelp/2025/PLK/OARX-DevGuide-Managed/files/GUID-0EFA65CC-C1AB-4B99-8159-C31602C1A5E8.htm): содержит общее правило WCS для .NET API с исключениями; для GetPoint используем более конкретный пример UCS, не распространяем общее правило механически.

Предложенный жизненный цикл точки: после закрытия формы снять текущую UCS непосредственно перед GetPoint, сравнить её с UCS после успешного запроса. При изменении — отказ без записей. Для неизменной матрицы преобразовать полученную точку в WCS; до записи снова подтвердить неизменность этой UCS. Вставлять обычный блок с единичным масштабом, положительной нормалью WCS Z и начальным углом 0. В подсказке явно сообщить, что схема располагается в плоскости XY МСК независимо от направления ПСК. Локальная X схемы — физические мм от основания наружу, не координаты фасада.

При проверке допускаются перенос и поворот в плоскости XY, а масштаб, зеркалирование, наклон, нечисловое преобразование — отказ. Машинная точность проверки матрицы не применяется к инженерному минимуму 20 мм. Численный тест матрицы должен проверять известные переносы и повороты; он не доказывает поведение живого AutoCAD при UCS/DUCS.

## Изоляция от ведомостей и ручного импорта

- `Common/FacadeQuantityStore.Frame.cs:IsFrameCandidate` и `FacadeQuantityStore.cs:ReadCore` используют точные ключи паспортов, а не имя блока. Новая схема не получает ATFRAME/ATFRAME_RAIL или quantity/manual stamps.
- Общий проход ReadCore по модели ищет только известные generated/manual stamps. Новый отдельный XRecord не делает схему деталью каркаса.
- `FrameCommand.cs:ATDEDUP` использует префикс слоя `_01_ПС_`. Поэтому утверждены отдельные слой `_01_УЗЛЫ_СХЕМЫ` и префикс определения `AFNODE_SCHEMA_`; схему не размещать на слое подсистемы.
- Выбор по слоям в `FacadeQuantityTableCommand.PickLayers` отделяет слои зарегистрированных объектов от остальных. Явно выбранная произвольная схема может честно появиться среди неучтённых объектов; она не должна автоматически становиться зарегистрированной деталью.
- `ManualQuantityGeometry.ReadDefinition` допускает многопримитивный простой блок как штучный символ; `ManualQuantitiesCore.EvaluateCore` дополнительно требует пользовательское подтверждение одной физической детали. Без нового exact-key guard пользователь мог бы ошибочно зарегистрировать всю схему как один кронштейн.
- Root разрешил после GO добавить только точный ключ `AFRAME_NODE_GEOMETRY` в запрет ручной перерегистрации `ManualGeneratedKeys/ManualGeneratedConflict`, с настоящей mapper-регрессией. Это не новый quantity source key, не изменение старых hash/schema и не отключение регистрации произвольных ручных блоков.

## Проверка собственного тела

Носитель имеет отдельный строгий XRecord с self/definition/zone soft pointers, собственными handle и fingerprint зоны. Проверять точное определение блока, его единицы, отсутствие динамического блока и любых AttributeReference на вставке. Тело допускает только создаваемые renderer типы Line/MText. Отпечаток наблюдается по реальным примитивам: тип, координаты, текст, поворот/высота/ширина текста, нормаль, слой/цвет и значимое оформление. Число примитивов либо сохранённый Drawing.Digest не заменяют наблюдение текущего тела.

Утверждён технический предел 4096 примитивов. Превышение даёт явный отказ, а не усечение и не инженерную норму. Проверка читает только выбранный блок, его собственное определение, прямую ссылку на зону и проект/привязку. Перечислять ModelSpace для ATFNODE нельзя; открытие таблицы блоков/текущего пространства для атомарной вставки допустимо.

Живые сценарии COPY, Undo, UCS/DUCS, масштабирования и вставки остаются отдельной будущей приёмкой. Эта подготовка не объявляет их пройденными.
