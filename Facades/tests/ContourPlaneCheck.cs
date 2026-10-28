// 28.10: контуры ATFZONE вне плоскости XY с Z = 0 (снимок Германа «Зона «БСбу-8»:
// поддерживается только плоскость XY с Z = 0»). Исполняется настоящий
// AFacadesPlugin.ContourPlane и настоящая проверка зоны ZoneGeometryGuard на
// CAD doubles. Независимые оракулы: явное отображение системы объекта с нормалью
// вниз (x → −x) и середины дуг, посчитанные до и после переноса. AutoCAD не запускается.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AFacadesPlugin;
using FacadeSafety;

public static class ContourPlaneCheck
{
    static int passed, failed;
    static readonly Database Db = new Database();
    static readonly StringBuilder Dump = new StringBuilder("{");

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { passed++; Console.WriteLine("PASS " + name); }
        else { failed++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? ": " + detail : "")); }
    }

    // Стена 6000×3000 с выпуклой дугой по верху (выпуклость 0,25) — в системе объекта.
    static readonly double[][] OcsPts = { new[] { 0.0, 0.0 }, new[] { 6000.0, 0.0 }, new[] { 6000.0, 3000.0 }, new[] { 0.0, 3000.0 } };
    static readonly double[] OcsBulges = { 0.0, 0.0, 0.25, 0.0 };

    static Polyline Make(Vector3d normal, double elevation, double[][] pts = null, double[] bulges = null)
    {
        var pl = new Polyline { Normal = normal, Elevation = elevation, Closed = true };
        pts = pts ?? OcsPts; bulges = bulges ?? OcsBulges;
        for (int i = 0; i < pts.Length; i++) pl.AddVertexAt(i, new Point2d(pts[i][0], pts[i][1]), bulges[i], 0, 0);
        Db.Add(pl);
        return pl;
    }

    // Середина дуги сегмента p1→p2 при выпуклости b (b > 0 — против часовой стрелки).
    static double[] ArcMid(double[] p1, double[] p2, double b)
    {
        return new[] { (p1[0] + p2[0]) / 2 + b * (p2[1] - p1[1]) / 2, (p1[1] + p2[1]) / 2 - b * (p2[0] - p1[0]) / 2 };
    }

    static double SignedArea(IList<double[]> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a[0] * b[1] - b[0] * a[1]; }
        return s / 2;
    }

    static List<double[]> Points(List<object> raw)
    {
        var result = new List<double[]>();
        foreach (var o in raw) result.Add((double[])o);
        return result;
    }

    static List<double> Bulges(List<object> raw)
    {
        var result = new List<double>();
        foreach (var o in raw) result.Add((double)o);
        return result;
    }

    static bool Near(double a, double b, double tol = 1e-9) { return Math.Abs(a - b) <= tol; }

    // Вид сверху, посчитанный независимо: нормаль вверх — как есть, нормаль вниз — x → −x и выпуклость с обратным знаком.
    static void ExpectedWorld(bool down, out List<double[]> pts, out List<double> bulges)
    {
        pts = new List<double[]>(); bulges = new List<double>();
        for (int i = 0; i < OcsPts.Length; i++)
        {
            pts.Add(new[] { down ? -OcsPts[i][0] : OcsPts[i][0], OcsPts[i][1] });
            bulges.Add(down ? -OcsBulges[i] : OcsBulges[i]);
        }
    }

    static bool SamePoints(IList<double[]> a, IList<double[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!Near(a[i][0], b[i][0]) || !Near(a[i][1], b[i][1])) return false;
        return true;
    }

    static bool SameNumbers(IList<double> a, IList<double> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!Near(a[i], b[i])) return false;
        return true;
    }

    // Середины дуг в мировых координатах: из исходной системы объекта (явное отображение) и из прочитанного контура.
    static bool SameArcMidpoints(bool down, IList<double[]> worldPts, IList<double> worldBulges)
    {
        for (int i = 0; i < OcsPts.Length; i++)
        {
            int j = (i + 1) % OcsPts.Length;
            var ocsMid = ArcMid(OcsPts[i], OcsPts[j], OcsBulges[i]);
            var expected = new[] { down ? -ocsMid[0] : ocsMid[0], ocsMid[1] };
            var actual = ArcMid(worldPts[i], worldPts[j], worldBulges[i]);
            if (!Near(expected[0], actual[0], 1e-7) || !Near(expected[1], actual[1], 1e-7)) return false;
        }
        return true;
    }

    static GeometryLoop Loop(IList<double[]> pts, IList<double> bulges)
    {
        return new GeometryLoop { Role = "outer", Points = new List<double[]>(pts).ToArray(), Bulges = new List<double>(bulges).ToArray(), Closed = true };
    }

    static void DumpCase(string name, IList<double[]> pts, IList<double> bulges)
    {
        if (Dump.Length > 1) Dump.Append(',');
        Dump.Append('"').Append(name).Append("\":{\"pts\":[");
        for (int i = 0; i < pts.Count; i++)
            Dump.Append(i > 0 ? "," : "").Append('[').Append(pts[i][0].ToString("R", CultureInfo.InvariantCulture))
                .Append(',').Append(pts[i][1].ToString("R", CultureInfo.InvariantCulture)).Append(']');
        Dump.Append("],\"bulges\":[");
        for (int i = 0; i < bulges.Count; i++)
            Dump.Append(i > 0 ? "," : "").Append(bulges[i].ToString("R", CultureInfo.InvariantCulture));
        Dump.Append("]}");
    }

    // Полный путь одного контура: классификация → чтение для движка → перенос → проверка зоны.
    static void Parallel(string name, Vector3d normal, double elevation, bool down, string describe)
    {
        var pl = Make(normal, elevation);
        string guardReason; GeometryLoop unused;
        Check(name + ": до переноса проверка зоны отказывает",
            !ZoneGeometryGuard.TryReadPolyline(pl, "outer", out unused, out guardReason) &&
            guardReason.Contains(pl.Handle.ToString()) && guardReason.Contains("не в плоскости XY с Z = 0"), guardReason ?? "");
        Check(name + ": классифицирован как параллельный плану", ContourPlane.Classify(pl) == ContourPlane.Kind.Parallel);
        string text = ContourPlane.Describe(pl);
        Check(name + ": описание «" + describe + "»", text == describe, text);

        var rawPts = new List<object>(); var rawBulges = new List<object>();
        ContourPlane.Read(pl, ContourPlane.Classify(pl), rawPts, rawBulges);
        var readPts = Points(rawPts); var readBulges = Bulges(rawBulges);
        List<double[]> expectedPts; List<double> expectedBulges;
        ExpectedWorld(down, out expectedPts, out expectedBulges);
        Check(name + ": вершины для движка — вид сверху", SamePoints(readPts, expectedPts));
        Check(name + ": выпуклости для движка — вид сверху", SameNumbers(readBulges, expectedBulges));
        Check(name + ": середины дуг совпадают с исходной фигурой", SameArcMidpoints(down, readPts, readBulges));
        double ocsArea = SignedArea(new List<double[]>(OcsPts));
        Check(name + ": обход сверху " + (down ? "обратный" : "прежний"),
            Near(SignedArea(readPts), down ? -ocsArea : ocsArea, 1e-6));
        DumpCase(name, readPts, readBulges);

        ContourPlane.MoveToWorld(pl);
        Check(name + ": после переноса нормаль вверх и Z = 0",
            Near(pl.Normal.X, 0) && Near(pl.Normal.Y, 0) && Near(pl.Normal.Z, 1) && pl.Elevation == 0.0);
        Check(name + ": после переноса контур на месте", ContourPlane.Classify(pl) == ContourPlane.Kind.World &&
            ContourPlane.Describe(pl) == "на месте");
        var afterPts = new List<double[]>(); var afterBulges = new List<double>();
        for (int i = 0; i < pl.NumberOfVertices; i++)
        {
            var p = pl.GetPoint2dAt(i); afterPts.Add(new[] { p.X, p.Y }); afterBulges.Add(pl.GetBulgeAt(i));
            var w = pl.GetPoint3dAt(i);
            if (!Near(w.X, p.X) || !Near(w.Y, p.Y) || !Near(w.Z, 0)) afterPts[i] = new[] { double.NaN, double.NaN };
        }
        Check(name + ": вид в плане не изменился (вершины)", SamePoints(afterPts, expectedPts));
        Check(name + ": вид в плане не изменился (дуги)", SameNumbers(afterBulges, expectedBulges) &&
            SameArcMidpoints(down, afterPts, afterBulges));
        GeometryLoop loop; string reason;
        bool accepted = ZoneGeometryGuard.TryReadPolyline(pl, "outer", out loop, out reason);
        Check(name + ": проверка зоны принимает перенесённый контур", accepted, reason ?? "");
        string why;
        Check(name + ": снимок зоны совпадает с данными движка", accepted &&
            GeometryFingerprint.Equivalent(new[] { Loop(readPts, readBulges) }, new[] { loop }, out why));
        ContourPlane.MoveToWorld(pl);
        var again = new List<double[]>();
        for (int i = 0; i < pl.NumberOfVertices; i++) { var p = pl.GetPoint2dAt(i); again.Add(new[] { p.X, p.Y }); }
        Check(name + ": повторный перенос ничего не меняет", SamePoints(again, expectedPts) && pl.Elevation == 0.0);
    }

    public static int Main(string[] args)
    {
        // 1. Обычный контур: путь чтения прежний, переносить нечего.
        var world = Make(Vector3d.ZAxis, 0.0);
        Check("на месте: классификация", ContourPlane.Classify(world) == ContourPlane.Kind.World);
        var wp = new List<object>(); var wb = new List<object>();
        ContourPlane.Read(world, ContourPlane.Kind.World, wp, wb);
        List<double[]> plain; List<double> plainBulges; ExpectedWorld(false, out plain, out plainBulges);
        Check("на месте: чтение как прежде (GetPoint2dAt)", SamePoints(Points(wp), plain) && SameNumbers(Bulges(wb), plainBulges));
        ContourPlane.MoveToWorld(world);
        var wAfter = new List<double[]>();
        for (int i = 0; i < world.NumberOfVertices; i++) { var p = world.GetPoint2dAt(i); wAfter.Add(new[] { p.X, p.Y }); }
        Check("на месте: перенос не трогает контур", SamePoints(wAfter, plain) && world.Elevation == 0.0);
        GeometryLoop l0; string r0;
        Check("на месте: проверка зоны принимает", ZoneGeometryGuard.TryReadPolyline(world, "outer", out l0, out r0), r0 ?? "");
        DumpCase("на месте", Points(wp), Bulges(wb));

        // 2–5. Параллельные плану: поднятые и развёрнутые.
        Parallel("уровень 3000", Vector3d.ZAxis, 3000.0, false, "Z = 3000 мм");
        Parallel("уровень -12,5", Vector3d.ZAxis, -12.5, false, "Z = -12,5 мм");
        Parallel("нормаль вниз", new Vector3d(0, 0, -1), 0.0, true, "развёрнут, нормаль вниз");
        Parallel("нормаль вниз и уровень 250", new Vector3d(0, 0, -1), 250.0, true, "развёрнут, нормаль вниз, Z = -250 мм");
        Parallel("уровень 0,002", Vector3d.ZAxis, 0.002, false, "Z = 0,002 мм");

        // 6. Допуски совпадают с проверкой зоны.
        var tiny = Make(Vector3d.ZAxis, 0.0005);
        GeometryLoop l1; string r1;
        Check("уровень 0,0005: на месте и для проверки зоны", ContourPlane.Classify(tiny) == ContourPlane.Kind.World &&
            ZoneGeometryGuard.TryReadPolyline(tiny, "outer", out l1, out r1));
        var tilt = Make(new Vector3d(1e-12, 0, 1), 0.0);
        Check("нормаль с шумом 1e-12: на месте", ContourPlane.Classify(tilt) == ContourPlane.Kind.World);

        // 7. Вертикальный и наклонный контуры не принимаются и не переносятся.
        foreach (var n in new[] { new Vector3d(0, -1, 0), new Vector3d(0, 0.6, 0.8) })
        {
            var bad = Make(n, 0.0);
            Check("нормаль (" + n.X + "; " + n.Y + "; " + n.Z + "): не принимается", ContourPlane.Classify(bad) == ContourPlane.Kind.Unsupported);
            Check("нормаль (" + n.X + "; " + n.Y + "; " + n.Z + "): причина понятна",
                ContourPlane.Describe(bad).StartsWith("лежит не в плане", StringComparison.Ordinal), ContourPlane.Describe(bad));
            bool threw = false;
            try { ContourPlane.Read(bad, ContourPlane.Kind.Unsupported, new List<object>(), new List<object>()); }
            catch (InvalidOperationException) { threw = true; }
            Check("нормаль (" + n.X + "; " + n.Y + "; " + n.Z + "): чтение запрещено", threw);
            var before = bad.GetPoint2dAt(1);
            ContourPlane.MoveToWorld(bad);
            Check("нормаль (" + n.X + "; " + n.Y + "; " + n.Z + "): перенос ничего не меняет",
                bad.Normal.X == n.X && bad.Normal.Y == n.Y && bad.Normal.Z == n.Z && bad.GetPoint2dAt(1).X == before.X);
            GeometryLoop l2; string r2;
            Check("нормаль (" + n.X + "; " + n.Y + "; " + n.Z + "): проверка зоны называет причину",
                !ZoneGeometryGuard.TryReadPolyline(bad, "outer", out l2, out r2) && r2.Contains("вертикальный или наклонный"), r2 ?? "");
        }
        var broken = Make(Vector3d.ZAxis, double.NaN);
        Check("уровень NaN: не принимается", ContourPlane.Classify(broken) == ContourPlane.Kind.Unsupported &&
            ContourPlane.Describe(broken) == "повреждённые координаты");

        // 8. Подробная причина проверки зоны (для команд, читающих готовые зоны).
        var raised = Make(Vector3d.ZAxis, 3000.0);
        GeometryLoop l3; string r3;
        ZoneGeometryGuard.TryReadPolyline(raised, "outer", out l3, out r3);
        Check("причина проверки зоны: контур и отметка", r3 == "исходный контур " + raised.Handle + " не в плоскости XY с Z = 0 (Z = 3000 мм)", r3 ?? "");
        var flipped = Make(new Vector3d(0, 0, -1), 0.0);
        ZoneGeometryGuard.TryReadPolyline(flipped, "outer", out l3, out r3);
        Check("причина проверки зоны: развёрнутый контур", r3 == "исходный контур " + flipped.Handle + " не в плоскости XY с Z = 0 (развёрнут, нормаль вниз)", r3 ?? "");

        Dump.Append('}');
        if (args.Length > 0) File.WriteAllText(args[0], Dump.ToString(), new UTF8Encoding(false));
        Console.WriteLine("Contour plane checks: " + passed + "/" + (passed + failed) + (failed == 0 ? " passed" : " — FAILED") +
            "; AutoCAD runtime: NOT RUN");
        return failed == 0 ? 0 : 1;
    }
}
