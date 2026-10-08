// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LaserGRBL.Marking
{
    internal sealed class LayoutForm : Form
    {
        private readonly GrblCore core;
        private LayoutDocument document = new LayoutDocument();
        private readonly LayoutCanvas canvas = new LayoutCanvas();
        private readonly ListBox objects = new ListBox { Dock = DockStyle.Fill };
        private readonly PropertyGrid properties = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8) };
        private readonly Stack<string> undo = new Stack<string>(), redo = new Stack<string>();
        private string snapshot, saved, filename;
        private bool refreshing;

        public LayoutForm(GrblCore core)
        {
            this.core = core;
            Text = "LaserGRBL Marking — レイアウト"; Size = new Size(1180, 800); MinimumSize = new Size(950, 640);
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi; KeyPreview = true;
            ToolStrip tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
            AddTool(tools, "新規", () => { if (ConfirmSave()) { document = new LayoutDocument(); filename = null; undo.Clear(); redo.Clear(); saved = LayoutDocument.Serialize(document); RefreshScene(-1); } });
            AddTool(tools, "開く", Open); AddTool(tools, "保存", () => Save(false)); AddTool(tools, "別名保存", () => Save(true));
            tools.Items.Add(new ToolStripSeparator());
            AddTool(tools, "SVG追加", ImportSvg); AddTool(tools, "文字", () => AddItem(LayoutKind.Text, "TEXT"));
            AddTool(tools, "QR", () => AddItem(LayoutKind.QR, "https://example.com"));
            AddTool(tools, "連番文字", () => AddItem(LayoutKind.Text, "{serial}")); AddTool(tools, "連番QR", () => AddItem(LayoutKind.QR, "{serial}"));
            tools.Items.Add(new ToolStripSeparator()); AddTool(tools, "全体表示", () => canvas.Fit());
            AddTool(tools, "定規・寸法補正", () => { using (CalibrationForm form = new CalibrationForm(core)) form.ShowDialog(this); });
            AddTool(tools, "Gコード生成 →", Generate);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(canvas);
            SplitContainer sidebar = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            sidebar.Panel1.Controls.Add(objects); sidebar.Panel2.Controls.Add(properties); split.Panel2.Controls.Add(sidebar);
            Label order = new Label { Dock = DockStyle.Top, Height = 27, Text = "オブジェクト（上から加工する順序）", Padding = new Padding(4) };
            sidebar.Panel1.Controls.Add(order);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
            Button copy = new Button { Text = "複製", Width = 58 }, delete = new Button { Text = "削除", Width = 58 }, up = new Button { Text = "↑", Width = 38 }, down = new Button { Text = "↓", Width = 38 };
            copy.Click += (s, e) => Safely(Duplicate); delete.Click += (s, e) => Safely(Delete); up.Click += (s, e) => Safely(() => Reorder(-1)); down.Click += (s, e) => Safely(() => Reorder(1));
            actions.Controls.AddRange(new Control[] { copy, delete, up, down }); sidebar.Panel1.Controls.Add(actions);
            Controls.Add(split); Controls.Add(status); Controls.Add(tools);
            split.SplitterDistance = 790; sidebar.SplitterDistance = 190;
            objects.SelectedIndexChanged += (s, e) => { if (!refreshing) Select(objects.SelectedIndex); };
            canvas.SelectionChanged += index => { Select(index); };
            canvas.Edited += before => { Commit(before); RefreshScene(canvas.SelectedIndex); };
            properties.PropertyValueChanged += (s, e) =>
            {
                string before = snapshot; int index = canvas.SelectedIndex;
                try { document.Validate(); if (index >= 0) document.Items[index].Outlines(canvas.Serial); Commit(before); RefreshScene(index); }
                catch (Exception ex) { document = LayoutDocument.Deserialize(before); RefreshScene(index); Error(ex); }
            };
            KeyDown += Shortcut;
            FormClosing += (s, e) => { if (!ConfirmSave()) e.Cancel = true; };
            saved = LayoutDocument.Serialize(document); RefreshScene(-1);
        }
        private void AddTool(ToolStrip strip, string text, Action action)
        { ToolStripButton button = new ToolStripButton(text); button.Click += (s, e) => { try { action(); } catch (Exception ex) { Error(ex); } }; strip.Items.Add(button); }
        private void Safely(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
        private void Error(Exception ex) { MessageBox.Show(this, ex.Message, "レイアウト", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        private void Commit(string before)
        {
            if (before != LayoutDocument.Serialize(document)) { undo.Push(before); TrimHistory(undo); redo.Clear(); }
        }
        private static void TrimHistory(Stack<string> history)
        {
            List<string> keep = new List<string>(); long chars = 0;
            foreach (string entry in history) { if (keep.Count > 0 && (keep.Count >= 100 || chars + entry.Length > 16000000)) break; keep.Add(entry); chars += entry.Length; }
            history.Clear(); foreach (string entry in keep.AsEnumerable().Reverse()) history.Push(entry);
        }
        private void RefreshScene(int index)
        {
            refreshing = true;
            objects.Items.Clear(); foreach (LayoutItem item in document.Items) objects.Items.Add(item);
            canvas.Document = document;
            canvas.Serial = SerialJournal.Open(Path.Combine(GrblCore.DataPath, "serial-number.json")).Format();
            snapshot = LayoutDocument.Serialize(document);
            Select(index); canvas.Rebuild(); refreshing = false;
            Text = "LaserGRBL Marking — レイアウト" + (filename == null ? "" : " — " + Path.GetFileName(filename)) + (snapshot != saved ? " *" : "");
            status.Text = "mm単位／上＝+Y、右＝+X　ドラッグ：移動　右上の□：等比拡大縮小　ホイール：ズーム　中／右ドラッグ：画面移動\r\n{serial} → " + canvas.Serial + "　青は配置図です。生成後に実際の加工軌道と退避を確認してください。";
        }
        private void Select(int index)
        {
            index = index >= 0 && index < document.Items.Count ? index : -1;
            bool prior = refreshing; refreshing = true;
            canvas.SelectedIndex = index; objects.SelectedIndex = index; properties.SelectedObject = index < 0 ? null : document.Items[index];
            refreshing = prior; canvas.Invalidate();
        }
        private void Add(LayoutItem item, bool useDefaultPower = true)
        {
            if (document.Items.Count >= 200) throw new ArgumentException("オブジェクトは200個までです。");
            if (useDefaultPower) item.Power = (double)Math.Max(1, GrblCore.Configuration.MaxPWM);
            item.Validate(); item.Outlines(canvas.Serial);
            string before = snapshot; document.Items.Add(item); Commit(before); RefreshScene(document.Items.Count - 1); canvas.Fit();
        }
        private void AddItem(LayoutKind kind, string content)
        { Add(new LayoutItem { Kind = kind, Content = content, Name = (content == "{serial}" ? "連番" : "") + (kind == LayoutKind.QR ? "QR" : "文字"), Size = kind == LayoutKind.QR ? 15 : 5, Mode = LayoutMode.Fill }); }
        private void ImportSvg()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "SVG|*.svg" })
                if (dialog.ShowDialog(this) == DialogResult.OK) Add(LayoutItem.ImportSvg(dialog.FileName, core));
        }
        private void Duplicate()
        {
            int index = canvas.SelectedIndex; if (index < 0) return;
            LayoutItem item = document.Items[index].Copy(); item.Name = item.Name.Substring(0, Math.Min(195, item.Name.Length)) + " コピー"; item.X = Math.Min(10000, item.X + 2); item.Y = Math.Min(10000, item.Y + 2);
            Add(item, false);
        }
        private void Delete()
        {
            int index = canvas.SelectedIndex; if (index < 0) return;
            string before = snapshot; document.Items.RemoveAt(index); Commit(before); RefreshScene(Math.Min(index, document.Items.Count - 1));
        }
        private void Reorder(int direction)
        {
            int index = canvas.SelectedIndex, target = index + direction;
            if (index < 0 || target < 0 || target >= document.Items.Count) return;
            string before = snapshot; LayoutItem item = document.Items[index]; document.Items.RemoveAt(index); document.Items.Insert(target, item); Commit(before); RefreshScene(target);
        }
        private void Undo(bool forward)
        {
            Stack<string> from = forward ? redo : undo, to = forward ? undo : redo;
            if (from.Count == 0) return; to.Push(snapshot); TrimHistory(to); document = LayoutDocument.Deserialize(from.Pop()); RefreshScene(-1);
        }
        private void Shortcut(object sender, KeyEventArgs e)
        {
            // Text editing keeps its own shortcuts; document shortcuts apply to the canvas/list.
            if (properties.ContainsFocus) return;
            if (e.Control && e.KeyCode == Keys.S) { Save(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { Undo(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Y) { Undo(true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.D) { Safely(Duplicate); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { Delete(); e.Handled = true; }
        }
        private void Open()
        {
            if (!ConfirmSave()) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "LaserGRBLレイアウト|*.lgrbl-layout" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    if (new FileInfo(dialog.FileName).Length > 32000000) throw new ArgumentException("レイアウトファイルが大きすぎます。");
                    LayoutDocument loaded = LayoutDocument.Deserialize(File.ReadAllText(dialog.FileName));
                    string serial = canvas.Serial;
                    foreach (LayoutItem item in loaded.Items) item.Outlines(serial);
                    document = loaded; filename = dialog.FileName; saved = LayoutDocument.Serialize(document); undo.Clear(); redo.Clear(); RefreshScene(-1); canvas.Fit();
                }
        }
        private bool Save(bool saveAs)
        {
            try
            {
                document.Validate(); string target = filename;
                if (saveAs || target == null)
                    using (SaveFileDialog dialog = new SaveFileDialog { Filter = "LaserGRBLレイアウト|*.lgrbl-layout", FileName = target == null ? "marking.lgrbl-layout" : Path.GetFileName(target) })
                    { if (dialog.ShowDialog(this) != DialogResult.OK) return false; target = dialog.FileName; }
                string json = LayoutDocument.Serialize(document), temp = target + ".tmp";
                File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                filename = target; saved = json; RefreshScene(canvas.SelectedIndex); return true;
            }
            catch (Exception ex) { Error(ex); return false; }
        }
        private bool ConfirmSave()
        {
            if (LayoutDocument.Serialize(document) == saved) return true;
            DialogResult answer = MessageBox.Show(this, "変更したレイアウトを保存しますか？", "レイアウト", MessageBoxButtons.YesNoCancel);
            return answer == DialogResult.No || (answer == DialogResult.Yes && Save(false));
        }
        private void Generate()
        {
            document.Validate(); if (!document.Items.Any(i => i.Enabled)) throw new ArgumentException("加工するオブジェクトを追加してください。");
            string before = snapshot;
            using (MarkingForm form = new MarkingForm(core, document)) form.ShowDialog(this);
            Commit(before); RefreshScene(canvas.SelectedIndex);
        }
    }

    internal sealed class LayoutCanvas : Panel
    {
        public LayoutDocument Document;
        public string Serial = "000001";
        public int SelectedIndex = -1;
        public event Action<int> SelectionChanged;
        public event Action<string> Edited;
        private sealed class Drawing { public List<MarkingPath> Paths; public RectangleF Bounds; }
        private readonly List<Drawing> drawings = new List<Drawing>();
        private float zoom = 7, viewX = 70, viewY = 500;
        private bool moving, resizing, panning;
        private Point mouseStart;
        private double startX, startY, startScale, startDistance;
        private float panX, panY;
        private string before;
        public LayoutCanvas() { Dock = DockStyle.Fill; BackColor = Color.White; DoubleBuffered = true; TabStop = true; }
        private PointF Map(PointF p) { return new PointF(viewX + p.X * zoom, viewY - p.Y * zoom); }
        private PointF World(Point p) { return new PointF((p.X - viewX) / zoom, (viewY - p.Y) / zoom); }
        private RectangleF Screen(RectangleF b) { PointF top = Map(new PointF(b.Left, b.Bottom)); return new RectangleF(top.X, top.Y, Math.Max(1, b.Width * zoom), Math.Max(1, b.Height * zoom)); }
        public void Rebuild()
        {
            drawings.Clear();
            if (Document != null) foreach (LayoutItem item in Document.Items) drawings.Add(new Drawing { Paths = item.Outlines(Serial), Bounds = item.Bounds(Serial) });
            Invalidate();
        }
        public void Fit()
        {
            if (drawings.Count == 0) { zoom = 7; viewX = 70; viewY = Height - 70; Invalidate(); return; }
            RectangleF bounds = drawings[0].Bounds; foreach (Drawing drawing in drawings.Skip(1)) bounds = RectangleF.Union(bounds, drawing.Bounds);
            bounds = RectangleF.Union(bounds, new RectangleF(0, 0, 0.01f, 0.01f));
            zoom = Math.Max(0.01f, Math.Min(100, Math.Min((Width - 100) / Math.Max(1, bounds.Width), (Height - 100) / Math.Max(1, bounds.Height))));
            viewX = (Width - bounds.Width * zoom) / 2 - bounds.Left * zoom; viewY = (Height + bounds.Height * zoom) / 2 + bounds.Top * zoom;
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e); PointF anchor = World(e.Location);
            zoom = Math.Max(0.01f, Math.Min(400, zoom * (float)Math.Pow(1.2, e.Delta / 120.0)));
            viewX = e.X - anchor.X * zoom; viewY = e.Y + anchor.Y * zoom; Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus(); mouseStart = e.Location;
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right) { panning = true; panX = viewX; panY = viewY; Capture = true; return; }
            if (e.Button != MouseButtons.Left || Document == null) return;
            resizing = SelectedIndex >= 0 && SelectedIndex < drawings.Count && Handle(drawings[SelectedIndex].Bounds).Contains(e.Location);
            int index = resizing ? SelectedIndex : -1;
            if (!resizing) for (int i = drawings.Count - 1; i >= 0; i--) { RectangleF hit = Screen(drawings[i].Bounds); hit.Inflate(4, 4); if (hit.Contains(e.Location)) { index = i; break; } }
            SelectedIndex = index; if (SelectionChanged != null) SelectionChanged(index);
            if (index >= 0)
            {
                LayoutItem item = Document.Items[index]; before = LayoutDocument.Serialize(Document);
                startX = item.X; startY = item.Y; startScale = item.Scale;
                PointF point = World(e.Location); startDistance = Math.Max(0.001, Math.Sqrt(Math.Pow(point.X - startX, 2) + Math.Pow(point.Y - startY, 2)));
                moving = !resizing; Capture = true;
            }
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (panning) { viewX = panX + e.X - mouseStart.X; viewY = panY + e.Y - mouseStart.Y; Invalidate(); return; }
            if ((!moving && !resizing) || SelectedIndex < 0) return;
            LayoutItem item = Document.Items[SelectedIndex];
            if (moving) { item.X = Math.Max(-10000, Math.Min(10000, startX + (e.X - mouseStart.X) / zoom)); item.Y = Math.Max(-10000, Math.Min(10000, startY - (e.Y - mouseStart.Y) / zoom)); }
            else { PointF p = World(e.Location); item.Scale = Math.Max(0.001, Math.Min(1000, startScale * Math.Sqrt(Math.Pow(p.X - startX, 2) + Math.Pow(p.Y - startY, 2)) / startDistance)); }
            Rebuild();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); bool changed = moving || resizing; moving = resizing = panning = false; Capture = false;
            if (changed && Edited != null) Edited(before);
        }
        private RectangleF Handle(RectangleF b) { RectangleF screen = Screen(b); return new RectangleF(screen.Right - 5, screen.Top - 5, 10, 10); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            double step = Math.Pow(10, Math.Floor(Math.Log10(60 / zoom))); if (step * zoom < 30) step *= 5;
            PointF lower = World(new Point(0, Height)), upper = World(new Point(Width, 0));
            using (Pen grid = new Pen(Color.FromArgb(235, 238, 242)))
            {
                for (double x = Math.Ceiling(lower.X / step) * step; x <= upper.X; x += step) { float px = Map(new PointF((float)x, 0)).X; g.DrawLine(grid, px, 0, px, Height); g.DrawString(x.ToString("0.###"), Font, Brushes.Gray, px + 2, Height - 20); }
                for (double y = Math.Ceiling(lower.Y / step) * step; y <= upper.Y; y += step) { float py = Map(new PointF(0, (float)y)).Y; g.DrawLine(grid, 0, py, Width, py); g.DrawString(y.ToString("0.###"), Font, Brushes.Gray, 2, py + 2); }
            }
            using (Pen axis = new Pen(Color.Silver)) { g.DrawLine(axis, viewX, 0, viewX, Height); g.DrawLine(axis, 0, viewY, Width, viewY); }
            for (int i = 0; i < drawings.Count; i++)
            {
                LayoutItem item = Document.Items[i]; Drawing drawing = drawings[i];
                Color color = !item.Enabled ? Color.Gray : i == SelectedIndex ? Color.DarkOrange : Color.RoyalBlue;
                using (Pen pen = new Pen(color, 1))
                using (GraphicsPath shape = new GraphicsPath(FillMode.Alternate))
                {
                    foreach (MarkingPath path in drawing.Paths)
                    {
                        if (path.Points.Count < 2) continue; PointF[] points = path.Points.Select(Map).ToArray();
                        if (path.Closed && points.Length >= 3) { shape.AddPolygon(points); } else g.DrawLines(pen, points);
                    }
                    if (item.Mode == LayoutMode.Fill) using (Brush brush = new SolidBrush(Color.FromArgb(100, color))) g.FillPath(brush, shape);
                    g.DrawPath(pen, shape);
                }
                if (i == SelectedIndex)
                {
                    RectangleF bounds = Screen(drawing.Bounds);
                    using (Pen select = new Pen(Color.DarkOrange) { DashStyle = DashStyle.Dash }) g.DrawRectangle(select, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    RectangleF handle = Handle(drawing.Bounds); g.FillRectangle(Brushes.White, handle); g.DrawRectangle(Pens.DarkOrange, handle.X, handle.Y, handle.Width, handle.Height);
                    g.DrawString(drawing.Bounds.Width.ToString("0.###") + " × " + drawing.Bounds.Height.ToString("0.###") + " mm", Font, Brushes.DarkOrange, bounds.Left, bounds.Bottom + 4);
                }
            }
            g.DrawString("レイアウト  " + zoom.ToString("0.#") + " px/mm", Font, Brushes.Black, 8, 8);
        }
    }
}
