// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LaserGRBL.Marking
{
    internal sealed class CalibrationForm : Form
    {
        private readonly GrblCore core;
        private DimensionCalibration profile;
        private readonly NumericUpDown lengthX = Number(100, 10, 1000, 3), lengthY = Number(100, 10, 1000, 3);
        private readonly NumericUpDown minor = Number(1, 0.1m, 100, 3), major = Number(10, 0.1m, 1000, 3);
        private readonly NumericUpDown speed = Number(1000, 1, 100000, 0), power = Number(255, 1, 100000, 0);
        private readonly NumericUpDown actualX = Number(100, 0.001m, 100000, 3), actualY = Number(100, 0.001m, 100000, 3);
        private readonly NumericUpDown factorX = Number(1, 0.1m, 10, 8), factorY = Number(1, 0.1m, 10, 8);
        private readonly CheckBox axisX = new CheckBox { Text = "X定規", Checked = true, AutoSize = true }, axisY = new CheckBox { Text = "Y定規", Checked = true, AutoSize = true };
        private readonly CheckBox applyX = new CheckBox { Text = "Xを補正", AutoSize = true }, applyY = new CheckBox { Text = "Yを補正", AutoSize = true };
        private readonly Label reference = new Label { AutoSize = true, MaximumSize = new Size(415,0) }, result = new Label { AutoSize = true, MaximumSize = new Size(415,0) };
        private readonly FlowLayoutPanel fields = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private readonly MarkingPreview preview = new MarkingPreview();
        private readonly Button generate = new Button { Text = "定規のGコード生成・加工...", AutoSize = true }, apply = new Button { Text = "実測値から補正を保存", AutoSize = true };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8) };
        private bool updating;

        public CalibrationForm(GrblCore core)
        {
            this.core = core; profile = DimensionCalibration.Open(DimensionCalibration.DefaultPath);
            Text = "LaserGRBL Marking — 定規で寸法補正"; Size = new Size(1050,800); MinimumSize = new Size(940,700); StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(fields); split.Panel2.Controls.Add(preview); Controls.Add(split); Controls.Add(status); split.SplitterDistance = 455;
            Header("1. 定規を生成する（単位：mm）"); Row("加工する軸", axisX, axisY);
            Row("X定規の長さ", lengthX); Row("Y定規の長さ", lengthY); Row("目盛り間隔", minor); Row("番号の間隔", major);
            Row("加工速度 mm/min", speed); Row("出力 S", power); Row("", generate);
            Header("2. 両端の目盛りの中心間を測る"); fields.Controls.Add(reference);
            Row("Xの実測長 mm", actualX, applyX); Row("Yの実測長 mm", actualY, applyY);
            fields.Controls.Add(result); Row("", apply);
            Header("3. 補正係数を手動設定／解除する"); Row("X補正係数", factorX); Row("Y補正係数", factorY);
            Button manual = new Button { Text = "係数を保存", AutoSize = true }, reset = new Button { Text = "補正を解除（X・Y＝1）", AutoSize = true };
            Row("", manual, reset);
            fields.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(415,0), Text = "補正はこの改造版のマーキングGコードに適用します。レイアウトは設計寸法のまま表示します。\r\n生成画面で保存したGコードには補正が適用済みです。\r\n使う加工機を変えた時や機械の寸法設定を変更した時は、補正を解除して定規を加工し直してください。" });
            power.Value = Math.Min(power.Maximum, Math.Max(1, GrblCore.Configuration.MaxPWM));
            foreach (NumericUpDown input in new[] { lengthX,lengthY,minor,major,speed,power }) input.ValueChanged += (s,e) => PreviewRuler();
            axisX.CheckedChanged += (s,e) => PreviewRuler(); axisY.CheckedChanged += (s,e) => PreviewRuler();
            foreach (NumericUpDown input in new[] { actualX,actualY }) input.ValueChanged += (s,e) => UpdateResult();
            applyX.CheckedChanged += (s,e) => UpdateResult(); applyY.CheckedChanged += (s,e) => UpdateResult();
            generate.Click += (s,e) => Safely(GenerateRuler); apply.Click += (s,e) => Safely(ApplyMeasurement);
            manual.Click += (s,e) => Safely(() => Persist(p => { p.X=(double)factorX.Value; p.Y=(double)factorY.Value; p.Trial=null; }));
            reset.Click += (s,e) => Safely(() => Persist(p => { p.X=p.Y=1; p.Trial=null; }));
            RefreshProfile(); PreviewRuler();
        }
        private static NumericUpDown Number(decimal value, decimal minimum, decimal maximum, int places)
        { return new NumericUpDown { Minimum=minimum,Maximum=maximum,Value=value,DecimalPlaces=places,Increment=places==0?1:0.001m,Width=110 }; }
        private void Header(string text) { fields.Controls.Add(new Label { Text=text,AutoSize=true,Margin=new Padding(6,12,6,5),Font=new Font(Font,FontStyle.Bold) }); }
        private void Row(string text, params Control[] controls)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize=true,Width=430,WrapContents=false,Margin=new Padding(4,3,4,3) };
            row.Controls.Add(new Label { Text=text,Width=140,Height=26,TextAlign=ContentAlignment.MiddleLeft }); row.Controls.AddRange(controls); fields.Controls.Add(row);
        }
        private void Safely(Action action) { try { action(); } catch(Exception ex) { status.Text=ex.Message; } }
        private LayoutDocument Rulers()
        {
            LayoutDocument document = CalibrationRuler.Create((double)lengthX.Value,(double)lengthY.Value,(double)minor.Value,(double)major.Value,axisX.Checked,axisY.Checked,(double)power.Value);
            foreach (LayoutItem item in document.Items) item.Speed=(double)speed.Value;
            return document;
        }
        private void PreviewRuler()
        {
            try { preview.Paths=Rulers().Items.SelectMany(i=>i.Outlines("")).ToList(); generate.Enabled=true; status.Text="0から末端の目盛りまでの中心間を測定します。生成後に加工軌道と範囲を確認してください。"; }
            catch(Exception ex) { preview.Paths=null; generate.Enabled=false; status.Text=ex.Message; }
            preview.Invalidate();
        }
        private void GenerateRuler()
        {
            LayoutDocument rulers=Rulers();
            using(MarkingForm form=new MarkingForm(core,rulers))
            {
                form.ShowDialog(this); DimensionCalibration used=form.GeneratedCalibration;
                if(used==null) return;
                CalibrationTrial trial=new CalibrationTrial {LengthX=(double)lengthX.Value,LengthY=(double)lengthY.Value,UsedX=used.X,UsedY=used.Y,AxisX=axisX.Checked,AxisY=axisY.Checked,Created=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)};
                Persist(p=>p.Trial=trial);
            }
            status.Text="この定規の生成条件を記録しました。加工した定規を実測して入力してください。";
        }
        private void RefreshProfile()
        {
            updating=true; factorX.Value=(decimal)profile.X; factorY.Value=(decimal)profile.Y;
            CalibrationTrial trial=profile.Trial;
            applyX.Enabled=trial!=null&&trial.AxisX; applyY.Enabled=trial!=null&&trial.AxisY;
            applyX.Checked=applyX.Enabled; applyY.Checked=applyY.Enabled;
            if(trial!=null)
            {
                actualX.Value=(decimal)trial.LengthX; actualY.Value=(decimal)trial.LengthY;
                reference.Text="記録した定規："+(trial.AxisX?"X="+trial.LengthX.ToString("0.###")+" mm  ":"")+(trial.AxisY?"Y="+trial.LengthY.ToString("0.###")+" mm":"")+
                    "\r\n生成時の係数 X="+trial.UsedX.ToString("0.########")+"  Y="+trial.UsedY.ToString("0.########")+"\r\n最新の記録と同じ定規を測定してください。";
            }
            else reference.Text="先に定規のGコードを生成してください。生成条件を記録してから実測できます。";
            updating=false; UpdateResult();
        }
        private void UpdateResult()
        {
            if(updating) return;
            apply.Enabled=false;
            if(profile.Trial==null) { result.Text="現在の係数 X="+profile.X.ToString("0.########")+"  Y="+profile.Y.ToString("0.########"); return; }
            try
            {
                double x=applyX.Checked?DimensionCalibration.Calculate(profile.Trial.UsedX,profile.Trial.LengthX,(double)actualX.Value):profile.X;
                double y=applyY.Checked?DimensionCalibration.Calculate(profile.Trial.UsedY,profile.Trial.LengthY,(double)actualY.Value):profile.Y;
                result.Text="保存する係数 X="+x.ToString("0.########")+"  Y="+y.ToString("0.########"); apply.Enabled=applyX.Checked||applyY.Checked;
            }
            catch(Exception ex) { result.Text=ex.Message; }
        }
        private void ApplyMeasurement()
        {
            if(!apply.Enabled||profile.Trial==null) return;
            double x=applyX.Checked?DimensionCalibration.Calculate(profile.Trial.UsedX,profile.Trial.LengthX,(double)actualX.Value):profile.X;
            double y=applyY.Checked?DimensionCalibration.Calculate(profile.Trial.UsedY,profile.Trial.LengthY,(double)actualY.Value):profile.Y;
            Persist(p=>{p.X=x;p.Y=y;p.Trial=null;});
            status.Text="寸法補正を保存しました。次に生成するGコードに適用します。新しい定規で再測定して確認できます。";
        }
        private void Persist(Action<DimensionCalibration> change)
        {
            DimensionCalibration updated=DimensionCalibration.Open(DimensionCalibration.DefaultPath);
            if(updated.Revision!=profile.Revision) throw new InvalidOperationException("寸法補正の記録が別の画面で更新されました。この画面を開き直してください。");
            change(updated); updated.Save(); profile=updated; RefreshProfile(); status.Text="寸法補正の記録を保存しました。次のGコード生成から反映します。";
        }
    }
}
