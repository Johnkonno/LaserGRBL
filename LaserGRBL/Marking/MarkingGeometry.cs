// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text;
using ClipperLib;
using QRCoder;

namespace LaserGRBL.Marking
{
    public enum MarkingMode { Trace, Offset, Zigzag }

    public sealed class MarkingPath
    {
        public readonly List<PointF> Points;
        public readonly bool Closed;
        public MarkingPath(IEnumerable<PointF> points, bool closed)
        {
            Points = points.ToList();
            Closed = closed;
        }
    }

    public static class MarkingGeometry
    {
        private const double Scale = 100000.0;
        private const int MaxPoints = 500000;
        private static string N(double value) { return value.ToString("0.#####", CultureInfo.InvariantCulture); }
        private static IntPoint IP(PointF p) { return new IntPoint(Math.Round(p.X * Scale), Math.Round(p.Y * Scale)); }
        private static PointF FP(IntPoint p) { return new PointF((float)(p.X / Scale), (float)(p.Y / Scale)); }

        public static List<MarkingPath> FromGraphicsPath(GraphicsPath graphicsPath)
        {
            using (GraphicsPath flat = (GraphicsPath)graphicsPath.Clone())
            {
                flat.Flatten(null, 0.01f);
                List<MarkingPath> result = new List<MarkingPath>();
                List<PointF> current = null;
                PointF[] points = flat.PathPoints;
                byte[] types = flat.PathTypes;
                for (int i = 0; i < points.Length; i++)
                {
                    if ((types[i] & 7) == 0)
                    {
                        if (current != null && current.Count > 1) result.Add(new MarkingPath(current, false));
                        current = new List<PointF>();
                    }
                    current.Add(points[i]);
                    if ((types[i] & 128) != 0)
                    {
                        if (current.Count > 1) result.Add(new MarkingPath(current, true));
                        current = null;
                    }
                }
                if (current != null && current.Count > 1) result.Add(new MarkingPath(current, false));
                return result;
            }
        }

