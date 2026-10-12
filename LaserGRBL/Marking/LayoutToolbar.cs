// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace LaserGRBL.Marking
{
    internal static class LayoutToolbar
    {
        private static readonly SvgIcons.SvgIcons icons = new SvgIcons.SvgIcons();
        private static readonly Dictionary<string,string> commands = new Dictionary<string,string> {
            {"新規","file-plus-outline"},{"開く","folder-open-outline"},{"保存","content-save-outline"},{"別名保存","content-save-edit-outline"},
            {"元に戻す","undo"},{"やり直し","redo"},{"画像トレース","image-plus"},{"SVG追加","svg"},{"文字","format-text"},
            {"QR","qrcode"},{"連番文字","counter"},{"連番QR","qrcode-plus"},{"全体表示","fit-to-screen-outline"},
            {"定規・寸法補正","ruler-square"},{"Gコード生成 →","code-braces"},{"複製","content-copy"},{"削除","delete-outline"},
            {"加工順を上へ","arrow-up"},{"加工順を下へ","arrow-down"}
        };
        private static readonly string[] drawingIcons = { "cursor-default-outline","vector-line","vector-rectangle","circle-outline","ellipse-outline",
            "vector-polyline","vector-polygon","hexagon-outline","vector-curve","pencil-outline" };
        public static ToolStrip Strip(DockStyle dock)
        {
            return new ToolStrip { Dock=dock,GripStyle=ToolStripGripStyle.Hidden,ImageScalingSize=new Size(24,24),
                BackColor=Color.FromArgb(246,248,251),Padding=new Padding(5),Renderer=new ToolStripProfessionalRenderer(new ToolbarColors()) };
        }
        public static ToolStripButton Command(string text) { return Button(text,commands[text],text=="削除"?Color.FromArgb(190,65,65):text=="Gコード生成 →"?Color.FromArgb(31,128,79):Color.FromArgb(50,85,120)); }
        public static ToolStripButton Drawing(DrawingTool tool,string text)
        {
            ToolStripButton button=Button(text,drawingIcons[(int)tool],Color.FromArgb(50,85,120));
            string hint=tool==DrawingTool.Select?"ドラッグで移動、右上の□で拡大縮小":
                tool==DrawingTool.Circle||tool==DrawingTool.RegularPolygon?"中心から半径までドラッグ":
                tool==DrawingTool.Polyline||tool==DrawingTool.Polygon?"頂点をクリック、Enter／ダブルクリックで確定":
                tool==DrawingTool.Arc?"始点・通過点・終点をクリック":tool==DrawingTool.Freehand?"左ボタンを押したまま描画":"始点から終点へドラッグ";
            button.ToolTipText=text+"\r\n"+hint;return button;
        }
        private static ToolStripButton Button(string text,string glyph,Color color)
        {
            string key="mdi-"+glyph;
            if(!icons.Contains(key)) throw new ArgumentException("アイコンがありません："+key);
            Bitmap image=new Bitmap(24,24);
            using(Bitmap source=icons.LoadImage(key)) using(Graphics graphics=Graphics.FromImage(image)) using(ImageAttributes tint=new ImageAttributes())
            {
                tint.SetColorMatrix(new ColorMatrix(new[] {new float[]{0,0,0,0,0},new float[]{0,0,0,0,0},new float[]{0,0,0,0,0},
                    new float[]{0,0,0,1,0},new float[]{color.R/255f,color.G/255f,color.B/255f,0,1}}));
                graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source,new Rectangle(0,0,24,24),0,0,source.Width,source.Height,GraphicsUnit.Pixel,tint);
            }
            ToolStripButton button=new ToolStripButton(text,image) {DisplayStyle=ToolStripItemDisplayStyle.Image,AutoSize=false,Size=new Size(40,40),
                ToolTipText=text,AccessibleName=text,ImageScaling=ToolStripItemImageScaling.SizeToFit,Margin=new Padding(1)};
            button.Disposed+=(s,e)=>image.Dispose();return button;
        }
        private sealed class ToolbarColors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin {get{return Color.FromArgb(246,248,251);}}
            public override Color ToolStripGradientMiddle {get{return ToolStripGradientBegin;}}
            public override Color ToolStripGradientEnd {get{return ToolStripGradientBegin;}}
            public override Color ToolStripBorder {get{return Color.FromArgb(218,224,231);}}
            public override Color ButtonSelectedHighlight {get{return Color.FromArgb(226,237,250);}}
            public override Color ButtonSelectedGradientBegin {get{return ButtonSelectedHighlight;}}
            public override Color ButtonSelectedGradientEnd {get{return ButtonSelectedHighlight;}}
            public override Color ButtonSelectedBorder {get{return Color.FromArgb(119,162,212);}}
            public override Color ButtonCheckedHighlight {get{return Color.FromArgb(201,224,250);}}
            public override Color ButtonCheckedGradientBegin {get{return ButtonCheckedHighlight;}}
            public override Color ButtonCheckedGradientMiddle {get{return ButtonCheckedHighlight;}}
            public override Color ButtonCheckedGradientEnd {get{return ButtonCheckedHighlight;}}
            public override Color ButtonCheckedHighlightBorder {get{return ButtonSelectedBorder;}}
        }
    }
}
