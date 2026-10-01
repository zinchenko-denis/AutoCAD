# Предлагаемый общий API 2Б3

01.10.2026, подготовка read-only до Windows CI 2Б2. Этот API — граница между тремя исполнителями, не продуктовый код. .NET Framework 4.8, C# совместимый с действующими Mono/Windows gates. Namespace `AFramePlugin`.

## Обязательные стабильные решения

- Файл чистого ядра: `AFrame/src/AFramePlugin/FrameNodeGeometry.cs`.
- Все входы строгие: полноценная проверка `FrameSolutionSelection`, а не сравнение одного hash. Новый input принимает только известные ключи/схему/базу. Старый каталог и его `revision` не меняются, включая исторический `checked_by_this_increment=false` (это область 2Б1). Новый результат имеет собственное coverage.
- UI не вычисляет суммы/зазор и не интерпретирует PDF. Команда перед вставкой повторно вызывает тот же Evaluate на утверждённых detached inputs.
- Форматные ошибки → `FrameNodeGeometryException` с Code. Геометрические противоречия → результат `CanInsert=false`, Issues с числами. Неполнота → результат с Missing, допускающий только честную частичную схему при наличии хотя бы одной известной ненулевой плоскости/слоя помимо основания.
- `FrameNodeGeometryInput` mutable как существующие DTO; Clone/FromDict/ToDict всегда проверяют и отделяют данные. Results/planes/bands/issues/drawing primitives — immutable после создания; списки ReadOnlyCollection, ToDict detached.
- Все имена ниже согласовываются до параллельных edits, затем не менять без уведомления UI/CAD.

## Input

```csharp
public sealed class FrameNodeGeometryException : InvalidOperationException {
    public string Code { get; private set; }
    public FrameNodeGeometryException(string code, string message);
}

public sealed class FrameNodeClearanceSurface {
    public const string Unknown = "unknown";
    public const string Layers = "insulation_outer_declared";
    public const string Membrane = "membrane_outer_declared";
    public string kind = Unknown;
    public double? membrane_outer_x_mm;
}

public sealed class FrameNodeGeometryInput {
    public string schema = "aframe_node_geometry_input/1";
    public string unit = "mm";
    public string basis = "wall_structural_face_x0_outward_positive";
    public double? profile_near_face_x_mm;
    public FrameNodeClearanceSurface clearance_surface = new FrameNodeClearanceSurface();
    public static FrameNodeGeometryInput CreateDefault(); // no hidden coordinates
    public static FrameNodeGeometryInput FromDict(object value); // null is malformed
    public Dictionary<string, object> ToDict();
    public FrameNodeGeometryInput Clone();
}
```

Строгие значения: x ГП null или finite > 0. Наружная мембрана null для Unknown/Layers, finite >= 0 для Membrane. Unknown не превращается в Layers автоматически. Layers при пустом списке остаётся объяснимым `CanInsert=false`/issue (выбранная поверхность не определена), не молчаливой нулевой границей. Любой известный x в результате — мм от структурной поверхности основания наружу.

## Result и Evaluate

```csharp
public sealed class FrameNodePlane {
    public string Id { get; }       // wall, insulation_1 ... insulation_N,
                                   // membrane_outer, profile_near, cladding_front
    public string Title { get; }
    public double XMm { get; }
    public string Basis { get; }    // structural_origin, project_declared,
                                   // local_declared, derived_from_declared_layers
}
public sealed class FrameNodeLayer {
    public int Index { get; }       // 1-based
    public double StartXMm { get; }
    public double EndXMm { get; }
    public double ThicknessMm { get; }
}
public sealed class FrameNodeDimension {
    public string Id { get; }       // layer_1 ... layer_N, cladding_offset,
                                   // profile_offset, membrane_offset, local_clearance
    public string Title { get; }
    public double FromXMm { get; }
    public double ToXMm { get; }
    public double ValueMm { get; }  // computed by Evaluate, never by renderer
    public string ValueText { get; }// exact decimal text; renderer labels use this
    public string Basis { get; }
}
public sealed class FrameNodeIssue {
    public string Code { get; }
    public string Message { get; }  // Russian; includes relevant numbers
    public string Severity { get; }// error or incomplete
}
public sealed class FrameNodeClearanceRule {
    public string SourceId { get; }
    public string SourceSha256 { get; }
    public int PdfPage { get; }     // 20
    public string Sheet { get; }   // 4.2.1
    public string From { get; }    // outer_insulation_membrane_surface
    public string To { get; }      // nearest_profile_surface
    public double MinimumMm { get; } // existing catalogue rule, 20
}
public sealed class FrameNodeGeometryResult {
    public string Schema { get; }  // aframe_node_geometry_result/1
    public string AlgorithmRevision { get; }
    public string SelectionDigest { get; }
    public string InputDigest { get; } // hash of algorithm + validated selection + input
    public string ResultDigest { get; } // canonical ToDict excluding ResultDigest
    public bool CanInsert { get; }
    public string Status { get; }  // partial, clearance_pass, rejected
    public string ClearanceStatus { get; } // not_evaluated, pass, fail
    public double? GapMm { get; }
    public string GapText { get; } // exact decimal text or null; never rounded PASS
    public double? InsulationOuterXMm { get; }
    public double? CladdingFrontXMm { get; }
    public double? ProfileNearFaceXMm { get; }
    public double? ClearanceSurfaceXMm { get; }
    public FrameNodeClearanceRule ClearanceRule { get; }
    public ReadOnlyCollection<FrameNodePlane> Planes { get; }
    public ReadOnlyCollection<FrameNodeLayer> Layers { get; }
    public ReadOnlyCollection<FrameNodeDimension> Dimensions { get; }
    public ReadOnlyCollection<FrameNodeIssue> Issues { get; }
    public ReadOnlyCollection<string> Missing { get; } // Russian user-facing reasons
    public ReadOnlyCollection<string> Limitations { get; }
    public Dictionary<string, object> ToDict();
    public string ReviewText(); // shared numerical explanation; no UI/CAD access
}

public static class FrameNodeGeometry {
    public const string AlgorithmRevision = "vector1_2015_4_2_1_planes/1";
    public static FrameNodeGeometryResult Evaluate(
        FrameSolutionSelection selection, FrameNodeGeometryInput input);
    public static string FormatMm(double value); // invariant deterministic, enough
                                               // precision for 19.999/20/20.001
}
```

