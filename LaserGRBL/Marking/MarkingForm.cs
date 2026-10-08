// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace LaserGRBL.Marking
{
    internal sealed class MarkingForm : Form
    {
        private readonly GrblCore core;
        private readonly LayoutDocument layout;
        private readonly SerialJournal journal;
        private readonly ComboBox source = Choice("SVG", "連番", "QRコード");
        private readonly ComboBox mode = Choice("元のパス", "インセット／オフセット重ね", "パス沿いジグザグ");
        private readonly ComboBox laser = Choice("M3", "M4");
        private readonly TextBox svgFile = new TextBox { ReadOnly = true };
        private readonly TextBox qrContent = new TextBox { Text = "https://example.com" };
        private readonly TextBox prefix = new TextBox();
        private readonly TextBox suffix = new TextBox();
        private readonly TextBox font = new TextBox { Text = "Arial" };
        private readonly NumericUpDown width = Number(0.5m, 0.001m, 1000, 3);
        private readonly NumericUpDown spacing = Number(0.05m, 0.001m, 100, 3);
        private readonly NumericUpDown pitch = Number(0.2m, 0.001m, 100, 3);
        private readonly NumericUpDown textHeight = Number(5, 0.01m, 1000, 2);
        private readonly NumericUpDown qrSize = Number(15, 0.1m, 1000, 2);
        private readonly NumericUpDown gap = Number(3, 0, 1000, 2);
        private readonly NumericUpDown next = Number(1, 0, long.MaxValue, 0);
        private readonly NumericUpDown increment = Number(1, 1, 1000000, 0);
        private readonly NumericUpDown digits = Number(6, 1, 18, 0);
        private readonly NumericUpDown speed = Number(1000, 1, 100000, 0);
        private readonly NumericUpDown power = Number(255, 1, 100000, 0);
        private readonly NumericUpDown originX = Number(0, -10000, 10000, 3);
        private readonly NumericUpDown originY = Number(0, -10000, 10000, 3);
        private readonly NumericUpDown retreatX = Number(0, 0, 1000, 3);
        private readonly NumericUpDown retreatY = Number(0, 0, 1000, 3);
        private readonly CheckBox withQr = new CheckBox { Text = "連番と同じ内容のQRを併記", AutoSize = true };
        private readonly CheckBox retreat = new CheckBox { Text = "加工終了位置から自動退避", AutoSize = true };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(410, 0) };
        private readonly Label serialDisplay = new Label { AutoSize = true };
        private readonly MarkingPreview preview = new MarkingPreview();
        private readonly FlowLayoutPanel settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private readonly Button generate = new Button { Text = "軌道を生成・プレビュー", AutoSize = true };
        private readonly Button load = new Button { Text = "メイン画面へ読み込み", AutoSize = true };
        private readonly Button run = new Button { Text = "このマークを加工開始", AutoSize = true };
        private readonly Button abort = new Button { Text = "加工を中断", AutoSize = true };
        private readonly Button export = new Button { Text = "G-code保存", AutoSize = true };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly List<Control> inputs = new List<Control>();
        private readonly List<Control> svgControls = new List<Control>();
        private readonly List<Control> serialControls = new List<Control>();
        private readonly List<Control> qrControls = new List<Control>();
        private Generated prepared;
        private bool busy, running, started, serialLoaded, disposing, serialFault, loadingLayout;
        private string loadedSerialCode;

        private sealed class Generated
        {
            public string Code, Text;
            public DimensionCalibration Calibration;
            public bool Serial;
            public List<MarkingPath> Paths;
            public double X, Y, RetreatX, RetreatY;
        }

        internal DimensionCalibration GeneratedCalibration { get { return prepared == null ? null : prepared.Calibration; } }

        public MarkingForm(GrblCore core) : this(core, null) { }

        public MarkingForm(GrblCore core, LayoutDocument layout)
        {
            this.core = core;
            this.layout = layout;
            journal = SerialJournal.Open(Path.Combine(GrblCore.DataPath, "serial-number.json"));
            Text = "LaserGRBL Marking — SVG・連番・QR";
            Size = new Size(980, 760);
            MinimumSize = new Size(850, 620);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            next.Value = journal.Next;
            increment.Value = journal.Increment;
            digits.Value = journal.Digits;
            prefix.Text = journal.Prefix;
            suffix.Text = journal.Suffix;
            power.Value = Math.Min(power.Maximum, Math.Max(power.Minimum, GrblCore.Configuration.MaxPWM));
            speed.Value = Math.Min(speed.Maximum, Math.Max(speed.Minimum, Settings.GetObject("GrayScaleConversion.VectorizeOptions.BorderSpeed", 1000)));

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Controls.Add(split);
            split.SplitterDistance = 445;
            split.Panel1.Controls.Add(settings);
            split.Panel2.Controls.Add(preview);
            Label tip = new Label { Dock = DockStyle.Bottom, Height = 86, Text = "青：レーザー軌道　赤：レーザーOFF退避\r\n上＝+Y、右＝+X（加工座標）。退避は加工終了位置からの相対移動です。\r\n線幅は軌道の幅です。実際の焼き幅にはスポット径も加わります。", Padding = new Padding(8) };
            split.Panel2.Controls.Add(tip);

            Row("生成する内容", source);
            Button open = new Button { Text = "SVGを選択...", AutoSize = true };
            open.Click += OpenSvg;
            svgControls.Add(Row("入力SVG", svgFile));
            svgControls.Add(Row("", open));
            svgControls.Add(Row("太線方式", mode));
            svgControls.Add(Row("軌道の幅 mm", width));
            svgControls.Add(Row("ジグザグピッチ mm", pitch));
            qrControls.Add(Row("QRの内容", qrContent));
            serialControls.Add(Row("接頭辞", prefix));
            serialControls.Add(Row("次に打刻する番号", next));
            serialControls.Add(Row("増分", increment));
            serialControls.Add(Row("ゼロ埋め桁数", digits));
            serialControls.Add(Row("接尾辞", suffix));
            serialControls.Add(Row("文字のフォント", font));
            serialControls.Add(Row("文字高さ mm", textHeight));
            serialControls.Add(Row("", withQr));
            serialControls.Add(Row("文字とQRの間隔 mm", gap));
            serialControls.Add(Row("打刻内容", serialDisplay));
            Row("QRサイズ（余白込み）mm", qrSize);
            Row("重ね／塗り間隔 mm", spacing);
            Row("加工速度 mm/min", speed);
            Row("出力 S", power);
            Row("レーザーモード", laser);
            Row("配置 X mm", originX);
            Row("配置 Y mm", originY);
            Row("", retreat);
            Row("右へ退避 mm (+X)", retreatX);
            Row("上へ退避 mm (+Y)", retreatY);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(425, 0) };
            actions.Controls.AddRange(new Control[] { generate, load, export, run, abort });
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
            footer.Controls.Add(actions);
            footer.Controls.Add(status);
            split.Panel1.Controls.Add(footer);
            if (journal.Pending)
            {
                status.Text = "前回の結果が未確定です：" + journal.PendingText + "。実物を確認して選んでください。";
                FlowLayoutPanel recovery = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(425, 0) };
                Button retry = new Button { Text = "同じ番号を再加工する", AutoSize = true };
                Button done = new Button { Text = "加工済みとして番号を進める", AutoSize = true };
                retry.Click += (s, e) => ResolveRecovery(false, recovery);
                done.Click += (s, e) => ResolveRecovery(true, recovery);
                recovery.Controls.AddRange(new Control[] { retry, done });
                footer.Controls.Add(recovery);
            }
            source.SelectedIndexChanged += (s, e) => UpdateSource();
            foreach (Control control in inputs)
            {
                if (control is NumericUpDown) ((NumericUpDown)control).ValueChanged += Changed;
                else if (control is ComboBox) ((ComboBox)control).SelectedIndexChanged += Changed;
                else if (control is TextBox) control.TextChanged += Changed;
                else if (control is CheckBox) ((CheckBox)control).CheckedChanged += Changed;
            }
            generate.Click += Generate;
            load.Click += LoadPrepared;
            run.Click += RunPrepared;
            abort.Click += (s, e) => core.AbortProgram();
            export.Click += ExportPrepared;
            core.ProgramStarted += ProgramStarted;
            core.PhysicalJobCompleted += Completed;
            core.PhysicalJobFailed += Failed;
            core.IssueDetected += Issue;
            timer.Tick += (s, e) => RefreshButtons();
            timer.Start();
            FormClosing += Closing;
            if (layout != null)
            {
                loadingLayout = true;
                Text = "LaserGRBL Marking — 全体のGコード生成・確認";
                originX.Value = (decimal)layout.OriginX; originY.Value = (decimal)layout.OriginY;
                retreatX.Value = (decimal)layout.RetreatX; retreatY.Value = (decimal)layout.RetreatY; retreat.Checked = layout.Retreat;
                source.SelectedIndex = layout.UsesSerial ? 1 : 0;
                generate.Text = "全体のGコードを生成";
                loadingLayout = false;
            }
            UpdateSource();
        }

        private Control Row(string label, Control control)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, Width = 425, Margin = new Padding(4, 3, 4, 3), WrapContents = false };
            row.Controls.Add(new Label { Text = label, Width = 178, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            control.Width = 230;
            row.Controls.Add(control);
            settings.Controls.Add(row);
            if (!inputs.Contains(control)) inputs.Add(control);
            return row;
        }
        private static ComboBox Choice(params string[] choices)
        {
            ComboBox box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(choices); box.SelectedIndex = 0; return box;
        }
        private static NumericUpDown Number(decimal value, decimal minimum, decimal maximum, int places)
        {
            return new NumericUpDown { Minimum = minimum, Maximum = maximum, DecimalPlaces = places,
                Value = value, Increment = places == 0 ? 1 : 0.01m };
        }
        private void Changed(object sender, EventArgs e)
        {
            if (layout != null && !loadingLayout)
            {
                layout.OriginX = (double)originX.Value; layout.OriginY = (double)originY.Value;
                layout.RetreatX = (double)retreatX.Value; layout.RetreatY = (double)retreatY.Value; layout.Retreat = retreat.Checked;
            }
            prepared = null;
            preview.Paths = null;
            preview.Invalidate();
            serialDisplay.Text = prefix.Text + ((long)next.Value).ToString("D" + (int)digits.Value) + suffix.Text;
            RefreshButtons();
        }
        private void UpdateSource()
        {
            foreach (Control c in svgControls) c.Visible = source.SelectedIndex == 0;
            foreach (Control c in serialControls) c.Visible = source.SelectedIndex == 1;
            foreach (Control c in qrControls) c.Visible = source.SelectedIndex == 2;
            if (layout != null)
            {
                foreach (Control row in settings.Controls) row.Visible = false;
                foreach (Control c in new Control[] { originX, originY, retreat, retreatX, retreatY }) c.Parent.Visible = true;
                if (layout.UsesSerial) foreach (Control c in new Control[] { prefix, next, increment, digits, suffix, serialDisplay }) c.Parent.Visible = true;
            }
            Changed(this, EventArgs.Empty);
            settings.AutoScrollPosition = Point.Empty;
        }
        private void RefreshButtons()
        {
            bool active = busy || running || core.InProgram;
            foreach (Control c in inputs) c.Enabled = !active;
            next.Enabled = increment.Enabled = prefix.Enabled = suffix.Enabled = digits.Enabled = !active && !journal.Pending;
            generate.Enabled = !active && !(source.SelectedIndex == 1 && (journal.Pending || serialFault));
            load.Enabled = !active && prepared != null && !prepared.Serial && core.CanLoadNewFile;
            export.Enabled = !active && prepared != null;
            run.Enabled = !active && prepared != null && core.IsConnected && core.MachineStatus == GrblCore.MacStatus.Idle && core.QueueEmpty && !(prepared.Serial && (journal.Pending || serialFault));
            abort.Enabled = core.CanAbortProgram;
            retreatX.Enabled = retreatY.Enabled = !active && retreat.Checked;
        }
        private void OpenSvg(object sender, EventArgs e)
        {
            if (busy || running) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "SVG|*.svg" })
                if (dialog.ShowDialog(this) == DialogResult.OK) svgFile.Text = dialog.FileName;
        }

        private void Generate(object sender, EventArgs e)
        {
            LayoutDocument job = layout == null ? null : LayoutDocument.Deserialize(LayoutDocument.Serialize(layout));
            int kind = source.SelectedIndex;
            string filename = svgFile.Text, content = qrContent.Text, family = font.Text;
            string serialText = prefix.Text + ((long)next.Value).ToString("D" + (int)digits.Value) + suffix.Text;
            MarkingMode selectedMode = (MarkingMode)mode.SelectedIndex;
            bool includeQr = withQr.Checked;
            double w = (double)width.Value, step = (double)spacing.Value, wavePitch = (double)pitch.Value;
            double height = (double)textHeight.Value, size = (double)qrSize.Value, separation = (double)gap.Value;
            double feed = (double)speed.Value, sPower = (double)power.Value;
            string laserMode = laser.Text;
            Generated generated = new Generated { Serial = kind == 1, Text = kind == 1 ? serialText : content,
                X = (double)originX.Value, Y = (double)originY.Value,
                RetreatX = retreat.Checked ? (double)retreatX.Value : 0, RetreatY = retreat.Checked ? (double)retreatY.Value : 0 };
            busy = true;
            prepared = null;
            status.Text = "軌道を生成中...";
            RefreshButtons();
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += (s, args) =>
            {
                generated.Calibration = DimensionCalibration.Open(DimensionCalibration.DefaultPath);
                if (job != null)
                {
                    List<MarkingOperation> operations = job.Operations(serialText);
                    foreach (MarkingOperation operation in operations)
                    {
                        if (GrblCore.Configuration.MaxPWM > 0 && operation.Power > (double)GrblCore.Configuration.MaxPWM)
                            throw new ArgumentException("オブジェクトの出力Sが機械設定の最大PWMを超えています。");
                        if (operation.LaserMode == "M4" && core.IsConnected && !GrblCore.Configuration.LaserMode)
                            throw new ArgumentException("M4にはコントローラーのレーザーモードが必要です。");
                    }
                    generated.Paths = operations.SelectMany(o => o.Paths).ToList();
                    generated.Code = MarkingGeometry.GCode(operations, generated.X, generated.Y, generated.RetreatX, generated.RetreatY, generated.Calibration.X, generated.Calibration.Y);
                    args.Result = generated; return;
                }
                if (GrblCore.Configuration.MaxPWM > 0 && sPower > (double)GrblCore.Configuration.MaxPWM)
                    throw new ArgumentException("出力Sが機械設定の最大PWMを超えています。");
                if (laserMode == "M4" && core.IsConnected && !GrblCore.Configuration.LaserMode)
                    throw new ArgumentException("M4にはコントローラーのレーザーモードが必要です。");
                List<MarkingPath> paths;
                if (kind == 0)
                {
                    if (!File.Exists(filename)) throw new ArgumentException("SVGファイルを選択してください。");
                    XDocument document;
                    using (XmlReader reader = XmlReader.Create(filename, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        document = XDocument.Load(reader);
                    string[] unsupported = { "text", "image", "use", "clipPath", "mask", "filter", "symbol" };
                    if (document.Descendants().Any(element => unsupported.Contains(element.Name.LocalName)))
                        throw new ArgumentException("文字はパス化し、クリップ・画像・参照要素は展開して保存してください。");
                    SvgConverter.GCodeFromSVG converter = new SvgConverter.GCodeFromSVG { CaptureGeometry = true, UseLegacyBezier = false };
                    converter.convertFromText(document.ToString(), core);
                    paths = MarkingGeometry.Widen(converter.Geometry, selectedMode, w, step, wavePitch);
                }
                else if (kind == 1)
                {
                    List<MarkingPath> textPaths = MarkingGeometry.Text(serialText, family, height);
                    paths = MarkingGeometry.Fill(textPaths, step);
                    if (includeQr)
                    {
                        List<MarkingPath> qr = MarkingGeometry.QrFill(serialText, size, step);
                        double right = textPaths.SelectMany(p => p.Points).Max(p => p.X);
                        paths.AddRange(MarkingGeometry.Shift(qr, right + separation, 0));
                    }
                }
                else paths = MarkingGeometry.QrFill(content, size, step);
                generated.Paths = paths;
                generated.Code = MarkingGeometry.GCode(new List<MarkingOperation> { new MarkingOperation { Paths=paths, Speed=feed, Power=sPower, LaserMode=laserMode } }, generated.X, generated.Y, generated.RetreatX, generated.RetreatY, generated.Calibration.X, generated.Calibration.Y);
                args.Result = generated;
            };
            worker.RunWorkerCompleted += (s, args) =>
            {
                busy = false;
                if (args.Error != null) status.Text = args.Error.Message;
                else
                {
                    prepared = (Generated)args.Result;
                    preview.Paths = prepared.Paths;
                    preview.RetreatX = prepared.RetreatX;
                    preview.RetreatY = prepared.RetreatY;
                    preview.Invalidate();
                    settings.AutoScrollPosition = Point.Empty;
                    status.Text = "生成済み：" + prepared.Paths.Count + "軌道" + (prepared.Serial ? " ／ " + prepared.Text : "") +
                        "\r\n配置 X=" + generated.X + " Y=" + generated.Y + " mm。退避を含む範囲を確認してください。" +
                        "\r\n寸法補正 X=" + prepared.Calibration.X.ToString("0.########") + " Y=" + prepared.Calibration.Y.ToString("0.########") + "（表示は設計寸法）";
                }
                RefreshButtons(); worker.Dispose();
            };
            worker.RunWorkerAsync();
        }

        private void LoadPrepared(object sender, EventArgs e)
        {
            if (prepared == null || prepared.Serial || !core.CanLoadNewFile) return;
            serialLoaded = false;
            core.LoadedFile.LoadMarkingCode(prepared.Code);
            DialogResult = DialogResult.OK;
            Close();
        }
        private void RunPrepared(object sender, EventArgs e)
        {
            if (prepared == null || !run.Enabled) return;
            try
            {
                core.LoadedFile.LoadMarkingCode(prepared.Code);
                serialLoaded = prepared.Serial;
                core.LoopCount = 1;
                if (prepared.Serial)
                {
                    journal.Next = (long)next.Value;
                    journal.Increment = (long)increment.Value;
                    journal.Digits = (int)digits.Value;
                    journal.Prefix = prefix.Text; journal.Suffix = suffix.Text;
                    journal.Begin(); // Persist before any command can reach the controller.
                    serialLoaded = true;
                    loadedSerialCode = prepared.Code;
                }
                running = true;
                started = false;
                status.Text = "加工中：" + (prepared.Serial ? prepared.Text : "マーク") + "。退避後のIdleを確認しています。";
                core.RunProgram(this);
                if (!started)
                {
                    running = false;
                    if (prepared.Serial) journal.Resolve(false);
                    status.Text = "加工は開始されませんでした。番号は進めていません。";
                }
            }
            catch (Exception ex) { running = false; if (prepared != null && prepared.Serial) serialFault = true; status.Text = ex.Message; }
            RefreshButtons();
        }
        private void ProgramStarted() { if (running) started = true; }
        private void Completed()
        {
            Dispatch(() =>
            {
                if (!running) return;
                running = false;
                try
                {
                    if (prepared != null && prepared.Serial)
                    {
                        journal.Resolve(true);
                        next.Value = journal.Next;
                        prepared = null;
                    }
                    status.Text = "マークと退避が正常完了しました。材料を交換して、次の軌道を生成してください。";
                }
                catch (Exception ex) { serialFault = true; status.Text = "連番の保存に失敗しました。実物と記録を確認してください：" + ex.Message; prepared = null; }
                RefreshButtons();
            });
        }
        private void Issue(GrblCore.DetectedIssue issue)
        {
            Dispatch(() => { running = false; status.Text = "加工が中断されました。番号は未確定のまま保持します：" + issue; RefreshButtons(); });
        }
        private void Failed()
        {
            Dispatch(() => { if (!running) return; running = false; prepared = null;
                status.Text = "正常完了を確認できませんでした。連番は未確定として保持します。画面を開き直して実物を確認してください。"; RefreshButtons(); });
        }
        private void Dispatch(Action action)
        {
            if (disposing || IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action); else action();
        }
        private void ResolveRecovery(bool completed, Control recovery)
        {
            if (running || busy || core.InProgram) return;
            string message = completed ? "実物の打刻を確認済みとして、次の番号に進めますか？" : "同じ番号を再び加工できる状態に戻しますか？";
            if (MessageBox.Show(this, message, "未確定の連番", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { journal.Resolve(completed); next.Value = journal.Next; recovery.Visible = false; status.Text = "前回の結果を記録しました。"; RefreshButtons(); }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        private void ExportPrepared(object sender, EventArgs e)
        {
            if (prepared == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "G-code|*.nc", FileName = prepared.Serial ? "serial-" + next.Value + ".nc" : "marking.nc" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, prepared.Code, new System.Text.UTF8Encoding(false));
                    status.Text = prepared.Serial ? "保存しました。外部で実行した加工は連番管理に反映されません。" : "保存しました。";
                }
        }
        private void Closing(object sender, FormClosingEventArgs e)
        {
            if (busy || core.InProgram || !core.QueueEmpty || core.MachineStatus == GrblCore.MacStatus.Run || core.MachineStatus == GrblCore.MacStatus.Hold)
            { e.Cancel = true; status.Text = "動作中です。完了を待つか、中断して停止を確認してください。"; return; }
            if (running && journal.Pending)
                if (MessageBox.Show(this, "加工結果が未確定です。番号を保持して画面を閉じますか？", "連番", MessageBoxButtons.OKCancel) != DialogResult.OK)
                { e.Cancel = true; return; }
            // A serial job may only be run through this session, where its number is journalled.
            if (serialLoaded && loadedSerialCode != null && core.CanLoadNewFile) core.LoadedFile.LoadMarkingCode("");
            disposing = true;
            timer.Stop(); timer.Dispose();
            core.ProgramStarted -= ProgramStarted;
            core.PhysicalJobCompleted -= Completed;
            core.PhysicalJobFailed -= Failed;
            core.IssueDetected -= Issue;
        }
    }

    internal sealed class MarkingPreview : Panel
    {
        public List<MarkingPath> Paths;
        public double RetreatX, RetreatY;
        public MarkingPreview() { Dock = DockStyle.Fill; BackColor = Color.White; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Paths == null || Paths.Count == 0) return;
            List<PointF> all = Paths.SelectMany(p => p.Points).ToList();
            MarkingPath lastPath = Paths[Paths.Count - 1];
            PointF last = lastPath.Closed ? lastPath.Points[0] : lastPath.Points[lastPath.Points.Count - 1];
            PointF end = new PointF((float)(last.X + RetreatX), (float)(last.Y + RetreatY));
            all.Add(end);
            float minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
            float scale = Math.Min((ClientSize.Width - 40) / Math.Max(0.1f, maxX - minX), (ClientSize.Height - 50) / Math.Max(0.1f, maxY - minY));
            float left = (ClientSize.Width - (maxX - minX) * scale) / 2;
            float top = (ClientSize.Height - (maxY - minY) * scale) / 2;
            Func<PointF, PointF> map = p => new PointF(left + (p.X - minX) * scale, top + (maxY - p.Y) * scale);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.RoyalBlue, 1))
                foreach (MarkingPath path in Paths)
                {
                    if (path.Points.Count < 2) continue;
                    PointF[] line = path.Points.Select(map).ToArray();
                    e.Graphics.DrawLines(pen, line);
                    if (path.Closed) e.Graphics.DrawLine(pen, line[line.Length - 1], line[0]);
                }
            if (RetreatX != 0 || RetreatY != 0)
                using (Pen pen = new Pen(Color.Red, 2) { DashStyle = DashStyle.Dash, EndCap = LineCap.ArrowAnchor })
                    e.Graphics.DrawLine(pen, map(last), map(end));
            e.Graphics.DrawString("範囲 " + (maxX - minX).ToString("0.###") + " × " + (maxY - minY).ToString("0.###") + " mm（退避込み）", Font, Brushes.Black, 8, 8);
        }
    }
}
