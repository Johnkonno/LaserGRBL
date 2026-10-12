// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace LaserGRBL.Marking
{
    internal sealed class MarkingFontEntry
    {
        public string Name, EnglishName, JapaneseName;
        public bool JapaneseSample;
        public string DisplayName { get { return Name + (JapaneseName != Name && JapaneseName != EnglishName ? " ／ " + JapaneseName : ""); } }
        public override string ToString() { return DisplayName; }
        public bool Matches(string query)
        {
            string names = Normalize(Name + "|" + EnglishName + "|" + JapaneseName);
            return (query ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries).All(word => names.Contains(Normalize(word)));
        }
        private static string Normalize(string text)
        {
            return new string(text.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
        }
    }

    internal static class MarkingFontCatalog
    {
        private static List<MarkingFontEntry> cache;
        public static IList<MarkingFontEntry> Installed
        {
            get
            {
                if (cache == null)
                {
                    var entries = new List<MarkingFontEntry>();
                    using (InstalledFontCollection collection = new InstalledFontCollection())
                    {
                        foreach (FontFamily family in collection.Families)
                        {
                            using (family)
                            {
                                try
                                {
                                    if (!family.IsStyleAvailable(FontStyle.Regular) || family.Name.StartsWith("@")) continue;
                                    entries.Add(new MarkingFontEntry { Name = family.GetName(1033), EnglishName = family.GetName(1033),
                                        JapaneseName = family.GetName(1041), JapaneseSample = MissingCharacters(family.Name, "あいうアイウ日本語履帯").Length == 0 });
                                }
                                catch (ArgumentException) { } // Ignore an installed family that GDI+ cannot use for outlines.
                                catch (ExternalException) { }
                            }
                        }
                    }
                    cache = entries.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                return cache.AsReadOnly();
            }
        }
        public static string MissingCharacters(string name, string text)
        {
            string sample = new string((text ?? "").Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).Distinct().ToArray());
            if (sample.Length == 0) return "";
            using (Font font = new Font(name, 12, FontStyle.Regular, GraphicsUnit.Point))
            {
                IntPtr handle = font.ToHfont(), dc = CreateCompatibleDC(IntPtr.Zero), prior = IntPtr.Zero;
                try
                {
                    if (dc == IntPtr.Zero) return sample;
                    prior = SelectObject(dc, handle);
                    ushort[] glyphs = new ushort[sample.Length];
                    if (GetGlyphIndices(dc, sample, sample.Length, glyphs, 1) == uint.MaxValue) return sample;
                    return new string(sample.Where((c, i) => glyphs[i] == ushort.MaxValue || glyphs[i] == 0).ToArray());
                }
                finally
                {
                    if (prior != IntPtr.Zero && prior != new IntPtr(-1)) SelectObject(dc, prior);
                    if (dc != IntPtr.Zero) DeleteDC(dc);
                    DeleteObject(handle);
                }
            }
        }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW", CharSet = CharSet.Unicode)]
        private static extern uint GetGlyphIndices(IntPtr dc, string text, int count, [Out] ushort[] glyphs, uint flags);
    }

    internal sealed class MarkingFontPicker : Form
    {
        private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox japaneseOnly = new CheckBox { Text = "日本語サンプル対応のみ", AutoSize = true };
        private readonly ListBox fontList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox sample = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, MaxLength = 400 };
        private readonly Label count = new Label { AutoSize = true };
        private readonly Label selected = new Label { AutoSize = true };
        private readonly Label warning = new Label { AutoSize = true, MaximumSize = new Size(490, 0), ForeColor = Color.Firebrick };
        private readonly Button apply = new Button { Text = "このフォントを使う", AutoSize = true, DialogResult = DialogResult.OK };
        private readonly FontOutlinePreview preview = new FontOutlinePreview();
        private readonly IList<MarkingFontEntry> installed;
        private string preferred;
        public string SelectedFontName { get { return fontList.SelectedItem == null ? null : ((MarkingFontEntry)fontList.SelectedItem).Name; } }

        public MarkingFontPicker(string current, string content)
        {
            Text = "フォントを探す";
            Size = new Size(920, 650); MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            installed = MarkingFontCatalog.Installed;
            preferred = current;
            string initial = string.IsNullOrWhiteSpace(content) ? "わくわくメイカー\nIV号戦車 / Pz.IV\nWK-IVT-0001" : content.Substring(0, Math.Min(400, content.Length));
            sample.Text = initial.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            top.Controls.Add(new Label { Text = "フォント名で検索", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            top.Controls.Add(search, 1, 0); top.Controls.Add(japaneseOnly, 2, 0);
            top.Controls.Add(count, 1, 1); top.SetColumnSpan(count, 2); root.Controls.Add(top, 0, 0);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(fontList);
            TableLayoutPanel details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8, 0, 0, 0) };
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); details.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); details.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); details.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            details.Controls.Add(selected, 0, 0); details.Controls.Add(new Label { Text = "プレビュー文字（自由に変更できます）", AutoSize = true }, 0, 1);
            details.Controls.Add(sample, 0, 2); details.Controls.Add(preview, 0, 3); details.Controls.Add(warning, 0, 4);
            split.Panel2.Controls.Add(details); root.Controls.Add(split, 0, 1);
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            Button cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
            footer.Controls.Add(cancel); footer.Controls.Add(apply); root.Controls.Add(footer, 0, 2); Controls.Add(root);
            split.SplitterDistance = 315;
            AcceptButton = apply; CancelButton = cancel;
            search.TextChanged += (s, e) => Filter(); japaneseOnly.CheckedChanged += (s, e) => Filter();
            fontList.SelectedIndexChanged += (s, e) => UpdatePreview(); sample.TextChanged += (s, e) => UpdatePreview();
            fontList.DoubleClick += (s, e) => { if (apply.Enabled) { DialogResult = DialogResult.OK; Close(); } };
            Shown += (s, e) => search.Focus();
            Filter();
        }
        private void Filter()
        {
            string keep = SelectedFontName ?? preferred;
            var choices = installed.Where(f => f.Matches(search.Text) && (!japaneseOnly.Checked || f.JapaneseSample)).ToList();
            fontList.BeginUpdate(); fontList.Items.Clear(); fontList.Items.AddRange(choices.ToArray());
            MarkingFontEntry previous = choices.FirstOrDefault(f => string.Equals(f.Name, keep, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.EnglishName, keep, StringComparison.OrdinalIgnoreCase) || string.Equals(f.JapaneseName, keep, StringComparison.OrdinalIgnoreCase));
            if (previous != null) fontList.SelectedItem = previous;
            else if (choices.Count > 0) fontList.SelectedIndex = 0;
            fontList.EndUpdate();
            count.Text = choices.Count + " / " + installed.Count + " フォント　日本語名・英語名で検索できます";
            UpdatePreview();
        }
        private void UpdatePreview()
        {
            string name = SelectedFontName;
            selected.Text = name ?? "該当するフォントがありません";
            preview.Paths = null; warning.Text = ""; warning.ForeColor = SystemColors.GrayText; apply.Enabled = false;
            if (name != null)
            {
                preferred = name;
                try
                {
                    preview.Paths = MarkingGeometry.Text(sample.Text, name, 10);
                    string missing = MarkingFontCatalog.MissingCharacters(name, sample.Text);
                    if (missing.Length > 0)
                    {
                        warning.ForeColor = Color.Firebrick;
                        warning.Text = "このフォントにない文字：" + missing.Substring(0, Math.Min(35, missing.Length)) + "\n文字の輪郭を確認してください。";
                    }
                    else warning.Text = "加工に使用する文字の輪郭を表示しています。";
                    apply.Enabled = true;
                }
                catch (ArgumentException error) { warning.ForeColor = Color.Firebrick; warning.Text = error.Message; }
                catch (ExternalException) { warning.ForeColor = Color.Firebrick; warning.Text = "このフォントの文字を描画できません。別のフォントを選んでください。"; }
            }
            preview.Invalidate();
        }
    }

    internal sealed class FontOutlinePreview : Panel
    {
        public List<MarkingPath> Paths;
        public FontOutlinePreview() { Dock = DockStyle.Fill; BackColor = Color.White; DoubleBuffered = true; BorderStyle = BorderStyle.FixedSingle; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Paths == null || Paths.Count == 0) return;
            var points = Paths.SelectMany(p => p.Points).ToList();
            float minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            float scale = Math.Min(Math.Max(1, ClientSize.Width - 32) / Math.Max(.01f, maxX - minX), Math.Max(1, ClientSize.Height - 32) / Math.Max(.01f, maxY - minY));
            float left = (ClientSize.Width - (maxX - minX) * scale) / 2, top = (ClientSize.Height - (maxY - minY) * scale) / 2;
            using (GraphicsPath drawing = new GraphicsPath(FillMode.Alternate))
            {
                foreach (MarkingPath path in Paths)
                {
                    PointF[] line = path.Points.Select(p => new PointF(left + (p.X - minX) * scale, top + (maxY - p.Y) * scale)).ToArray();
                    if (line.Length < 2) continue;
                    drawing.StartFigure(); drawing.AddLines(line); if (path.Closed) drawing.CloseFigure();
                }
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(Brushes.Black, drawing);
            }
        }
    }

    internal sealed class MarkingFontEditor : UITypeEditor
    {
        public MarkingFontEditor() { }
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) { return UITypeEditorEditStyle.Modal; }
        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            if (context == null || provider == null) return value;
            var service = provider.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (service == null) return value;
            LayoutItem item = context.Instance as LayoutItem;
            var view = context.Instance as LayoutPropertyView;
            if (view != null) item = view.GetPropertyOwner(context.PropertyDescriptor) as LayoutItem;
            string content = item == null ? "" : item.Content;
            if (content != null && content.Contains("{serial}"))
                content = content.Replace("{serial}", SerialJournal.Open(System.IO.Path.Combine(GrblCore.DataPath, "serial-number.json")).Format());
            using (var dialog = new MarkingFontPicker(value as string, content))
                return service.ShowDialog(dialog) == DialogResult.OK ? dialog.SelectedFontName : value;
        }
    }
}