`Status=clearance_pass` означает только локальный просвет. При отсутствии наружной плоскости облицовки этот PASS не скрывает Missing. Нельзя выводить `узел годен` по Status. `CanInsert` запрещён при противоречии, непригодном выборе явной поверхности и полностью пустой схеме. Изменение длины КР2/УК не меняет плоскости, хотя SelectionDigest сохраняет полное происхождение.

## Детерминированный чертёж

Чистое ядро предоставляет semantic planes/layers/dimensions. Отдельный CAD-независимый `FrameNodeDrawing.cs` (владение исполнителя CAD/renderer) строит раскладку простых line/text primitives. Команда создаёт именно этот список, а UI может показывать тот же список при необходимости. Никаких скрытых размеров из эскиза или кронштейна. Примитивы только в локальной плоскости XY, мм, без произвольного масштабирования геометрического X. Ядро FrameNodeGeometry не зависит от файла renderer; численные native tests компилируют его отдельно.

```csharp
public sealed class FrameNodePrimitive {
    public string Kind { get; }     // line or text
    public string Role { get; }     // wall, insulation, profile, cladding,
                                   // membrane, dimension, leader, note
    public double X1 { get; }
    public double Y1 { get; }
    public double X2 { get; }        // line only; text = X1
    public double Y2 { get; }        // line only; text = Y1
    public string Text { get; }     // text only, plain text, not MText escapes
    public double TextHeight { get; }// text only
    public double TextWidth { get; }// text box width, text only
    public Dictionary<string, object> ToDict();
}
public sealed class FrameNodeDrawing {
    public ReadOnlyCollection<FrameNodePrimitive> Primitives { get; }
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public string Digest { get; }
    public Dictionary<string, object> ToDict();
}
public static class FrameNodeDrawingBuilder {
    public static FrameNodeDrawing Build(FrameNodeGeometryResult result,
        IEnumerable<string> captionLines);
}
```

Drawing.Digest доказывает ожидаемую разметку на момент создания; для проверки фактического CAD-тела адаптер хранит и заново вычисляет свой `cad_content_digest` по реальным объектам/координатам/текстам. Не выдавать сохранённый Drawing.Digest за наблюдение текущего DWG. CAD-адаптер экранирует `\\`, `{`, `}` при записи plain Text в MText и не теряет численные подписи. Сами layout offsets/text height — оформление, не нормы альбома.

## Чистый паспорт снимка

Чтобы CAD не дублировал сериализацию и не принимал произвольное сохранённое `pass`, ядро даёт immutable passport. Он хранит входы и digest результата; при чтении пересчитывает настоящий Evaluate. Полную геометрию второй раз хранить необязательно.

```csharp
public sealed class FrameNodeSnapshot {
    public string Schema { get; }  // aframe_node_snapshot/1
    public string CreatedUtc { get; }
    public FrameSolutionSelection Selection { get; } // detached clone on access
    public FrameParameterContext Context { get; }    // detached clone on access
    public FrameNodeGeometryInput Input { get; }     // detached clone on access
    public FrameNodeGeometryResult Result { get; } // recomputed by Create/FromDict
    public string SnapshotDigest { get; }
    public static FrameNodeSnapshot Create(FrameSolutionSelection selection,
        FrameParameterContext context, FrameNodeGeometryInput input, string createdUtc);
    public static FrameNodeSnapshot FromDict(object value); // strict; reevaluate
    public Dictionary<string, object> ToDict();
    public ReadOnlyCollection<string> CaptionLines(); // identity/source/revisions/
                                                     // snapshot/partial/limits
}
```

