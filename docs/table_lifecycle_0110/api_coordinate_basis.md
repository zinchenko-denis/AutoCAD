# Точка вставки новой ведомости: основание API

Проверено 01.10.2026 по официальной документации Autodesk AutoCAD .NET 2026.
Исходный срез расследования — `16c7307ce42715721ee7835d2dc90955e2229781`.
Этот документ фиксирует семантику API и независимый координатный эталон.
Он не заменяет исполнимое воспроизведение команды или живую проверку AutoCAD.

В официальном C# примере [Define a User Coordinate System (.NET)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-096085E3-5AD5-4454-BF10-C9177FDB5979.htm)
после `Editor.GetPoint` значение `PromptPointResult.Value` сохраняется как
координаты текущей ПСК, затем отдельно преобразуется в МСК матрицей перехода
из текущей ПСК. Это конкретный managed-пример, строки 78–105 просмотренной
страницы. Расположенный ниже пример VBA/ActiveX использует другое API
`Utility.GetPoint` с обратным направлением преобразования; смешивать эти
контракты нельзя.

Справочники [Editor.GetPoint(PromptPointOptions)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_EditorInput_Editor_GetPoint_PromptPointOptions.html)
и [PromptPointResult.Value](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_EditorInput_PromptPointResult_Value.html)
сами не уточняют систему координат. Поэтому основание вывода — приведённый
выше конкретный C# пример, а не одно имя `Point3d` или общее правило API.
[Editor.CurrentUserCoordinateSystem](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_EditorInput_Editor_CurrentUserCoordinateSystem.html)
предоставляет текущую ПСК как `Matrix3d`.

Официальная [иерархия Table](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Table.html)
показывает наследование от `BlockReference`.
Унаследованное [BlockReference.Position](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BlockReference_Position.html)
задаёт точку вставки в МСК. Таким образом, передача необработанного результата
managed `GetPoint` в `Table.Position` не сохраняет выбранную мировую точку
при отличии текущей ПСК от МСК.

Независимый эталон: начало ПСК в МСК `(100, 200, 0)`, поворот вокруг Z на
90°, единичный масштаб. Точка ПСК `(3, 4, 0)` должна давать точку МСК
`(100 − 4, 200 + 3, 0) = (96, 203, 0)`. Прямая запись `(3, 4, 0)` оставляет
таблицу в другом месте. При единичной ПСК преобразование не меняет точку.

В исходном `FacadeQuantityTableCommand` новая таблица получает
`position.Value` без преобразования (строки 129–131, 174).
При обновлении позиция существующей таблицы читается из `table.Position`
(строка 112) и уже находится в МСК. Повторно преобразовывать её нельзя.

Существующий [контракт ATFNODE](../node_geometry_0110/cad_integration_preflight.md)
основан на том же managed-примере. `FrameNodeCommand` снимает ПСК перед
запросом, проверяет её после запроса и перед записью, затем переводит точку
ровно один раз. Этот приём можно ограниченно применить к новой ведомости:
снять ПСК перед `GetPoint`, отказаться при изменении во время ввода или
до первой записи, преобразовать успешный результат в МСК один раз.
Проверка неизменности ПСК — защитное решение проекта, а не утверждение,
что документация Autodesk требует именно такой способ отказа.

Обновление существующей таблицы и выдача только XLSX не требуют новой точки
вставки. Ориентация таблицы этим исправлением не переопределяется. Формы
не меняются. Живые UCS/DUCS, команды AutoCAD, CAD-транзакции и отображение
остаются отдельной приёмкой; инженерный допуск пилота **BLOCKED**.
