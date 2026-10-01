using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AFramePlugin
{
    // A drawing plan contains only the planes already approved by the pure
    // evaluator. Decorative dimensions do not infer metal bodies or fixings.
    public sealed class FrameNodePrimitive
    {
        public string Kind { get; private set; }
        public string Role { get; private set; }
        public double X1 { get; private set; } public double Y1 { get; private set; }
        public double X2 { get; private set; } public double Y2 { get; private set; }
        public string Text { get; private set; }
        public double TextHeight { get; private set; } public double TextWidth { get; private set; }
        internal FrameNodePrimitive(string kind, string role, double x1, double y1, double x2, double y2,
            string text = null, double height = 0, double width = 0)
        { Kind = kind; Role = role; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Text = text; TextHeight = height; TextWidth = width; }
        public Dictionary<string, object> ToDict()
        { return new Dictionary<string, object> { { "kind", Kind }, { "role", Role }, { "x1", X1 }, { "y1", Y1 },
            { "x2", X2 }, { "y2", Y2 }, { "text", Text }, { "text_height", TextHeight }, { "text_width", TextWidth } }; }
    }
    public sealed class FrameNodeDrawing
    {
        public ReadOnlyCollection<FrameNodePrimitive> Primitives { get; private set; }
        public double MinX { get; private set; } public double MinY { get; private set; }
        public double MaxX { get; private set; } public double MaxY { get; private set; }
        public string Digest { get; private set; }
        internal FrameNodeDrawing(List<FrameNodePrimitive> primitives)
        {
            Primitives = primitives.AsReadOnly();
            MinX = primitives.Min(p => Math.Min(p.X1, p.X2)); MinY = primitives.Min(p => Math.Min(p.Y1, p.Y2));
            MaxX = primitives.Max(p => Math.Max(p.X1 + p.TextWidth, p.X2)); MaxY = primitives.Max(p => Math.Max(p.Y1, p.Y2));
            Digest = FrameParameterJson.Hash(Content());
        }
        private Dictionary<string, object> Content()
        { return new Dictionary<string, object> { { "schema", "aframe_node_drawing/1" }, { "primitives", Primitives.Select(p => p.ToDict()).ToArray() },
            { "min_x", MinX }, { "min_y", MinY }, { "max_x", MaxX }, { "max_y", MaxY } }; }
        public Dictionary<string, object> ToDict() { var d = Content(); d["digest"] = Digest; return d; }
    }
    public static class FrameNodeDrawingBuilder
    {
        public const int MaxPrimitives = 4096;
        public static FrameNodeDrawing Build(FrameNodeGeometryResult result, IEnumerable<string> captionLines)
        {
            if (result == null || !result.CanInsert) throw new FrameNodeGeometryException("E_NODE_REJECTED", "Нельзя рисовать отклонённую размерную схему.");
            var items = new List<FrameNodePrimitive>();
            Action<FrameNodePrimitive> add = p => {
                if (items.Count >= MaxPrimitives) throw new FrameNodeGeometryException("E_NODE_CAPACITY", "Схема превышает техническую ёмкость 4096 примитивов; данные не сокращаются.");
                items.Add(p);
            };
            Action<string, double, double, double, double> line = (r, a, b, c, d) => add(new FrameNodePrimitive("line", r, a, b, c, d));
            Action<string, double, double, string> text = (r, x, rowY, s) => { string wrapped = Wrap(s); add(new FrameNodePrimitive("text", r, x, rowY, x, rowY - Height(wrapped), wrapped, 3.5, 300)); };
            double furthest = result.Planes.Max(p => p.XMm), labelX = furthest + 20;
            if (labelX == furthest || double.IsInfinity(labelX)) throw new FrameNodeGeometryException("E_NODE_CAD_PRECISION", "Невозможно разместить подписи в точности CAD для этих координат.");
            double diagramHeight = Math.Max(100, result.Planes.Count * 20);
            text("note", 0, diagramHeight + 24, "Размерная схема заявленных плоскостей — не рабочий узел");
            double planeRow = diagramHeight;
            foreach (var plane in result.Planes)
            {
                string role = plane.Id == "wall" ? "wall" : plane.Id.StartsWith("insulation_", StringComparison.Ordinal) ? "insulation" :
                    plane.Id == "profile_near" ? "profile" : plane.Id == "cladding_front" ? "cladding" : "membrane";
                line(role, plane.XMm, 0, plane.XMm, diagramHeight);
                // Labels have their own rows; closely spaced exact planes are
                // never merged just to make text fit.
                double row = planeRow;
                line("leader", plane.XMm, row, labelX - 4, row);
                string label = plane.Title + "; x = " + FrameNodeGeometry.FormatMm(plane.XMm) + " мм";
                text(role, labelX, row + 2, label); planeRow -= Math.Max(20, Height(Wrap(label)) + 5);
            }
            foreach (var layer in result.Layers)
            {
                line("insulation", layer.StartXMm, 0, layer.EndXMm, 0);
                line("insulation", layer.StartXMm, diagramHeight, layer.EndXMm, diagramHeight);
                line("insulation", layer.StartXMm, diagramHeight * .15, layer.EndXMm, diagramHeight * .85);
            }
            double y = Math.Min(-18, planeRow - 18);
            foreach (var dimension in result.Dimensions)
            {
                line("dimension", dimension.FromXMm, y, dimension.ToXMm, y);
                line("dimension", dimension.FromXMm, y - 3, dimension.FromXMm, y + 3);
                line("dimension", dimension.ToXMm, y - 3, dimension.ToXMm, y + 3);
                string label = dimension.Title + " = " + dimension.ValueText + " мм";
                text("dimension", labelX, y + 3, label);
                y -= Math.Max(17, Height(Wrap(label)) + 6);
            }
            y -= 12;
            foreach (string caption in captionLines ?? new string[0]) { text("note", 0, y, caption); y -= Height(Wrap(caption)) + 7; }
            foreach (string missing in result.Missing) { string label = "Не задано: " + missing; text("note", 0, y, label); y -= Height(Wrap(label)) + 7; }
            return new FrameNodeDrawing(items);
        }
        private static double Height(string wrapped) { return (wrapped.Count(c => c == '\n') + 1) * 5.5; }
        private static string Wrap(string text)
        {
            // Explicit short rows in a wide MText box make a long zone name
            // affect layout height rather than obscure the following caption.
            var rows = new List<string>();
            foreach (string original in (text ?? "").Replace("\r", "").Replace("\t", " ").Split('\n'))
            {
                int offset = 0;
                while (original.Length - offset > 52)
                {
                    int split = original.LastIndexOf(' ', offset + 52, 53); if (split - offset < 16) split = offset + 52;
                    rows.Add(original.Substring(offset, split - offset)); offset = split;
                    while (offset < original.Length && char.IsWhiteSpace(original[offset])) offset++;
                }
                rows.Add(original.Substring(offset));
            }
            return string.Join("\n", rows);
        }
    }
}
