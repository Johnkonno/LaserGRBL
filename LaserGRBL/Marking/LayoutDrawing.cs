// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace LaserGRBL.Marking
{
    internal enum DrawingTool { Select, Line, Rectangle, Circle, Ellipse, Polyline, Polygon, RegularPolygon, Arc, Freehand }

    internal static class LayoutDrawing
    {
        public static List<MarkingPath> Shape(LayoutKind kind, double width, double height, int sides)
        {
            if (kind == LayoutKind.Rectangle)
                return new List<MarkingPath> { new MarkingPath(new[] { PointF.Empty, new PointF((float)width,0), new PointF((float)width,(float)height), new PointF(0,(float)height) }, true) };
            if (kind == LayoutKind.Ellipse)
                using (GraphicsPath path = new GraphicsPath())
                { path.AddEllipse(0,0,(float)width,(float)height); return MarkingGeometry.FromGraphicsPath(path); }
            if (kind == LayoutKind.RegularPolygon)
            {
                var points = Enumerable.Range(0,sides).Select(i => { double a = Math.PI/2 + 2*Math.PI*i/sides;
                    return new PointF((float)(width/2+width/2*Math.Cos(a)),(float)(width/2+width/2*Math.Sin(a))); });
                return new List<MarkingPath> { new MarkingPath(points,true) };
            }
            throw new ArgumentException("未対応の図形です。");
        }
        public static List<PointF> Arc(PointF start, PointF middle, PointF end)
        {
            double ax=middle.X-start.X, ay=middle.Y-start.Y, bx=end.X-start.X, by=end.Y-start.Y;
            double determinant=2*(ax*by-ay*bx), a2=ax*ax+ay*ay, b2=bx*bx+by*by;
            if (Math.Abs(determinant) < 0.000001*Math.Max(1,Math.Sqrt(a2*b2))) throw new ArgumentException("円弧の3点は同一直線上を避けて指定してください。");
            double cx=start.X+(by*a2-ay*b2)/determinant, cy=start.Y+(ax*b2-bx*a2)/determinant;
            double radius=Math.Sqrt(Math.Pow(start.X-cx,2)+Math.Pow(start.Y-cy,2));
            double first=Math.Atan2(start.Y-cy,start.X-cx), mid=PositiveAngle(Math.Atan2(middle.Y-cy,middle.X-cx)-first), sweep=PositiveAngle(Math.Atan2(end.Y-cy,end.X-cx)-first);
            if (mid>sweep) sweep-=2*Math.PI;
            double step=2*Math.Acos(Math.Max(-1,Math.Min(1,1-0.01/radius)));
            int segments=(int)Math.Ceiling(Math.Abs(sweep)/Math.Max(0.00001,step)); segments=Math.Max(2,segments);
            if (segments>20000 || radius>100000) throw new ArgumentException("円弧が大きすぎます。");
            List<PointF> points=Enumerable.Range(0,segments+1).Select(i=>new PointF((float)(cx+radius*Math.Cos(first+sweep*i/segments)),(float)(cy+radius*Math.Sin(first+sweep*i/segments)))).ToList();
            points[0]=start; points[points.Count-1]=end; return points;
        }
        private static double PositiveAngle(double angle) { angle%=2*Math.PI; return angle<0?angle+2*Math.PI:angle; }
        public static LayoutItem Path(string name, IEnumerable<PointF> vertices, bool closed)
        {
            var points=vertices.ToList();
            if (points.Count<(closed?3:2)) throw new ArgumentException("図形の点が足りません。");
            float x=points.Min(p=>p.X),y=points.Min(p=>p.Y);
            return new LayoutItem {Kind=LayoutKind.Path,Name=name,X=x,Y=y,Mode=LayoutMode.Trace,Geometry=new List<LayoutPath> {
                new LayoutPath {Closed=closed,Points=points.Select(p=>new LayoutPoint {X=p.X-x,Y=p.Y-y}).ToList()} }};
        }
    }

    // Keep shape dimensions and text fields relevant to the selected object.
    internal sealed class LayoutPropertyView : CustomTypeDescriptor
    {
        private readonly LayoutItem item;
        public LayoutPropertyView(LayoutItem item) : base(TypeDescriptor.GetProvider(item).GetTypeDescriptor(item)) { this.item=item; }
        public override object GetPropertyOwner(PropertyDescriptor pd) { return item; }
        public override PropertyDescriptorCollection GetProperties() { return GetProperties(null); }
        public override PropertyDescriptorCollection GetProperties(Attribute[] attributes)
        {
            bool shape=item.Kind==LayoutKind.Rectangle||item.Kind==LayoutKind.Ellipse||item.Kind==LayoutKind.RegularPolygon;
            return new PropertyDescriptorCollection(base.GetProperties(attributes).Cast<PropertyDescriptor>().Where(p =>
                (p.Name!="ShapeWidth"||shape) && (p.Name!="ShapeHeight"||item.Kind==LayoutKind.Rectangle||item.Kind==LayoutKind.Ellipse) && (p.Name!="Sides"||item.Kind==LayoutKind.RegularPolygon) &&
                (p.Name!="FontName"||item.Kind==LayoutKind.Text) && ((p.Name!="Content"&&p.Name!="Size")||item.Kind==LayoutKind.Text||item.Kind==LayoutKind.QR)).ToArray(),true);
        }
    }
}