        public static List<MarkingPath> Text(string text, string font, double height)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("文字を入力してください。");
            Positive(height, "文字高さ");
            using (GraphicsPath path = new GraphicsPath())
            using (FontFamily family = new FontFamily(font))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                path.AddString(text, family, (int)FontStyle.Regular, 100, PointF.Empty, format);
                RectangleF bounds = path.GetBounds();
                if (bounds.Height <= 0) throw new ArgumentException("描画可能な文字がありません。");
                float factor = (float)(height / bounds.Height);
                using (Matrix matrix = new Matrix(factor, 0, 0, -factor, -bounds.Left * factor, bounds.Bottom * factor))
                    path.Transform(matrix);
                return FromGraphicsPath(path);
            }
        }

        // QRCoder includes a four-module quiet zone in ModuleMatrix. Preserve it in the layout.
        public static List<MarkingPath> Qr(string content, double size)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("QRの内容を入力してください。");
            Positive(size, "QRサイズ");
            using (QRCodeGenerator generator = new QRCodeGenerator())
            using (QRCodeData data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q, true))
            {
                List<MarkingPath> result = new List<MarkingPath>();
                double cell = size / data.ModuleMatrix.Count;
                for (int y = 0; y < data.ModuleMatrix.Count; y++)
                    for (int x = 0; x < data.ModuleMatrix.Count; x++)
                        if (data.ModuleMatrix[y][x])
                        {
                            float left = (float)(x * cell), right = (float)((x + 1) * cell);
                            float bottom = (float)(size - (y + 1) * cell), top = (float)(size - y * cell);
                            result.Add(new MarkingPath(new[] { new PointF(left, bottom), new PointF(right, bottom),
                                new PointF(right, top), new PointF(left, top) }, true));
                        }
                return result;
            }
        }

        public static List<MarkingPath> QrFill(string content, double size, double spacing)
        {
            List<MarkingPath> modules = Qr(content, size);
            double moduleWidth = modules[0].Points[1].X - modules[0].Points[0].X;
            if (spacing > moduleWidth / 2) throw new ArgumentException("QRの塗り間隔は1モジュール幅の半分以下にしてください（上限 " + N(moduleWidth / 2) + " mm）。");
            return Fill(modules, spacing);
        }

        public static List<MarkingPath> Shift(IEnumerable<MarkingPath> paths, double x, double y)
        {
            return paths.Select(p => new MarkingPath(p.Points.Select(q => new PointF((float)(q.X + x), (float)(q.Y + y))), p.Closed)).ToList();
        }

        public static List<MarkingPath> Widen(List<MarkingPath> paths, MarkingMode mode, double width, double spacing, double pitch)
        {
            ValidatePaths(paths);
            if (mode == MarkingMode.Trace) return paths;
            Positive(width, "線幅");
            Positive(spacing, "軌道間隔");
            if (spacing > width) throw new ArgumentException("軌道間隔は線幅以下にしてください。");
            ClipperOffset offset = new ClipperOffset(2, 0.005 * Scale);
            foreach (MarkingPath path in paths)
                offset.AddPath(Clean(path.Points), JoinType.jtRound, path.Closed ? EndType.etClosedLine : EndType.etOpenRound);
            List<List<IntPoint>> band = new List<List<IntPoint>>();
            offset.Execute(ref band, width * Scale / 2);
            if (mode == MarkingMode.Offset)
            {
                List<MarkingPath> result = new List<MarkingPath>();
                ClipperOffset inset = new ClipperOffset(2, 0.005 * Scale);
                inset.AddPaths(band, JoinType.jtRound, EndType.etClosedPolygon);
                // Include the band boundary, then shrink it until both sides of the stroke meet.
                int count = (int)Math.Ceiling(width / spacing) + 2;
                if (count > 10000) throw new ArgumentException("軌道間隔が細かすぎます。");
                for (int i = 0; i < count; i++)
                {
                    List<List<IntPoint>> rings = new List<List<IntPoint>>();
                    inset.Execute(ref rings, -i * spacing * Scale);
                    if (rings.Count == 0) break;
                    result.AddRange(rings.Where(p => p.Count > 2).Select(p => new MarkingPath(p.Select(FP), true)));
                    ValidatePaths(result);
                }
                // The original centerline covers narrow residual regions between the last two rings.
                result.AddRange(paths);
                ValidatePaths(result);
                return result;
            }

            Positive(pitch, "ジグザグピッチ");
            List<MarkingPath> zigzags = new List<MarkingPath>();
            foreach (MarkingPath path in paths)
            {
                List<PointF> center = path.Points.ToList();
                if (path.Closed) center.Add(center[0]);
                List<double> distance = new List<double> { 0 };
                for (int i = 1; i < center.Count; i++) distance.Add(distance[i - 1] + Length(center[i - 1], center[i]));
                double total = distance[distance.Count - 1];
                int samples = (int)Math.Ceiling(total / (pitch / 2));
                if (samples > MaxPoints) throw new ArgumentException("ジグザグピッチが細かすぎます。");
                List<IntPoint> wave = new List<IntPoint> { IP(center[0]) };
                int segment = 1;
                for (int i = 1; i < samples; i++)
                {
                    double along = total * i / samples;
                    while (segment < center.Count - 1 && distance[segment] < along) segment++;
                    double length = distance[segment] - distance[segment - 1];
                    if (length <= 0) continue;
                    PointF a = center[segment - 1], b = center[segment];
                    double t = (along - distance[segment - 1]) / length;
                    double side = (i % 2 == 0 ? -1 : 1) * width / 2;
                    wave.Add(IP(new PointF((float)(a.X + (b.X - a.X) * t - (b.Y - a.Y) / length * side),
                        (float)(a.Y + (b.Y - a.Y) * t + (b.X - a.X) / length * side))));
                }
                wave.Add(IP(center[center.Count - 1]));
                // Clip sharp-corner shortcuts to the stroke band; travel between fragments with the laser off.
                Clipper clip = new Clipper();
                clip.AddPath(wave, PolyType.ptSubject, false);
                clip.AddPaths(band, PolyType.ptClip, true);
                PolyTree tree = new PolyTree();
                if (!clip.Execute(ClipType.ctIntersection, tree, PolyFillType.pftNonZero)) throw new InvalidOperationException("軌道の切り取りに失敗しました。");
                zigzags.AddRange(Clipper.OpenPathsFromPolyTree(tree).Where(p => p.Count > 1).Select(p => new MarkingPath(p.Select(FP), false)));
            }
            ValidatePaths(zigzags);
            return zigzags;
        }

        public static List<MarkingPath> Fill(List<MarkingPath> outlines, double spacing)
        {
            Positive(spacing, "塗りつぶし間隔");
            ValidatePaths(outlines);
            if (outlines.Any(p => !p.Closed)) throw new ArgumentException("塗りつぶしには閉じたパスが必要です。");
            List<List<IntPoint>> polygons = outlines.Select(p => Clean(p.Points)).ToList();
            IntRect bounds = Clipper.GetBounds(polygons);
            int rows = (int)Math.Ceiling((bounds.bottom - bounds.top) / Scale / spacing);
            if (rows > 100000) throw new ArgumentException("塗りつぶし間隔が細かすぎます。");
            List<MarkingPath> result = new List<MarkingPath>();
            int pointCount = 0;
            for (int row = 0; row < rows; row++)
            {
                long y = bounds.top + (long)Math.Round((row + 0.5) * spacing * Scale);
                if (y >= bounds.bottom) break;
                Clipper clip = new Clipper();
                clip.AddPath(new List<IntPoint> { new IntPoint(bounds.left - 1, y), new IntPoint(bounds.right + 1, y) }, PolyType.ptSubject, false);
                clip.AddPaths(polygons, PolyType.ptClip, true);
                PolyTree solution = new PolyTree();
                if (!clip.Execute(ClipType.ctIntersection, solution, PolyFillType.pftEvenOdd)) throw new InvalidOperationException("塗りつぶしに失敗しました。");
                var segments = Clipper.OpenPathsFromPolyTree(solution).Where(p => p.Count > 1).OrderBy(p => p.Min(q => q.X)).ToList();
                if (row % 2 == 1) segments.Reverse();
                foreach (var segment in segments)
                {
                    if ((row % 2 == 0 && segment[0].X > segment[segment.Count - 1].X) ||
                        (row % 2 == 1 && segment[0].X < segment[segment.Count - 1].X)) segment.Reverse();
                    result.Add(new MarkingPath(segment.Select(FP), false));
                    pointCount += segment.Count;
                    if (pointCount > MaxPoints) throw new ArgumentException("軌道が多すぎます。間隔を広げてください。");
                }
            }
            ValidatePaths(result);
            return result;
        }

        public static string GCode(List<MarkingPath> paths, double speed, double power, string laserMode,
            double offsetX, double offsetY, double retreatX, double retreatY)
        {
            ValidatePaths(paths);
            Positive(speed, "加工速度");
            Positive(power, "出力");
            if (laserMode != "M3" && laserMode != "M4") throw new ArgumentException("レーザーモードが不正です。");
            foreach (double v in new[] { offsetX, offsetY, retreatX, retreatY })
                if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("座標が不正です。");
            StringBuilder code = new StringBuilder("(LaserGRBL Marking)\r\nM5 S0\r\nG21\r\nG90\r\nG94\r\n");
            foreach (MarkingPath path in paths)
            {
                if (path.Points.Count < 2) continue;
                PointF first = path.Points[0];
                code.Append("M5 S0\r\nG0 X").Append(N(first.X + offsetX)).Append(" Y").Append(N(first.Y + offsetY)).Append("\r\n");
                code.Append(laserMode).Append(" S0\r\n");
                for (int i = 1; i < path.Points.Count + (path.Closed ? 1 : 0); i++)
                {
                    PointF p = path.Points[i % path.Points.Count];
                    code.Append("G1 X").Append(N(p.X + offsetX)).Append(" Y").Append(N(p.Y + offsetY))
                        .Append(" F").Append(N(speed)).Append(" S").Append(N(power)).Append("\r\n");
                }
                code.Append("M5 S0\r\n");
            }
            // Synchronize the planner before and after the laser-off retreat.
            code.Append("G4 P0\r\nM5 S0\r\n");
            if (retreatX != 0 || retreatY != 0)
                code.Append("G91\r\nG0 X").Append(N(retreatX)).Append(" Y").Append(N(retreatY)).Append("\r\nG90\r\nG4 P0\r\n");
            return code.ToString();
        }

        private static List<IntPoint> Clean(List<PointF> points)
        {
            List<IntPoint> result = new List<IntPoint>();
            foreach (PointF p in points) if (result.Count == 0 || IP(p) != result[result.Count - 1]) result.Add(IP(p));
            if (result.Count > 1 && result[0] == result[result.Count - 1]) result.RemoveAt(result.Count - 1);
            return result;
        }
        private static double Length(PointF a, PointF b) { return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)); }
        private static void Positive(double value, string name)
        {
            if (value < 0.001 || double.IsInfinity(value) || double.IsNaN(value) || value > 100000)
                throw new ArgumentException(name + "は0.001〜100000の範囲にしてください。");
        }
        private static void ValidatePaths(List<MarkingPath> paths)
        {
            if (paths.Count == 0) throw new ArgumentException("加工可能なパスがありません。");
            int total = 0;
            foreach (MarkingPath path in paths)
            {
                total += path.Points.Count;
                if (total > MaxPoints) throw new ArgumentException("軌道が多すぎます。間隔を広げてください。");
                foreach (PointF p in path.Points)
                    if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y) ||
                        Math.Abs(p.X) > 100000 || Math.Abs(p.Y) > 100000) throw new ArgumentException("パス座標が範囲外です。");
            }
        }
    }
}
