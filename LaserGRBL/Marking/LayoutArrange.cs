// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LaserGRBL.Marking
{
    internal enum ArrangeMode { Left, CenterX, Right, Top, CenterY, Bottom, SpaceX, SpaceY }

    internal static class LayoutArrange
    {
        public static void Apply(LayoutDocument document, IEnumerable<int> selection, ArrangeMode mode, string serial)
        {
            var selected = selection.Distinct().Where(i=>i>=0&&i<document.Items.Count).ToList();
            var movable = selected.Where(i=>!document.Items[i].Background).ToList();
            bool spacing = mode==ArrangeMode.SpaceX||mode==ArrangeMode.SpaceY;
            if (spacing ? movable.Count<3 : selected.Count<2||movable.Count==0)
                throw new ArgumentException(spacing?"等間隔に並べるには固定していないオブジェクトを3個以上選択してください。":"整列するオブジェクトを2個以上選択してください。");
            var bounds = selected.ToDictionary(i=>i,i=>document.Items[i].Bounds(serial));
            var positions = movable.ToDictionary(i=>i,i=>new double[]{document.Items[i].X,document.Items[i].Y});
            if (spacing)
            {
                bool horizontal=mode==ArrangeMode.SpaceX;
                var sorted=movable.OrderBy(i=>horizontal?bounds[i].Left:bounds[i].Top).ThenBy(i=>i).ToList();
                double start=horizontal?bounds[sorted[0]].Left:bounds[sorted[0]].Top;
                double end=horizontal?bounds[sorted.Last()].Right:bounds[sorted.Last()].Bottom;
                double sizes=sorted.Sum(i=>horizontal?bounds[i].Width:bounds[i].Height);
                double gap=(end-start-sizes)/(sorted.Count-1), cursor=start;
                for (int n=0;n<sorted.Count;n++)
                {
                    int i=sorted[n];
                    // The two end objects anchor the available span.
                    if(n>0&&n<sorted.Count-1) positions[i][horizontal?0:1]+=cursor-(horizontal?bounds[i].Left:bounds[i].Top);
                    cursor+=(horizontal?bounds[i].Width:bounds[i].Height)+gap;
                }
            }
            else
            {
                var fixedItems=selected.Where(i=>document.Items[i].Background).ToList();
                var reference=fixedItems.Count>0?fixedItems:movable;
                RectangleF region=bounds[reference[0]];
                foreach(int i in reference.Skip(1))region=RectangleF.Union(region,bounds[i]);
                foreach(int i in movable)
                {
                    RectangleF b=bounds[i];double dx=0,dy=0;
                    switch(mode)
                    {
                        case ArrangeMode.Left: dx=region.Left-b.Left;break;
                        case ArrangeMode.CenterX: dx=region.Left+region.Width/2-b.Left-b.Width/2;break;
                        case ArrangeMode.Right: dx=region.Right-b.Right;break;
                        case ArrangeMode.Top: dy=region.Bottom-b.Bottom;break;
                        case ArrangeMode.CenterY: dy=region.Top+region.Height/2-b.Top-b.Height/2;break;
                        case ArrangeMode.Bottom: dy=region.Top-b.Top;break;
                    }
                    positions[i][0]+=dx;positions[i][1]+=dy;
                }
            }
            // Validate every target before changing any object, so failed alignment is atomic.
            foreach(var position in positions.Values){LayoutItem.Range(position[0],-10000,10000);LayoutItem.Range(position[1],-10000,10000);}
            foreach(var pair in positions){document.Items[pair.Key].X=pair.Value[0];document.Items[pair.Key].Y=pair.Value[1];}
        }
        public static PointF ClampMove(IEnumerable<PointF> origins, double x, double y)
        {
            var points=origins.ToList();if(points.Count==0)return PointF.Empty;
            return new PointF((float)Math.Max(-10000-points.Min(p=>p.X),Math.Min(10000-points.Max(p=>p.X),x)),
                (float)Math.Max(-10000-points.Min(p=>p.Y),Math.Min(10000-points.Max(p=>p.Y),y)));
        }
    }
}
