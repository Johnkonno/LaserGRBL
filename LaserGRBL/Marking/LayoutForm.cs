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
        private readonly ListBox objects = new ListBox { Dock = DockStyle.Fill,SelectionMode=SelectionMode.MultiExtended };
        private readonly PropertyGrid properties = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
        private readonly CheckBox markEnabled = new CheckBox { Text="加工する", AutoSize=true };
        private readonly CheckBox background = new CheckBox { Text="背景／テンプレート（加工しない・固定）", AutoSize=true };
        private readonly Label selectionInfo=new Label{AutoSize=true};
        private readonly Dictionary<ArrangeMode,ToolStripMenuItem> alignItems=new Dictionary<ArrangeMode,ToolStripMenuItem>();
        private ToolStripDropDownButton alignButton;
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8) };
        private readonly Stack<string> undo = new Stack<string>(), redo = new Stack<string>();
        private ToolStripButton undoButton, redoButton, copyButton, deleteButton, upButton, downButton;
        private string snapshot, saved, filename;
        private bool refreshing;

        public LayoutForm(GrblCore core)
        {
            this.core = core;
            Text = "LaserGRBL Marking — レイアウト"; Size = new Size(1180, 800); MinimumSize = new Size(950, 640);
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi; KeyPreview = true;
            ToolStrip tools = LayoutToolbar.Strip(DockStyle.Top);
            AddTool(tools, "新規", () => { if (ConfirmSave()) { document = new LayoutDocument(); filename = null; undo.Clear(); redo.Clear(); saved = LayoutDocument.Serialize(document); RefreshScene(-1); } });
            AddTool(tools, "開く", Open); AddTool(tools, "保存", () => Save(false)); AddTool(tools, "別名保存", () => Save(true));
            tools.Items.Add(new ToolStripSeparator());
            undoButton=AddTool(tools,"元に戻す",()=>Undo(false));redoButton=AddTool(tools,"やり直し",()=>Undo(true));
            undoButton.ToolTipText="元に戻す (Ctrl+Z)";redoButton.ToolTipText="やり直し (Ctrl+Y)";
            tools.Items.Add(new ToolStripSeparator());
            AddTool(tools, "画像トレース", ImportBitmap); AddTool(tools, "SVG追加", ImportSvg); AddTool(tools, "文字", () => AddItem(LayoutKind.Text, "TEXT"));
            AddTool(tools, "QR", () => AddItem(LayoutKind.QR, "https://example.com"));
            AddTool(tools, "連番文字", () => AddItem(LayoutKind.Text, "{serial}")); AddTool(tools, "連番QR", () => AddItem(LayoutKind.QR, "{serial}"));
            tools.Items.Add(new ToolStripSeparator()); AddTool(tools, "全体表示", () => canvas.Fit());
            AddTool(tools, "定規・寸法補正", () => { using (CalibrationForm form = new CalibrationForm(core)) form.ShowDialog(this); });
            alignButton=LayoutToolbar.Menu("整列");tools.Items.Add(alignButton);
            string[] alignNames={"左揃え","左右中央揃え","右揃え","上揃え","上下中央揃え","下揃え","左右を等間隔","上下を等間隔"};
            foreach(ArrangeMode mode in Enum.GetValues(typeof(ArrangeMode)))
            {
                ArrangeMode operation=mode;var item=LayoutToolbar.MenuItem(alignNames[(int)mode]);
                item.Click+=(s,e)=>Safely(()=>Arrange(operation));alignButton.DropDownItems.Add(item);alignItems.Add(mode,item);
                if(mode==ArrangeMode.Right||mode==ArrangeMode.Bottom)alignButton.DropDownItems.Add(new ToolStripSeparator());
            }
            ToolStripButton generateButton=AddTool(tools,"Gコード生成 →",Generate);
            generateButton.DisplayStyle=ToolStripItemDisplayStyle.ImageAndText;generateButton.Text="Gコード生成";generateButton.Size=new Size(128,40);generateButton.Alignment=ToolStripItemAlignment.Right;
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(canvas);
            SplitContainer sidebar = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            sidebar.Panel1.Controls.Add(objects); sidebar.Panel2.Controls.Add(properties); split.Panel2.Controls.Add(sidebar);
            FlowLayoutPanel flags = new FlowLayoutPanel { Dock=DockStyle.Top,Height=80,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(4) };
            flags.Controls.AddRange(new Control[] { selectionInfo,markEnabled, background }); sidebar.Panel2.Controls.Add(flags);
            markEnabled.CheckStateChanged += (s,e) => ToggleFlag(false); background.CheckStateChanged += (s,e) => ToggleFlag(true);
            Label order = new Label { Dock = DockStyle.Top, Height = 27, Text = "オブジェクト（上から加工する順序）", Padding = new Padding(4) };
            sidebar.Panel1.Controls.Add(order);
            ToolStrip actions=LayoutToolbar.Strip(DockStyle.Bottom);
            copyButton=AddTool(actions,"複製",Duplicate);deleteButton=AddTool(actions,"削除",Delete);
            actions.Items.Add(new ToolStripSeparator());upButton=AddTool(actions,"加工順を上へ",()=>Reorder(-1));downButton=AddTool(actions,"加工順を下へ",()=>Reorder(1));
            sidebar.Panel1.Controls.Add(actions);
            Controls.Add(split); Controls.Add(status);
            ToolStrip drawingTools=LayoutToolbar.Strip(DockStyle.Left);drawingTools.AutoSize=false;drawingTools.Width=52;drawingTools.CanOverflow=false;drawingTools.LayoutStyle=ToolStripLayoutStyle.VerticalStackWithOverflow;
            Dictionary<DrawingTool,ToolStripButton> toolButtons = new Dictionary<DrawingTool,ToolStripButton>();
            string[] toolNames = { "選択", "線", "矩形", "円", "楕円", "折れ線", "多角形", "正多角形", "円弧", "フリーハンド" };
            foreach (DrawingTool tool in Enum.GetValues(typeof(DrawingTool)))
            {
                DrawingTool selectedTool=tool; ToolStripButton button=LayoutToolbar.Drawing(tool,toolNames[(int)tool]);
                if(tool==DrawingTool.Line) drawingTools.Items.Add(new ToolStripSeparator());
                button.Click += (s,e) => canvas.SetTool(selectedTool); drawingTools.Items.Add(button); toolButtons.Add(tool,button);
            }
            canvas.ToolChanged += tool => { foreach (var pair in toolButtons) pair.Value.Checked=pair.Key==tool; status.Text=canvas.ToolTip; };
            canvas.ShapeCreated += item => Safely(() => Add(item,true,false)); canvas.DrawingError += message => status.Text=message;
            split.Panel1.Controls.Add(drawingTools); Controls.Add(tools); canvas.SetTool(DrawingTool.Select);
            split.SplitterDistance = 790; sidebar.SplitterDistance = 190;
            objects.SelectedIndexChanged += (s, e) => { if (!refreshing) SelectMany(objects.SelectedIndices.Cast<int>(),objects.SelectedIndex); };
            canvas.SelectionChanged += index => { SelectMany(canvas.SelectedIndices,index); };
            canvas.Edited += before => { Commit(before); RefreshScene(canvas.SelectedIndices,canvas.SelectedIndex); };
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
        private ToolStripButton AddTool(ToolStrip strip, string text, Action action)
        { ToolStripButton button=LayoutToolbar.Command(text);button.Click+=(s,e)=>Safely(action);strip.Items.Add(button);return button; }
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
        private void RefreshScene(int index) { RefreshScene(new[]{index},index); }
        private void RefreshScene(IEnumerable<int> indices,int index)
        {
            var selection=indices.ToList();
            refreshing = true;
            objects.Items.Clear(); foreach (LayoutItem item in document.Items) objects.Items.Add(item);
            if (canvas.Document != document) canvas.SetTool(DrawingTool.Select);
            canvas.Document = document;
            canvas.Serial = SerialJournal.Open(Path.Combine(GrblCore.DataPath, "serial-number.json")).Format();
            snapshot = LayoutDocument.Serialize(document);
            SelectMany(selection,index); canvas.Rebuild(); refreshing = false;
            Text = "LaserGRBL Marking — レイアウト" + (filename == null ? "" : " — " + Path.GetFileName(filename)) + (snapshot != saved ? " *" : "");
            status.Text = "mm単位／上＝+Y、右＝+X　Ctrl／Shift：複数選択　範囲ドラッグ：選択　選択図形をドラッグ：まとめて移動　ホイール：ズーム　中／右ドラッグ：画面移動\r\n{serial} → " + canvas.Serial + "　青は配置図です。生成後に実際の加工軌道と退避を確認してください。";
            if (canvas.Tool!=DrawingTool.Select) status.Text=canvas.ToolTip;
        }
        private void Select(int index) { SelectMany(new[]{index},index); }
        private void SelectMany(IEnumerable<int> indices,int primary)
        {
            var selection=indices.Distinct().Where(i=>i>=0&&i<document.Items.Count).OrderBy(i=>i).ToList();
            bool prior=refreshing;refreshing=true;
            canvas.SetSelection(selection,primary,false);
            if(!objects.SelectedIndices.Cast<int>().SequenceEqual(selection)){objects.ClearSelected();foreach(int i in selection)objects.SetSelected(i,true);}
            var selected=selection.Select(i=>document.Items[i]).ToList();var movable=selected.Where(i=>!i.Background).ToList();
            LayoutItem single=selected.Count==1?selected[0]:null;
            properties.SelectedObject=single==null?null:new LayoutPropertyView(single);properties.Enabled=single!=null&&!single.Background;
            selectionInfo.Text=selection.Count==0?"未選択":selection.Count+"個選択"+(selected.Count!=movable.Count?"（固定 "+(selected.Count-movable.Count)+"個）":"");
            background.Enabled=selected.Count>0;background.ThreeState=false;
            background.CheckState=selected.Count==0||selected.All(i=>!i.Background)?CheckState.Unchecked:selected.All(i=>i.Background)?CheckState.Checked:CheckState.Indeterminate;
            markEnabled.Enabled=movable.Count>0;markEnabled.ThreeState=false;
            markEnabled.CheckState=movable.Count==0||movable.All(i=>!i.Enabled)?CheckState.Unchecked:movable.All(i=>i.Enabled)?CheckState.Checked:CheckState.Indeterminate;
            copyButton.Enabled=deleteButton.Enabled=movable.Count>0;
            upButton.Enabled=selection.Any(i=>i>0&&!selection.Contains(i-1));downButton.Enabled=selection.Any(i=>i<document.Items.Count-1&&!selection.Contains(i+1));
            undoButton.Enabled=undo.Count>0;redoButton.Enabled=redo.Count>0;
            alignButton.Enabled=selected.Count>=2&&movable.Count>0;
            foreach(var pair in alignItems)pair.Value.Enabled=pair.Key==ArrangeMode.SpaceX||pair.Key==ArrangeMode.SpaceY?movable.Count>=3:alignButton.Enabled;
            refreshing=prior;canvas.Invalidate();
        }
        private void ToggleFlag(bool isBackground)
        {
            if(refreshing||canvas.SelectedIndices.Count==0)return;
            string before=snapshot;var selection=canvas.SelectedIndices.ToList();int primary=canvas.SelectedIndex;
            foreach(int i in selection)
            {
                LayoutItem item=document.Items[i];
                if(isBackground)item.Background=background.CheckState==CheckState.Checked;
                else if(!item.Background)item.Enabled=markEnabled.CheckState==CheckState.Checked;
            }
            Commit(before);RefreshScene(selection,primary);
        }
        private void Arrange(ArrangeMode mode)
        {
            var selection=canvas.SelectedIndices.ToList();string before=snapshot;int primary=canvas.SelectedIndex;
            LayoutArrange.Apply(document,selection,mode,canvas.Serial);Commit(before);RefreshScene(selection,primary);
        }
        private void Add(LayoutItem item, bool useDefaultPower = true, bool fit = true)
        {
            if (document.Items.Count >= 200) throw new ArgumentException("オブジェクトは200個までです。");
            if (useDefaultPower) item.Power = (double)Math.Max(1, GrblCore.Configuration.MaxPWM);
            item.Validate(); item.Outlines(canvas.Serial);
            string before = snapshot; document.Items.Add(item); Commit(before); RefreshScene(document.Items.Count - 1); if (fit) canvas.Fit();
        }
        private void AddItem(LayoutKind kind, string content)
        { Add(new LayoutItem { Kind = kind, Content = content, Name = (content == "{serial}" ? "連番" : "") + (kind == LayoutKind.QR ? "QR" : "文字"), Size = kind == LayoutKind.QR ? 15 : 5, Mode = LayoutMode.Fill }); }
        private void ImportBitmap()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter="画像|*.bmp;*.png;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|すべてのファイル|*.*" })
                if (dialog.ShowDialog(this)==DialogResult.OK)
                {
                    LayoutItem item=RasterConverter.RasterToLaserForm.CreateLayoutTrace(this,core,dialog.FileName);
                    if (item!=null) Add(item,false);
                }
        }
        private void ImportSvg()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "SVG|*.svg" })
                if (dialog.ShowDialog(this) == DialogResult.OK) Add(LayoutItem.ImportSvg(dialog.FileName, core));
        }
        private void Duplicate()
        {
            var selected=canvas.SelectedIndices.Where(i=>!document.Items[i].Background).ToList();if(selected.Count==0)return;
            if(document.Items.Count+selected.Count>200)throw new ArgumentException("オブジェクトは200個までです。");
            var copies=selected.Select(i=>document.Items[i].Copy()).ToList();
            PointF delta=LayoutArrange.ClampMove(copies.Select(i=>new PointF((float)i.X,(float)i.Y)),2,2);
            foreach(var item in copies){item.Name=item.Name.Substring(0,Math.Min(195,item.Name.Length))+" コピー";item.X+=delta.X;item.Y+=delta.Y;}
            if(document.Items.Concat(copies).Where(i=>i.HasGeometry).Sum(i=>i.Geometry.Sum(path=>(long)path.Points.Count))>500000)throw new ArgumentException("パスの総座標数が多すぎます。");
            string before=snapshot;int first=document.Items.Count;document.Items.AddRange(copies);Commit(before);RefreshScene(Enumerable.Range(first,copies.Count),first);
        }
        private void Delete()
        {
            var selected=canvas.SelectedIndices.Where(i=>!document.Items[i].Background).OrderByDescending(i=>i).ToList();if(selected.Count==0)return;
            string before=snapshot;foreach(int i in selected)document.Items.RemoveAt(i);Commit(before);RefreshScene(Math.Min(selected.Min(),document.Items.Count-1));
        }
        private void Reorder(int direction)
        {
            var selected=new HashSet<LayoutItem>(canvas.SelectedIndices.Select(i=>document.Items[i]));if(selected.Count==0)return;
            LayoutItem primary=canvas.SelectedIndex>=0?document.Items[canvas.SelectedIndex]:null;string before=snapshot;
            if(direction<0)
            {
                for(int i=1;i<document.Items.Count;i++)if(selected.Contains(document.Items[i])&&!selected.Contains(document.Items[i-1]))
                {var item=document.Items[i];document.Items[i]=document.Items[i-1];document.Items[i-1]=item;}
            }
            else
            {
                for(int i=document.Items.Count-2;i>=0;i--)if(selected.Contains(document.Items[i])&&!selected.Contains(document.Items[i+1]))
                {var item=document.Items[i];document.Items[i]=document.Items[i+1];document.Items[i+1]=item;}
            }
            Commit(before);RefreshScene(Enumerable.Range(0,document.Items.Count).Where(i=>selected.Contains(document.Items[i])),document.Items.IndexOf(primary));
        }
        private void Undo(bool forward)
        {
            Stack<string> from = forward ? redo : undo, to = forward ? undo : redo;
            if (from.Count == 0) return; to.Push(snapshot); TrimHistory(to); document = LayoutDocument.Deserialize(from.Pop()); RefreshScene(-1);
        }
        private void Shortcut(object sender, KeyEventArgs e)
        {
            // Text editing keeps its own shortcuts; document shortcuts apply to the canvas/list.
            if (properties.ContainsFocus || markEnabled.ContainsFocus || background.ContainsFocus) return;
            if (canvas.Tool != DrawingTool.Select && !e.Control) return;
            if (e.Control && e.KeyCode == Keys.S) { Save(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { Undo(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Y) { Undo(true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A && canvas.Tool==DrawingTool.Select) { SelectMany(Enumerable.Range(0,document.Items.Count),0); e.Handled=true; }
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
                filename = target; saved = json; RefreshScene(canvas.SelectedIndices,canvas.SelectedIndex); return true;
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
            document.Validate(); if (!document.Items.Any(i => i.CanMark)) throw new ArgumentException("加工するオブジェクトを追加してください。");
            string before = snapshot;
            using (MarkingForm form = new MarkingForm(core, document)) form.ShowDialog(this);
            Commit(before); RefreshScene(canvas.SelectedIndices,canvas.SelectedIndex);
        }
    }

    internal sealed class LayoutCanvas : Panel
    {
        public LayoutDocument Document;
        public string Serial = "000001";
        private readonly SortedSet<int> selected=new SortedSet<int>();
        private int primary=-1;
        public int SelectedIndex { get { return primary; } set { SetSelection(new[]{value},value,false); } }
        public IList<int> SelectedIndices { get { return selected.ToList().AsReadOnly(); } }
        public void SetSelection(IEnumerable<int> indices,int active,bool notify)
        {
            var values=indices.Distinct().Where(i=>Document!=null&&i>=0&&i<Document.Items.Count).ToList();
            selected.Clear();foreach(int i in values)selected.Add(i);primary=selected.Contains(active)?active:selected.Count>0?selected.Max:-1;
            if(notify&&SelectionChanged!=null)SelectionChanged(primary);Invalidate();
        }
        private bool ClickSelect(int index,Keys modifiers)
        {
            var next=selected.ToList();bool extend=(modifiers&(Keys.Control|Keys.Shift))!=0;
            if(index<0){if(!extend)next.Clear();SetSelection(next,primary,true);return false;}
            if(extend){if(next.Contains(index)){next.Remove(index);SetSelection(next,primary,true);return false;}next.Add(index);}
            else if(!next.Contains(index))next=new List<int>{index};
            SetSelection(next,index,true);return true;
        }
        public event Action<int> SelectionChanged;
        public event Action<string> Edited;
        public event Action<LayoutItem> ShapeCreated;
        public event Action<DrawingTool> ToolChanged;
        public event Action<string> DrawingError;
        public DrawingTool Tool { get; private set; }
        private readonly List<PointF> draft = new List<PointF>();
        private PointF hover;
        private bool drawingGesture;
        public string ToolTip
        {
            get
            {
                switch (Tool)
                {
                    case DrawingTool.Circle: case DrawingTool.RegularPolygon: return "中心を押して半径までドラッグ。Escで選択に戻ります。";
                    case DrawingTool.Polyline: case DrawingTool.Polygon: return "頂点をクリック。ダブルクリック／Enterで確定、Backspaceで1点戻る、Escで中止。";
                    case DrawingTool.Arc: return "円弧の始点 → 通過点 → 終点をクリック。Escで中止します。";
                    case DrawingTool.Freehand: return "左ボタンを押したまま描きます。Escで中止します。";
                    case DrawingTool.Select: return "Ctrl／Shiftクリック・範囲ドラッグで複数選択。選択した図形をドラッグしてまとめて移動できます。";
                    default: return "始点から終点へドラッグして描画。矩形・楕円はShiftで縦横を同じ寸法に。Escで選択に戻ります。";
                }
            }
        }
        public void SetTool(DrawingTool tool)
        {
            moving=resizing=panning=drawingGesture=marquee=false; draft.Clear(); Capture=false; Tool=tool;
            Cursor=tool==DrawingTool.Select?Cursors.Default:Cursors.Cross;
            if (ToolChanged!=null) ToolChanged(tool); Invalidate();
        }
        private sealed class Drawing { public List<MarkingPath> Paths; public RectangleF Bounds; }
        private readonly List<Drawing> drawings = new List<Drawing>();
        private float zoom = 7, viewX = 70, viewY = 500;
        private bool moving, resizing, panning,marquee;
        private Point marqueeEnd;
        private List<int> marqueeBase=new List<int>();
        private readonly Dictionary<int,double[]> moveOrigins=new Dictionary<int,double[]>();
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
            base.OnMouseDown(e); Focus(); mouseStart=e.Location;
            if (e.Button==MouseButtons.Middle || e.Button==MouseButtons.Right) { panning=true;panX=viewX;panY=viewY;Capture=true;return; }
            if (e.Button!=MouseButtons.Left || Document==null) return;
            hover=World(e.Location);
            if (Tool!=DrawingTool.Select)
            {
                if (Tool==DrawingTool.Polyline || Tool==DrawingTool.Polygon || Tool==DrawingTool.Arc)
                {
                    if (draft.Count==0 || Distance(draft[draft.Count-1],hover)>0.0001) draft.Add(hover);
                    if ((Tool==DrawingTool.Arc && draft.Count==3) || e.Clicks>=2) FinishVertices();
                }
                else { draft.Clear();draft.Add(hover);drawingGesture=true;Capture=true; }
                Invalidate();return;
            }
            resizing=selected.Count==1 && SelectedIndex>=0 && SelectedIndex<drawings.Count && !Document.Items[SelectedIndex].Background && Handle(drawings[SelectedIndex].Bounds).Contains(e.Location) && (ModifierKeys&(Keys.Control|Keys.Shift))==0;
            int index=resizing?SelectedIndex:-1;
            if(!resizing)foreach(int i in Enumerable.Range(0,drawings.Count).OrderBy(n=>Document.Items[n].Background?0:1).Reverse())
            {RectangleF hit=Screen(drawings[i].Bounds);hit.Inflate(4,4);if(hit.Contains(e.Location)){index=i;break;}}
            var previousSelection=selected.ToList();bool selectedHit=ClickSelect(index,ModifierKeys);
            if(index<0||Document.Items[index].Background)
            {
                marquee=true;marqueeEnd=e.Location;marqueeBase=(ModifierKeys&(Keys.Control|Keys.Shift))!=0?previousSelection:new List<int>();Capture=true;
            }
            else if(selectedHit&&!Document.Items[index].Background)
            {
                LayoutItem item=Document.Items[index];before=LayoutDocument.Serialize(Document);
                startX=item.X;startY=item.Y;startScale=item.Scale;
                PointF point=World(e.Location);startDistance=Math.Max(0.001,Distance(point,new PointF((float)startX,(float)startY)));
                moveOrigins.Clear();foreach(int i in selected.Where(i=>!Document.Items[i].Background))moveOrigins.Add(i,new[]{Document.Items[i].X,Document.Items[i].Y});
                moving=!resizing;Capture=true;
            }
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if(panning){viewX=panX+e.X-mouseStart.X;viewY=panY+e.Y-mouseStart.Y;Invalidate();return;}
            hover=World(e.Location);
            if(Tool!=DrawingTool.Select)
            {
                if(drawingGesture && Tool==DrawingTool.Freehand && draft.Count>0 && draft.Count<20000 && Distance(draft[draft.Count-1],hover)*zoom>=2) draft.Add(hover);
                Invalidate();return;
            }
            if(marquee){marqueeEnd=e.Location;Invalidate();return;}
            if((!moving&&!resizing)||SelectedIndex<0)return;
            LayoutItem item=Document.Items[SelectedIndex];if(item.Background)return;
            if(moving)
            {
                PointF delta=LayoutArrange.ClampMove(moveOrigins.Values.Select(p=>new PointF((float)p[0],(float)p[1])),(e.X-mouseStart.X)/zoom,-(e.Y-mouseStart.Y)/zoom);
                foreach(var pair in moveOrigins){Document.Items[pair.Key].X=pair.Value[0]+delta.X;Document.Items[pair.Key].Y=pair.Value[1]+delta.Y;}
            }
            else{PointF p=World(e.Location);item.Scale=Math.Max(0.001,Math.Min(1000,startScale*Distance(p,new PointF((float)startX,(float)startY))/startDistance));}
            Rebuild();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if(panning){panning=false;Capture=false;return;}
            if(e.Button!=MouseButtons.Left)return;
            if(drawingGesture)
            {
                hover=World(e.Location);drawingGesture=false;Capture=false;
                try
                {
                    LayoutItem item=null;
                    if(Tool==DrawingTool.Freehand && draft.Count>0){if(Distance(draft[draft.Count-1],hover)>0.0001)draft.Add(hover);if(draft.Count>=2)item=LayoutDrawing.Path("フリーハンド",draft,false);}
                    else if(draft.Count>0 && Distance(draft[0],hover)>=0.01) item=DraggedShape();
                    draft.Clear();if(item!=null&&ShapeCreated!=null)ShapeCreated(item);
                }
                catch(Exception ex){draft.Clear();if(DrawingError!=null)DrawingError(ex.Message);}
                Invalidate();return;
            }
            if(marquee)
            {
                marqueeEnd=e.Location;RectangleF box=SelectionBox();marquee=false;Capture=false;
                if(Distance(new PointF(mouseStart.X,mouseStart.Y),new PointF(e.X,e.Y))<3){Invalidate();return;}
                SetSelection(marqueeBase.Concat(Enumerable.Range(0,drawings.Count).Where(i=>box.Contains(Screen(drawings[i].Bounds)))),primary,true);return;
            }
            bool changed=moving||resizing;moving=resizing=false;Capture=false;if(changed&&Edited!=null)Edited(before);
        }
        private static double Distance(PointF a,PointF b){return Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));}
        private LayoutItem DraggedShape()
        {
            PointF start=draft[0];
            if(Tool==DrawingTool.Line)return LayoutDrawing.Path("線",new[]{start,hover},false);
            double x=Math.Min(start.X,hover.X),y=Math.Min(start.Y,hover.Y),w=Math.Abs(hover.X-start.X),h=Math.Abs(hover.Y-start.Y);
            if(Tool==DrawingTool.Circle||Tool==DrawingTool.RegularPolygon){double radius=Distance(start,hover);w=h=2*radius;x=start.X-radius;y=start.Y-radius;}
            else if((ModifierKeys&Keys.Shift)!=0){w=h=Math.Max(w,h);x=hover.X<start.X?start.X-w:start.X;y=hover.Y<start.Y?start.Y-h:start.Y;}
            return new LayoutItem{Kind=Tool==DrawingTool.Rectangle?LayoutKind.Rectangle:Tool==DrawingTool.RegularPolygon?LayoutKind.RegularPolygon:LayoutKind.Ellipse,
                Name=Tool==DrawingTool.Rectangle?"矩形":Tool==DrawingTool.Circle?"円":Tool==DrawingTool.RegularPolygon?"正多角形":"楕円",X=x,Y=y,ShapeWidth=Math.Max(0.01,w),ShapeHeight=Math.Max(0.01,h),Mode=LayoutMode.Trace};
        }
        private void FinishVertices()
        {
            if(draft.Count<(Tool==DrawingTool.Polygon||Tool==DrawingTool.Arc?3:2))return;
            try
            {
                LayoutItem item=Tool==DrawingTool.Arc?LayoutDrawing.Path("円弧",LayoutDrawing.Arc(draft[0],draft[1],draft[2]),false):
                    LayoutDrawing.Path(Tool==DrawingTool.Polygon?"多角形":"折れ線",draft,Tool==DrawingTool.Polygon);
                draft.Clear();if(ShapeCreated!=null)ShapeCreated(item);
            }
            catch(Exception ex){draft.Clear();if(DrawingError!=null)DrawingError(ex.Message);}
            Invalidate();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if(e.KeyCode==Keys.Escape)
            {
                if(moving)foreach(var pair in moveOrigins){Document.Items[pair.Key].X=pair.Value[0];Document.Items[pair.Key].Y=pair.Value[1];}
                if(resizing&&SelectedIndex>=0)Document.Items[SelectedIndex].Scale=startScale;
                SetTool(DrawingTool.Select);Rebuild();e.Handled=true;
            }
            else if((Tool==DrawingTool.Polyline||Tool==DrawingTool.Polygon||Tool==DrawingTool.Arc) && e.KeyCode==Keys.Enter){FinishVertices();e.Handled=true;}
            else if(Tool!=DrawingTool.Select && (e.KeyCode==Keys.Back||e.KeyCode==Keys.Delete)){if(!drawingGesture && draft.Count>0)draft.RemoveAt(draft.Count-1);Invalidate();e.Handled=true;}
        }
        private RectangleF SelectionBox() { return RectangleF.FromLTRB(Math.Min(mouseStart.X,marqueeEnd.X),Math.Min(mouseStart.Y,marqueeEnd.Y),Math.Max(mouseStart.X,marqueeEnd.X),Math.Max(mouseStart.Y,marqueeEnd.Y)); }
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
            foreach (int i in Enumerable.Range(0,drawings.Count).OrderBy(n=>Document.Items[n].Background?0:1))
            {
                LayoutItem item = Document.Items[i]; Drawing drawing = drawings[i];
                Color color = item.Background ? Color.SlateGray : !item.Enabled ? Color.Gray : selected.Contains(i) ? Color.DarkOrange : Color.RoyalBlue;
                using (Pen pen = new Pen(color, 1) { DashStyle=item.Background?DashStyle.Dash:DashStyle.Solid })
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
                if (selected.Contains(i))
                {
                    RectangleF bounds = Screen(drawing.Bounds);
                    using (Pen select = new Pen(Color.DarkOrange) { DashStyle = DashStyle.Dash }) g.DrawRectangle(select, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    if (!item.Background && selected.Count==1) { RectangleF handle = Handle(drawing.Bounds); g.FillRectangle(Brushes.White, handle); g.DrawRectangle(Pens.DarkOrange, handle.X, handle.Y, handle.Width, handle.Height); }
                    g.DrawString(drawing.Bounds.Width.ToString("0.###") + " × " + drawing.Bounds.Height.ToString("0.###") + " mm", Font, Brushes.DarkOrange, bounds.Left, bounds.Bottom + 4);
                }
            }
            if(marquee)
            {
                RectangleF box=SelectionBox();using(Brush fill=new SolidBrush(Color.FromArgb(35,62,132,220)))g.FillRectangle(fill,box);
                using(Pen pen=new Pen(Color.RoyalBlue){DashStyle=DashStyle.Dash})g.DrawRectangle(pen,box.X,box.Y,box.Width,box.Height);
            }
            if(selected.Count>1)
            {
                RectangleF group=drawings[selected.First()].Bounds;foreach(int i in selected.Skip(1))group=RectangleF.Union(group,drawings[i].Bounds);RectangleF box=Screen(group);
                using(Pen pen=new Pen(Color.DarkOrange,2){DashStyle=DashStyle.Dash})g.DrawRectangle(pen,box.X,box.Y,box.Width,box.Height);
                g.DrawString(selected.Count+"個選択",Font,Brushes.DarkOrange,box.Left,box.Top-20);
            }
            if(draft.Count>0)
            {
                using(Pen pen=new Pen(Color.DarkOrange,1){DashStyle=DashStyle.Dash})
                {
                    List<PointF> points=new List<PointF>(draft);
                    if(Tool==DrawingTool.Rectangle||Tool==DrawingTool.Circle||Tool==DrawingTool.Ellipse||Tool==DrawingTool.RegularPolygon)
                    { try { foreach(var path in DraggedShape().Outlines(Serial)){var line=path.Points.Select(Map).ToArray();if(line.Length>1)g.DrawLines(pen,line);if(path.Closed)g.DrawLine(pen,line[line.Length-1],line[0]);} } catch(ArgumentException) {} }
                    else
                    {
                        if(Tool!=DrawingTool.Freehand)points.Add(hover);
                        if(Tool==DrawingTool.Arc&&points.Count==3){try{points=LayoutDrawing.Arc(points[0],points[1],points[2]);}catch(ArgumentException){}}
                        if(points.Count>1)g.DrawLines(pen,points.Select(Map).ToArray());
                        if(Tool==DrawingTool.Polygon&&points.Count>2)g.DrawLine(pen,Map(points[points.Count-1]),Map(points[0]));
                    }
                }
            }
            g.DrawString("レイアウト  " + zoom.ToString("0.#") + " px/mm", Font, Brushes.Black, 8, 8);
        }
    }
}
