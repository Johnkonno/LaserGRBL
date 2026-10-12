// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using CsPotrace;

namespace LaserGRBL.Marking
{
    internal static class LayoutBitmapTrace
    {
        // The bitmap has already gone through ImageProcessor's native crop/resize/threshold pipeline.
        internal static LayoutItem Convert(Bitmap bitmap, string name, SizeF size, bool removeSpots, int spots,
            bool smooth, decimal smoothing, bool optimize, decimal tolerance, bool optimizeTravel)
        {
            LayoutItem.Range(size.Width, 0.01, 10000); LayoutItem.Range(size.Height, 0.01, 10000);
            List<List<Curve>> curves;
            using (Bitmap flipped = (Bitmap)bitmap.Clone())
            {
                // Match GrblFile.LoadImagePotrace's image-to-machine Y orientation.
                flipped.RotateFlip(RotateFlipType.RotateNoneFlipY);
                Potrace.turdsize = removeSpots ? spots : 2;
                Potrace.alphamax = smooth ? (double)smoothing : 0;
                Potrace.opttolerance = optimize ? (double)tolerance : 0.2;
                Potrace.curveoptimizing = optimize;
                curves = Potrace.PotraceTrace(flipped);
            }
            if (curves == null || curves.Count == 0) throw new ArgumentException("トレースできる輪郭がありません。しきい値などの画像設定を調整してください。");
            if (curves.Sum(p => (long)p.Count) > 200000) throw new ArgumentException("画像の輪郭が多すぎます。ゴミ除去や縮小を調整してください。");
            if (optimizeTravel)
            {
                if (curves.Count > 2000) throw new ArgumentException("移動最適化の対象が多すぎます。移動最適化を解除するか、ゴミ除去を調整してください。");
                curves = GrblFile.OptimizePaths(curves, 0);
            }
            else curves.Reverse();
            List<MarkingPath> outlines;
            using (GraphicsPath path = new GraphicsPath(FillMode.Alternate))
            {
                foreach (List<Curve> contour in curves)
                {
                    path.StartFigure();
                    foreach (Curve curve in contour)
                    {
                        PointF a = Point(curve.A), b = Point(curve.B);
                        if (curve.Kind == CurveKind.Line) path.AddLine(a, b);
                        else path.AddBezier(a, Point(curve.ControlPointA), Point(curve.ControlPointB), b);
                    }
                    path.CloseFigure();
                }
                using (Matrix physical = new Matrix())
                { physical.Scale(size.Width / bitmap.Width, size.Height / bitmap.Height); path.Transform(physical); }
                outlines = MarkingGeometry.FromGraphicsPath(path);
            }
            LayoutItem item = new LayoutItem { Kind = LayoutKind.BitmapTrace, Name = name, ShapeWidth = size.Width, ShapeHeight = size.Height,
                Mode = LayoutMode.Trace, Geometry = outlines.Select(p => new LayoutPath { Closed = p.Closed,
                    Points = p.Points.Select(q => new LayoutPoint { X = q.X, Y = q.Y }).ToList() }).ToList() };
            item.Validate(); return item;
        }
        private static PointF Point(dPoint p) { return new PointF((float)p.X, (float)p.Y); }
    }
}
