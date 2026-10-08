// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Xml;
using System.Xml.Linq;

namespace LaserGRBL.Marking
{
    internal enum LayoutKind { SVG, Text, QR }
    internal enum LayoutMode { Trace, Offset, Zigzag, Fill }

    [DataContract]
    internal sealed class LayoutPoint
    {
        [DataMember] public float X;
        [DataMember] public float Y;
    }
    [DataContract]
    internal sealed class LayoutPath
    {
        [DataMember] public bool Closed;
        [DataMember] public List<LayoutPoint> Points;
    }

    [DataContract]
    internal sealed class LayoutItem
    {
        [DataMember, Category("1. 内容"), DisplayName("名前")]
        public string Name { get; set; }
        [DataMember, Category("1. 内容"), DisplayName("種類"), ReadOnly(true)]
        public LayoutKind Kind { get; set; }
        [DataMember, Category("1. 内容"), DisplayName("文字／QR内容"), Description("{serial} は加工時の連番に置き換わります。")]
        public string Content { get; set; }
        [DataMember, Category("1. 内容"), DisplayName("フォント")]
        public string FontName { get; set; }
        [DataMember, Category("1. 内容"), DisplayName("文字高さ／QRサイズ mm"), Description("QRサイズには周囲の余白を含みます。SVGには適用しません。")]
        public double Size { get; set; }
        [DataMember, Category("2. 配置"), DisplayName("X mm")]
        public double X { get; set; }
        [DataMember, Category("2. 配置"), DisplayName("Y mm")]
        public double Y { get; set; }
        [DataMember, Category("2. 配置"), DisplayName("倍率"), Description("縦横比を保って拡大縮小します。")]
        public double Scale { get; set; }
        [DataMember, Category("2. 配置"), DisplayName("回転 °"), Description("配置原点を中心に反時計回り。")]
        public double Rotation { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("加工する")]
        public bool Enabled { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("加工方式"), Description("Trace: 輪郭、Offset: 重ね、Zigzag: 太線ジグザグ、Fill: 閉じた図形の塗りつぶし。")]
        public LayoutMode Mode { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("軌道幅 mm")]
        public double Width { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("重ね／塗り間隔 mm")]
        public double Spacing { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("ジグザグピッチ mm")]
        public double Pitch { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("速度 mm/min")]
        public double Speed { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("出力 S")]
        public double Power { get; set; }
        [DataMember, Category("3. 加工"), DisplayName("M3／M4")]
        public string LaserMode { get; set; }
        [DataMember, Browsable(false)] public List<LayoutPath> Geometry { get; set; }
        [Browsable(false)] public bool UsesSerial { get { return Kind != LayoutKind.SVG && Content != null && Content.Contains("{serial}"); } }

