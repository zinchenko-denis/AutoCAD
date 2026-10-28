using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

namespace AFacadesPlugin
{
    /// <summary>
    /// 28.10 (снимок Германа: «Зоны не созданы: … поддерживается только плоскость
    /// XY с Z = 0»). Вся фасадная цепочка — зоны, штриховка, ATTILE, ATFRAME —
    /// работает в плане мировой системы: плоскость XY, Z = 0, нормаль вверх.
    /// Полилиния, параллельная этой плоскости, но поднятая (свойство «Уровень»
    /// не 0) или развёрнутая (нормаль вниз — например, после расчленения
    /// зеркального блока или черчения в перевёрнутой ПСК), в плане выглядит так
    /// же. Однако у развёрнутой GetPoint2dAt возвращает зеркальные X, а проверки
    /// зоны требуют Z = 0, поэтому ATFZONE отказывал.
    ///
    /// Здесь — чтение вершин в мировых координатах (вид сверху) и перенос такой
    /// полилинии на Z = 0 с нормалью вверх без изменения её вида в плане.
    /// Перенос выполняет ATFZONE только после согласия пользователя, в той же
    /// транзакции, что и создание зоны (одна отмена U). Вертикальные и наклонные
    /// контуры не принимаются: их проекция на план была бы другой фигурой.
    /// </summary>
    internal static class ContourPlane
    {
        internal enum Kind { World, Parallel, Unsupported }

        // Те же допуски, что в ZoneGeometryGuard.WorldPlane: контур, признанный
        // здесь лежащим на месте, принимает и проверка зоны.
        private const double NormalTolerance = 1e-9;

        internal static Kind Classify(Polyline pl)
        {
            if (pl == null) return Kind.Unsupported;
            Vector3d n = pl.Normal;
            double e = pl.Elevation;
            if (!Finite(n.X) || !Finite(n.Y) || !Finite(n.Z) || !Finite(e)) return Kind.Unsupported;
            if (Math.Abs(n.X) > NormalTolerance || Math.Abs(n.Y) > NormalTolerance ||
                Math.Abs(Math.Abs(n.Z) - 1) > NormalTolerance) return Kind.Unsupported;
            return n.Z > 0 && Math.Abs(e) <= GeometryFingerprint.CoordinateTolerance ? Kind.World : Kind.Parallel;
        }

        /// <summary>Вершины в мировых XY и выпуклости так, как их видно сверху (+Z).
        /// Для контура на месте чтение прежнее (GetPoint2dAt) — обычный путь не меняется.</summary>
        internal static void Read(Polyline pl, Kind kind, List<object> pts, List<object> bulges)
        {
            if (kind == Kind.Unsupported) throw new InvalidOperationException("контур не параллелен плоскости XY");
            bool down = kind == Kind.Parallel && pl.Normal.Z < 0;
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                double x, y;
                if (kind == Kind.World) { Point2d p = pl.GetPoint2dAt(i); x = p.X; y = p.Y; }
                else { Point3d p = pl.GetPoint3dAt(i); x = p.X; y = p.Y; }   // мировые координаты
                double b = pl.GetBulgeAt(i);
                pts.Add(new[] { x, y });
                // У развёрнутой полилинии дуга, идущая против часовой стрелки в её
                // собственной системе, сверху видна по часовой: знак выпуклости обратный.
                bulges.Add(down ? -b : b);
            }
        }

        /// <summary>Перенести параллельный плоскости XY контур на Z = 0 с нормалью вверх.
        /// Вид в плане, порядок вершин, ширины сегментов и замкнутость сохраняются.
        /// Полилиния должна быть открыта на запись.</summary>
        internal static void MoveToWorld(Polyline pl)
        {
            if (Classify(pl) != Kind.Parallel) return;
            int n = pl.NumberOfVertices;
            bool down = pl.Normal.Z < 0;
            var pts = new Point2d[n];
            var bulges = new double[n];
            for (int i = 0; i < n; i++)
            {
                Point3d p = pl.GetPoint3dAt(i);
                pts[i] = new Point2d(p.X, p.Y);
                double b = pl.GetBulgeAt(i);
                bulges[i] = down ? -b : b;
            }
            pl.Normal = Vector3d.ZAxis;
            pl.Elevation = 0.0;
            for (int i = 0; i < n; i++)
            {
                pl.SetPointAt(i, pts[i]);
                pl.SetBulgeAt(i, bulges[i]);
            }
        }

        /// <summary>Коротко, что не так с плоскостью контура, — для сообщения пользователю.</summary>
        internal static string Describe(Polyline pl)
        {
            if (pl == null) return "не полилиния";
            Vector3d n = pl.Normal;
            double e = pl.Elevation;
            if (!Finite(n.X) || !Finite(n.Y) || !Finite(n.Z) || !Finite(e)) return "повреждённые координаты";
            if (Classify(pl) == Kind.Unsupported)
                return "лежит не в плане: нормаль (" + Num(n.X) + "; " + Num(n.Y) + "; " + Num(n.Z) +
                    ") — вертикальная или наклонная плоскость, например контур начерчен в повёрнутой ПСК";
            var parts = new List<string>();
            if (n.Z < 0) parts.Add("развёрнут, нормаль вниз");
            // Мировая отметка: у развёрнутой полилинии ось Z её системы направлена вниз.
            double z = n.Z < 0 ? -e : e;
            if (Math.Abs(z) > GeometryFingerprint.CoordinateTolerance) parts.Add("Z = " + Num(z) + " мм");
            return parts.Count == 0 ? "на месте" : string.Join(", ", parts.ToArray());
        }

        private static string Num(double v)
        {
            if (Math.Abs(v) < 5e-4) v = 0.0;   // без «-0»
            return v.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
        }
        private static bool Finite(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
    }
}