CreatedUtc передаёт команда в invariant UTC `DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)`; ядро не читает часы и проверяет UTC ISO-формат. Context.ValidateSelection(selection) обязателен: равные числа не подменяют происхождение. Create отказывает на Result.CanInsert=false, FromDict отклоняет неизвестный algorithm/неверные digests/подменённый snapshot. ToDict строго содержит schema, algorithm_revision, created_utc, selection, context, input, input_digest, result_digest, snapshot_digest; `snapshot_digest` исключает себя, включает всё остальное. Persisted pass-флага нет.

## Форма: единственная зависимость команды от UI

```csharp
public sealed class FrameNodeForm : System.Windows.Forms.Form {
    public FrameNodeForm(FrameSolutionSelection selection,
        FrameParameterContext parameterContext,
        FrameNodeGeometryInput initialInput = null);
    public FrameNodeGeometryInput Result { get; } // detached; null until OK
    public FrameNodeGeometryResult Geometry { get; } // validated preview; optional
}
```

Форма клонирует selection/context, вызывает Context.ValidateSelection. Отсутствующий initialInput заменяется CreateDefault (unknown/null), не проектными выдуманными координатами. Сверху текущая зона/ревизии, слои/вынос с origins; ниже X ГП и явно выбранная поверхность. `Проверить схему` вызывает Evaluate и показывает ReviewText. `Вставить схему` даёт OK только при CanInsert, после реального просмотра актуальных введённых значений. Изменение любого input сбрасывает старый preview/OK. Cancel и закрытие окна не записывают ничего.

Действие Проверить не требует второго редактора: команда читает паспорт, проверяет CAD/source и пишет ясный подробный результат в командную строку/readonly message. При необходимости UI может добавить простой readonly просмотр с отдельным API; на реализацию первого инкремента он не влияет.

## Команда/хранилище: ответственность CAD-исполнителя

`FrameNodeCommand.Node()` зарегистрирован `[CommandMethod("ATFNODE", CommandFlags.Modal)]`. Действия Create/Check в командной строке, русские global/local keywords по существующим привычкам. Одно создание = одна каноническая привязанная зона и один BlockReference. Нет новой групповой семантики.

Хранилище можно оставить в AFrame `FrameNodeStore.cs`, а не в Common: типы узла специфичны для AFrame, остальные фасадные плагины их не используют. Предлагаемая внутренняя граница (CAD-агент может уточнить сам, поскольку UI/core её не вызывают):

```csharp
internal sealed class FrameNodeStored {
    internal FrameNodeSnapshot Snapshot;
    internal ObjectId OwnerId, ZoneId;
    internal string OwnerHandle, ZoneHandle, ZoneFingerprint;
    internal string RecordDigest, CadContentDigest;
    internal string UnitDeclaration; // database_mm or user_confirmed_mm
}
internal static class FrameNodeStore {
    internal const string Key = "AFRAME_NODE_GEOMETRY";
    internal static FrameNodeStored Read(Transaction tr, BlockReference owner);
    internal static void Write(Transaction tr, BlockReference owner, Hatch zone,
        string zoneFingerprint, FrameNodeSnapshot snapshot,
        string unitDeclaration, string cadContentDigest);
    internal static string CadContentDigest(Transaction tr, BlockReference owner);
}
```

Read проверяет собственный handle + self soft-pointer, зону по прямой ссылке, целостность строгой envelope/schema, raw JSON digest/размер. Write только внутри caller transaction, без скрытого NOD, без сканирования чертежа. Не выдавать отсутствие/повреждение/копию за одно состояние. Freshness проекта/зоны — сравнение настоящих raw records, контекста, поколения геометрии, не только текущих численных плоскостей. За пределами pure passport хранится CAS/control информация конкретного DWG, не смешанная с инженерным результатом.

## Проверочные точки между исполнителями

1. Core объявляет перечисленные DTO/API и закрепляет JSON fixtures до подключения UI/CAD.
2. UI компилируется с core+existing selection/project; native probe не требует CAD.
3. CAD doubles используют настоящие core/UI interfaces, никакого второго геометрического алгоритма.
4. Command перед commit повторно читает проект/зону новым cache и проверяет ZoneGeometryGuard; после этого создаёт Snapshot, Drawing и CAD в одной транзакции.
5. Независимые тесты 19,999/20/20,001,180/[150,50], malformed, stale/cancel/COPY/unit/scale и O(L) по счётчикам сохраняют различие между локальным PASS и допуском всей системы.