        public LayoutItem()
        {
            Name = "オブジェクト"; Content = "TEXT"; FontName = "Arial"; Size = 5;
            Scale = 1; Enabled = true; Width = 0.5; Spacing = 0.05; Pitch = 0.2;
            Speed = 1000; Power = 255; LaserMode = "M3"; Geometry = new List<LayoutPath>();
        }
        public override string ToString() { return (Enabled ? "" : "[OFF] ") + Name; }
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(LayoutKind), Kind) || !Enum.IsDefined(typeof(LayoutMode), Mode)) throw new ArgumentException("オブジェクトの種類／方式が不正です。");
            Range(X, -10000, 10000); Range(Y, -10000, 10000); Range(Rotation, -3600, 3600);
            Range(Scale, 0.001, 1000); Range(Size, 0.01, 1000); Range(Width, 0.001, 1000);
            Range(Spacing, 0.001, 100); Range(Pitch, 0.001, 100); Range(Speed, 1, 100000); Range(Power, 1, 100000);
            if (LaserMode != "M3" && LaserMode != "M4") throw new ArgumentException("M3またはM4を指定してください。");
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || Content == null || Content.Length > 10000 || string.IsNullOrWhiteSpace(FontName)) throw new ArgumentException("名前・内容・フォントを確認してください。");
            if (Kind == LayoutKind.SVG)
            {
                if (Geometry == null || Geometry.Count == 0 || Geometry.Any(p => p == null || p.Points == null || p.Points.Count < 2) || Geometry.Sum(p => (long)p.Points.Count) > 500000)
                    throw new ArgumentException("SVGのパスが不正または多すぎます。");
                foreach (LayoutPoint p in Geometry.SelectMany(g => g.Points))
                { if (p == null) throw new ArgumentException("SVGの座標がありません。"); Range(p.X, -100000, 100000); Range(p.Y, -100000, 100000); }
            }
            else if (string.IsNullOrWhiteSpace(Content)) throw new ArgumentException("文字／QRの内容を入力してください。");
        }
        internal static void Range(double value, double min, double max)
        { if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max) throw new ArgumentException("数値が範囲外です（" + min + "〜" + max + "）。"); }

        public List<MarkingPath> Outlines(string serial)
        {
            Validate();
            string text = Content.Replace("{serial}", serial);
            List<MarkingPath> paths = Kind == LayoutKind.SVG ? Geometry.Select(p => new MarkingPath(p.Points.Select(q => new PointF(q.X, q.Y)), p.Closed)).ToList() :
                Kind == LayoutKind.QR ? MarkingGeometry.Qr(text, Size) : MarkingGeometry.Text(text, FontName, Size);
            return Transform(paths, true);
        }
        private List<MarkingPath> Transform(List<MarkingPath> paths, bool scale)
        {
            double angle = Rotation * Math.PI / 180, c = Math.Cos(angle), s = Math.Sin(angle), factor = scale ? Scale : 1;
            return paths.Select(p => new MarkingPath(p.Points.Select(q => new PointF(
                (float)(X + factor * (q.X * c - q.Y * s)), (float)(Y + factor * (q.X * s + q.Y * c)))), p.Closed)).ToList();
        }
        public RectangleF Bounds(string serial)
        {
            List<PointF> points = (Kind == LayoutKind.QR ? Transform(new List<MarkingPath> { new MarkingPath(new[] {
                PointF.Empty, new PointF((float)Size, 0), new PointF((float)Size, (float)Size), new PointF(0, (float)Size) }, true) }, true) : Outlines(serial)).SelectMany(p => p.Points).ToList();
            return RectangleF.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }
        public MarkingOperation Operation(string serial)
        {
            Validate();
            // Generate hatching in object coordinates; rotate/translate the completed paths afterwards.
            LayoutItem local = Copy(); local.X = local.Y = local.Rotation = 0;
            List<MarkingPath> paths = local.Outlines(serial);
            if (Mode == LayoutMode.Fill)
            {
                if (Kind == LayoutKind.QR) paths = MarkingGeometry.QrFill(Content.Replace("{serial}", serial), Size * Scale, Spacing);
                else paths = MarkingGeometry.Fill(paths, Spacing);
            }
            else paths = MarkingGeometry.Widen(paths, (MarkingMode)(int)Mode, Width, Spacing, Pitch);
            return new MarkingOperation { Paths = Transform(paths, false), Speed = Speed, Power = Power, LaserMode = LaserMode };
        }
        public LayoutItem Copy() { return LayoutDocument.Deserialize(LayoutDocument.Serialize(new LayoutDocument { Items = new List<LayoutItem> { this } })).Items[0]; }

        public static LayoutItem ImportSvg(string filename, GrblCore core)
        {
            XDocument xml;
            using (XmlReader reader = XmlReader.Create(filename, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) xml = XDocument.Load(reader);
            string[] unsupported = { "text", "image", "use", "clipPath", "mask", "filter", "symbol" };
            if (xml.Descendants().Any(e => unsupported.Contains(e.Name.LocalName))) throw new ArgumentException("SVGの文字はパス化し、画像・クリップ・参照要素は展開してください。");
            SvgConverter.GCodeFromSVG converter = new SvgConverter.GCodeFromSVG { CaptureGeometry = true, UseLegacyBezier = false };
            converter.convertFromText(xml.ToString(), core);
            List<PointF> all = converter.Geometry.SelectMany(p => p.Points).ToList();
            if (all.Count == 0) throw new ArgumentException("SVGに加工可能なパスがありません。");
            float left = all.Min(p => p.X), bottom = all.Min(p => p.Y);
            LayoutItem item = new LayoutItem { Kind = LayoutKind.SVG, Name = Path.GetFileNameWithoutExtension(filename) };
            item.Geometry = converter.Geometry.Select(p => new LayoutPath { Closed = p.Closed, Points = p.Points.Select(q => new LayoutPoint { X = q.X - left, Y = q.Y - bottom }).ToList() }).ToList();
            item.Validate(); return item;
        }
    }

    [DataContract]
    internal sealed class LayoutDocument
    {
        [DataMember] public int Version = 1;
        [DataMember] public List<LayoutItem> Items = new List<LayoutItem>();
        [DataMember] public double OriginX, OriginY, RetreatX, RetreatY;
        [DataMember] public bool Retreat;
        public bool UsesSerial { get { return Items.Any(i => i.Enabled && i.UsesSerial); } }
        public void Validate()
        {
            if (Version != 1 || Items == null || Items.Count > 200 || Items.Any(i => i == null)) throw new ArgumentException("対応していないレイアウト形式です。");
            LayoutItem.Range(OriginX, -10000, 10000); LayoutItem.Range(OriginY, -10000, 10000);
            LayoutItem.Range(RetreatX, 0, 1000); LayoutItem.Range(RetreatY, 0, 1000);
            foreach (LayoutItem item in Items) item.Validate();
            if (Items.Where(i => i.Kind == LayoutKind.SVG).Sum(i => i.Geometry.Sum(p => (long)p.Points.Count)) > 500000) throw new ArgumentException("SVGの総座標数が多すぎます。");
        }
        public List<MarkingOperation> Operations(string serial)
        {
            Validate();
            var operations = new List<MarkingOperation>(); long points = 0;
            foreach (LayoutItem item in Items.Where(i => i.Enabled))
            {
                MarkingOperation operation = item.Operation(serial); points += operation.Paths.Sum(p => (long)p.Points.Count);
                if (points > 500000) throw new ArgumentException("全体の軌道が多すぎます。間隔を広げてください。");
                operations.Add(operation);
            }
            if (operations.Count == 0) throw new ArgumentException("加工するオブジェクトがありません。");
            return operations;
        }
        public static string Serialize(LayoutDocument document)
        {
            using (MemoryStream stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(LayoutDocument)).WriteObject(stream, document); return System.Text.Encoding.UTF8.GetString(stream.ToArray()); }
        }
        public static LayoutDocument Deserialize(string json)
        {
            if (json == null || json.Length > 32000000) throw new ArgumentException("レイアウトファイルが大きすぎます。");
            using (MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            { LayoutDocument result = (LayoutDocument)new DataContractJsonSerializer(typeof(LayoutDocument)).ReadObject(stream); if (result == null) throw new ArgumentException("レイアウトがありません。"); result.Validate(); return result; }
        }
    }
}
