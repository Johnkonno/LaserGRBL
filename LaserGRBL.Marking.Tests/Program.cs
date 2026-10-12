// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LaserGRBL;
using LaserGRBL.Marking;

internal static class Program
{
    private static int passed, failed;
    private static string artifacts;
    [STAThread]
    private static int Main(string[] args)
    {
        artifacts = args[0]; Directory.CreateDirectory(artifacts);
        Application.EnableVisualStyles();
        Test("Open line offset has the requested band width", () =>
        {
            var paths = MarkingGeometry.Widen(Line(), MarkingMode.Offset, 2, 0.25, 1);
            var points = paths.SelectMany(p => p.Points).ToList();
            Near(-1, points.Min(p => p.Y)); Near(1, points.Max(p => p.Y));
            Assert(paths.Count > 3, "Multiple lanes missing");
        });
        Test("Closed path widens inward and outward", () =>
        {
            var paths = MarkingGeometry.Widen(new List<MarkingPath> { Box(0, 0, 10, 10) }, MarkingMode.Offset, 2, 0.25, 1);
            var points = paths.SelectMany(p => p.Points).ToList();
            Near(-1, points.Min(p => p.X)); Near(11, points.Max(p => p.X));
            Assert(paths.Any(p => p.Points.All(q => q.X >= 0.9f && q.X <= 9.1f && q.Y >= 0.9f && q.Y <= 9.1f)), "Inset ring missing");
        });
        Test("Zigzag traverses both sides of the centerline", () =>
        {
            var points = MarkingGeometry.Widen(Line(), MarkingMode.Zigzag, 2, 0.25, 1).SelectMany(p => p.Points).ToList();
            Assert(points.Min(p => p.Y) < -0.9 && points.Max(p => p.Y) > 0.9, "Wave width missing");
            Assert(points.All(p => p.Y >= -1.001 && p.Y <= 1.001), "Wave escaped band");
        });
        Test("Zigzag at a sharp corner remains within its stroke band", () =>
        {
            var path = new List<MarkingPath> { new MarkingPath(new[] { new PointF(0,0), new PointF(10,0), new PointF(10,10) }, false) };
            var points = MarkingGeometry.Widen(path, MarkingMode.Zigzag, 2, 0.2, 0.5).SelectMany(p => p.Points).ToList();
            Assert(points.All(p => p.X >= -1.001 && p.X <= 11.001 && p.Y >= -1.001 && p.Y <= 11.001), "Corner escaped bounds");
        });
        Test("Hatching preserves holes without engraving travel across them", () =>
        {
            var paths = MarkingGeometry.Fill(new List<MarkingPath> { Box(0,0,10,10), Box(4,4,6,6) }, 0.25);
            foreach (var p in paths.Where(p => p.Points[0].Y > 4 && p.Points[0].Y < 6))
                Assert(p.Points.Max(q => q.X) <= 4.001 || p.Points.Min(q => q.X) >= 5.999, "Hatch crossed hole");
        });
        Test("Text is flattened to the specified physical height", () =>
        {
            var points = MarkingGeometry.Text("SN-000001", "Arial", 5).SelectMany(p => p.Points).ToList();
            Near(0, points.Min(p => p.Y)); Near(5, points.Max(p => p.Y));
        });
        Test("QR has quiet space and generates filled toolpaths", () =>
        {
            string value = "SN-000001";
            var modules = MarkingGeometry.Qr(value, 15);
            double cell = modules[0].Points[1].X - modules[0].Points[0].X;
            Assert(modules.SelectMany(p => p.Points).Min(p => p.X) >= cell * 3.99, "Quiet zone missing");
            RenderQr(value, "qr-000001.png"); RenderQr("仕事館-SN-000002", "qr-unicode.png");
            Assert(MarkingGeometry.QrFill(value, 15, 0.05).Count > 200, "QR fill missing");
        });
        Test("QR rejects undersampling", () => Throws<ArgumentException>(() => MarkingGeometry.QrFill("SN-000001", 5, 1)));
        Test("Travel and retreat occur with the laser off, and distance mode is restored", () =>
        {
            string code = MarkingGeometry.GCode(Line(), 500, 100, "M3", 1.25, 2.5, 3, 7);
            bool laserOn = false;
            foreach (var line in code.Split(new[] { '\r','\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("M5")) laserOn = false;
                if (line.StartsWith("G1 ")) laserOn = true;
                if (line.StartsWith("G0 ")) Assert(!laserOn, "Laser on during rapid travel");
            }
            Assert(code.EndsWith("G91\r\nG0 X3 Y7\r\nG90\r\nG4 P0\r\n"), "Retreat/mode restore missing");
            File.WriteAllText(Path.Combine(artifacts,"retreat.nc"), code);
        });
        Test("G-code uses decimal points under a decimal-comma locale", () =>
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try { Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert(MarkingGeometry.GCode(Line(), 500, 100, "M4", 1.25, 2.5, 0, 0).Contains("X1.25 Y2.5"), "Locale leak"); }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        });
        Test("Serial reservation survives restart and increments once", () =>
        {
            var filename = JournalFile(); var j = SerialJournal.Open(filename);
            j.Prefix="SN-"; j.Increment=2; j.Begin();
            j = SerialJournal.Open(filename); Assert(j.Pending && j.PendingText=="SN-000001", "Reservation lost");
            Throws<InvalidOperationException>(() => j.Begin()); j.Resolve(true);
            j = SerialJournal.Open(filename); Assert(j.Next==3 && !j.Pending, "Increment incorrect");
            Throws<InvalidOperationException>(() => j.Resolve(true));
        });
        Test("Aborted serial retains the number", () =>
        {
            var filename=JournalFile(); var j=SerialJournal.Open(filename); j.Next=42; j.Begin(); j.Resolve(false);
            Assert(SerialJournal.Open(filename).Next==42, "Aborted job consumed number");
        });
        Test("Serial overflow is rejected before reservation", () =>
        {
            var j=SerialJournal.Open(JournalFile()); j.Next=long.MaxValue; Throws<OverflowException>(()=>j.Begin()); Assert(!j.Pending,"Overflow reserved number");
        });
        Test("A stale second session cannot reserve the same serial number", () =>
        {
            string file=JournalFile(); var first=SerialJournal.Open(file); var second=SerialJournal.Open(file);
            first.Begin(); Throws<IOException>(()=>second.Begin());
            Assert(SerialJournal.Open(file).Pending,"First reservation was overwritten");
        });
        using (Control sync = new Control())
        {
            var handle = sync.Handle;
            var core = new TestCore(sync);
            Test("SVG captures millimeters, subpaths, transforms, and circular arcs", () =>
            {
                var c = new LaserGRBL.SvgConverter.GCodeFromSVG { CaptureGeometry=true, UseLegacyBezier=false };
                c.convertFromText("<svg xmlns='http://www.w3.org/2000/svg' width='20mm' height='20mm' viewBox='0 0 20 20'><g transform='translate(2 3)'><path d='M0 0 L5 0 M0 2 L5 2'/><circle cx='6' cy='6' r='2'/></g></svg>", core);
                Assert(c.Geometry.Count==3,"Subpaths lost: "+c.Geometry.Count);
                Assert(c.Geometry.Any(p=>p.Points.Count>20),"Arc not flattened");
                Near(2,c.Geometry.SelectMany(p=>p.Points).Min(p=>p.X));
                Near(17,c.Geometry.SelectMany(p=>p.Points).Max(p=>p.Y));
            });
            Test("Malformed and Run status cannot complete a job; fresh Idle completes once", () =>
            {
                int completed=0; core.PhysicalJobCompleted+=()=>completed++;
                core.Arm(); core.Feed("<Run|MPos:0,0,0>"); Assert(completed==0,"Run counted as complete");
                core.Feed("<Idle|MPos:bad,0,0>"); Assert(completed==0,"Malformed report counted as complete");
                core.Feed("<Idle|MPos:0,0,0>"); Assert(completed==1,"Fresh Idle missing");
                core.Feed("<Idle|MPos:0,0,0>"); Assert(completed==1,"Duplicate completion");
            });
            Test("Pending commands prevent completion and a rejected command cancels it", () =>
            {
                int complete=0, fail=0; core.PhysicalJobCompleted+=()=>complete++; core.PhysicalJobFailed+=()=>fail++;
                core.Arm(); core.AddPending(new GrblCommand("G0 X1")); core.Feed("<Idle|MPos:0,0,0>");
                Assert(complete==0,"Completed with pending command"); core.Reply("error:1"); core.Feed("<Idle|MPos:0,0,0>");
                Assert(complete==0 && fail==1,"Command error not retained");
                core.Arm(); core.Fail(GrblCore.DetectedIssue.ManualAbort); core.Feed("<Idle|MPos:0,0,0>");
                Assert(complete==0 && fail==2,"Abort counted as completion");
            });
            Test("Marking data and exported files bypass return-to-origin footer", () =>
            {
                string code=MarkingGeometry.GCode(Line(),500,100,"M3",0,0,0,5);
                core.LoadedFile.LoadMarkingCode(code); core.Footer(); Assert(core.QueueEmpty,"Footer injected an origin move");
                string file=Path.Combine(artifacts,"saved-marking.nc"); core.LoadedFile.SaveGCODE(file,true,true,true,1,false,core);
                string saved=File.ReadAllText(file); Assert(saved.StartsWith("(LaserGRBL Marking)"),"Marker lost");
                Assert(!saved.Contains("Z0"),"Origin footer exported");
            });
            Test("An Alarm status cancels completion even without a separate alarm message", () =>
            {
                int complete=0; core.PhysicalJobCompleted+=()=>complete++;
                core.Arm(); core.Feed("<Alarm|MPos:0,0,0>"); core.Feed("<Idle|MPos:0,0,0>");
                Assert(complete==0,"Alarm was counted as success");
            });
            Test("Serial and QR dialog builds and renders without a connected machine", () =>
            {
                using (var form=new MarkingForm(core))
                {
                    form.Location=new Point(-2000,-2000); form.StartPosition=FormStartPosition.Manual; form.ShowInTaskbar=false;
                    form.Show();
                    ((ComboBox)Field(form,"source")).SelectedIndex=1;
                    ((TextBox)Field(form,"prefix")).Text="SN-";
                    ((CheckBox)Field(form,"withQr")).Checked=true;
                    ((CheckBox)Field(form,"retreat")).Checked=true;
                    ((NumericUpDown)Field(form,"retreatY")).Value=5;
                    Method(form,"Generate",null,EventArgs.Empty);
                    var deadline=DateTime.UtcNow.AddSeconds(30);
                    while ((bool)Field(form,"busy") && DateTime.UtcNow<deadline) { Application.DoEvents(); Thread.Sleep(10); }
                    Assert(!(bool)Field(form,"busy"),"Generation timed out");
                    Assert(Field(form,"prepared")!=null,((Label)Field(form,"status")).Text);
                    using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(Path.Combine(artifacts,"marking-dialog.png")); }
                    var journal=(SerialJournal)Field(form,"journal"); long number=journal.Next;
                    journal.Begin(); typeof(MarkingForm).GetField("running",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,true);
                    core.Arm(); core.Feed("<Idle|MPos:0,0,0>"); Application.DoEvents();
                    Assert(journal.Next==number+1 && !journal.Pending,"Completion did not advance the UI journal exactly once");
                    core.Feed("<Idle|MPos:0,0,0>"); Application.DoEvents(); Assert(journal.Next==number+1,"UI duplicated increment");
                    form.Close();
                }
            });
            Test("Layout scales, rotates, and translates SVG geometry in millimeters", () =>
            {
                string svg=Path.Combine(artifacts,"layout-shape.svg");
                File.WriteAllText(svg,"<svg xmlns='http://www.w3.org/2000/svg' width='20mm' height='10mm' viewBox='0 0 20 10'><path d='M0 0 L10 0 L10 5 L0 5 Z'/></svg>");
                var item=LayoutItem.ImportSvg(svg,core); item.Scale=2; item.Rotation=90; item.X=30; item.Y=40;
                var b=item.Bounds("000001"); Near(20,b.Left); Near(30,b.Right); Near(40,b.Top); Near(60,b.Bottom);
                var document=new LayoutDocument { Items=new List<LayoutItem>{item}, OriginX=7,OriginY=8,Retreat=true,RetreatX=3,RetreatY=4 };
                File.Delete(svg);
                var restored=LayoutDocument.Deserialize(LayoutDocument.Serialize(document));
                Near(20,restored.Items[0].Bounds("000001").Left); Near(7,restored.OriginX); Near(4,restored.RetreatY);
                Assert(restored.Operations("000001")[0].Paths.Count==1,"Embedded SVG did not survive without the original file");
                File.WriteAllText(Path.Combine(artifacts,"sample.lgrbl-layout"),LayoutDocument.Serialize(document));
            });
            Test("Layout preserves per-object feed and power, order, and a single final retreat", () =>
            {
                var a=new LayoutItem {Kind=LayoutKind.Text,Content="A",Mode=LayoutMode.Trace,Speed=700,Power=80};
                var b=new LayoutItem {Kind=LayoutKind.Text,Content="B",X=30,Mode=LayoutMode.Trace,Speed=900,Power=100};
                var document=new LayoutDocument {Items=new List<LayoutItem>{a,b}};
                var operations=document.Operations("000001");
                string code=MarkingGeometry.GCode(operations,2,3,4,5);
                Assert(code.IndexOf("F700 S80")<code.IndexOf("F900 S100"),"Machining order/settings lost");
                Assert(code.Split(new[]{"G91"},StringSplitOptions.None).Length==2,"Retreat repeated per object");
                Assert(code.EndsWith("G91\r\nG0 X4 Y5\r\nG90\r\nG4 P0\r\n"),"Final retreat missing");
            });
            Test("Serial bindings share one value and disabled objects do not participate", () =>
            {
                var text=new LayoutItem {Kind=LayoutKind.Text,Content="ID:{serial}",Mode=LayoutMode.Fill};
                var qr=new LayoutItem {Kind=LayoutKind.QR,Content="ID:{serial}",Size=15,Mode=LayoutMode.Fill,X=40};
                var document=new LayoutDocument {Items=new List<LayoutItem>{text,qr}};
                Assert(document.UsesSerial && document.Operations("SN-000123").Count==2,"Bindings missing");
                Near(text.Bounds("SN-000123").Width, new LayoutItem {Kind=LayoutKind.Text,Content="ID:SN-000123"}.Bounds("").Width);
                Assert(qr.Outlines("SN-000123").Count==new LayoutItem {Kind=LayoutKind.QR,Content="ID:SN-000123",Size=15}.Outlines("").Count,"QR binding mismatch");
                text.Enabled=qr.Enabled=false; Assert(!document.UsesSerial,"Disabled binding used"); Throws<ArgumentException>(()=>document.Operations("000001"));
            });
            Test("Layout rejects unsupported versions, nonfinite positions, and empty fill", () =>
            {
                Throws<ArgumentException>(()=>LayoutDocument.Deserialize("{\"Version\":99,\"Items\":[]}"));
                var item=new LayoutItem {Kind=LayoutKind.Text,X=double.NaN}; Throws<ArgumentException>(()=>item.Validate());
                item.X=0; item.Size=0; Throws<ArgumentException>(()=>item.Validate());
                var open=new LayoutItem {Kind=LayoutKind.SVG,Mode=LayoutMode.Fill,Geometry=new List<LayoutPath>{new LayoutPath{Points=new List<LayoutPoint>{new LayoutPoint{X=0,Y=0},new LayoutPoint{X=10,Y=0}}}}};
                Throws<ArgumentException>(()=>open.Operation("000001"));
            });
            Test("Layout canvas renders, drags objects, and restores edits with undo", () =>
            {
                var document=new LayoutDocument {Items=new List<LayoutItem>{
                    new LayoutItem{Kind=LayoutKind.Text,Name="品番",Content="PART-A",Mode=LayoutMode.Fill,Y=20},
                    new LayoutItem{Kind=LayoutKind.Text,Name="連番",Content="{serial}",Mode=LayoutMode.Fill},
                    new LayoutItem{Kind=LayoutKind.QR,Name="連番QR",Content="{serial}",Size=15,X=30,Mode=LayoutMode.Fill}}};
                document.Retreat=true; document.RetreatY=5;
                File.WriteAllText(Path.Combine(artifacts,"serial-template.lgrbl-layout"),LayoutDocument.Serialize(document));
                using(var form=new LayoutForm(core))
                {
                    typeof(LayoutForm).GetField("document",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,document);
                    typeof(LayoutForm).GetField("saved",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,LayoutDocument.Serialize(document));
                    Method(form,"RefreshScene",0);
                    form.Location=new Point(-2000,-2000);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();Application.DoEvents();
                    var canvas=(LayoutCanvas)Field(form,"canvas");canvas.Fit();
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(artifacts,"layout-editor.png"));}
                    var bounds=document.Items[0].Bounds(canvas.Serial);
                    var point=(PointF)canvas.GetType().GetMethod("Map",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{new PointF(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2)});
                    float zoom=(float)Field(canvas,"zoom"); int x=(int)point.X,y=(int)point.Y;
                    Method(canvas,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,x,y,0));
                    Method(canvas,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,x+20,y-10,0));
                    Method(canvas,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,x+20,y-10,0));
                    Near(20/zoom,document.Items[0].X); Near(20+10/zoom,document.Items[0].Y);
                    Method(form,"Undo",false); var restored=(LayoutDocument)Field(form,"document"); Near(0,restored.Items[0].X);Near(20,restored.Items[0].Y);
                    form.Close();
                }
            });
            Test("Whole-layout generation retains saved retreat and advances serial once", () =>
            {
                var document=new LayoutDocument {Items=new List<LayoutItem>{
                    new LayoutItem{Kind=LayoutKind.Text,Content="{serial}",Mode=LayoutMode.Fill,Power=80},
                    new LayoutItem{Kind=LayoutKind.QR,Content="{serial}",Size=15,X=30,Mode=LayoutMode.Fill,Power=80}},OriginX=4,OriginY=7,Retreat=true,RetreatX=3,RetreatY=5};
                using(var form=new MarkingForm(core,document))
                {
                    Near(7,(double)((NumericUpDown)Field(form,"originY")).Value); Near(5,(double)((NumericUpDown)Field(form,"retreatY")).Value);
                    Assert(((CheckBox)Field(form,"retreat")).Checked,"Saved retreat disabled");
                    form.Location=new Point(-2000,-2000);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();
                    Method(form,"Generate",null,EventArgs.Empty);var deadline=DateTime.UtcNow.AddSeconds(30);
                    while((bool)Field(form,"busy")&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                    object prepared=Field(form,"prepared");Assert(prepared!=null,((Label)Field(form,"status")).Text);
                    Assert((bool)prepared.GetType().GetField("Serial").GetValue(prepared),"Layout serial flag missing");
                    string code=(string)prepared.GetType().GetField("Code").GetValue(prepared);Assert(code.EndsWith("G91\r\nG0 X3 Y5\r\nG90\r\nG4 P0\r\n"),"Saved retreat lost");
                    File.WriteAllText(Path.Combine(artifacts,"layout-generated.nc"),code);
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(artifacts,"layout-generated.png"));}
                    var journal=(SerialJournal)Field(form,"journal");long number=journal.Next;journal.Begin();typeof(MarkingForm).GetField("running",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,true);
                    core.Arm();core.Feed("<Idle|MPos:0,0,0>");Application.DoEvents();Assert(journal.Next==number+1&&!journal.Pending,"Whole layout incremented incorrectly");
                    core.Feed("<Idle|MPos:0,0,0>");Application.DoEvents();Assert(journal.Next==number+1,"Duplicate completion incremented");form.Close();
                }
            });
            Test("Dimension correction uses the generated ruler factor for repeat calibration", () =>
            {
                double first=DimensionCalibration.Calculate(1,100,98);Assert(Math.Abs(first-100.0/98)<1e-12,"Initial ratio wrong");
                double second=DimensionCalibration.Calculate(first,100,99.9);Assert(Math.Abs(second-first*100/99.9)<1e-12,"Prior correction discarded");
                Assert(Math.Abs(DimensionCalibration.Calculate(first,100,100)-first)<1e-12,"Already correct ruler changed the factor");
                Throws<ArgumentException>(()=>DimensionCalibration.Calculate(1,100,0));Throws<ArgumentException>(()=>DimensionCalibration.Calculate(1,100,double.NaN));
                Throws<ArgumentException>(()=>DimensionCalibration.Calculate(1,100,0.01));
            });
            Test("Calibration rulers have exact endpoints and preserve a noninteger final tick", () =>
            {
                var rulers=CalibrationRuler.Create(100.5,80,1,10,true,true,80);Assert(rulers.Items.Count==2,"Axis missing");
                var x=rulers.Items[0].Geometry;Near(100.5,x[0].Points[1].X-x[0].Points[0].X);
                var y=rulers.Items[1].Geometry;Near(80,y[0].Points[1].Y-y[0].Points[0].Y);
                Assert(x.Any(p=>!p.Closed&&p.Points.Count==2&&Math.Abs(p.Points[0].X-100.5)<0.0001&&Math.Abs(p.Points[1].Y-4)<0.0001),"Final numbered tick missing");
                Assert(CalibrationRuler.Create(100,100,1,10,false,true,80).Items.Count==1,"Unused X axis generated");
                File.WriteAllText(Path.Combine(artifacts,"calibration-rulers.lgrbl-layout"),LayoutDocument.Serialize(rulers));
                Throws<ArgumentException>(()=>CalibrationRuler.Create(1000,100,0.1,10,true,false,80));
                Throws<ArgumentException>(()=>CalibrationRuler.Create(100,100,1,1.5,true,false,80));
                Throws<ArgumentException>(()=>CalibrationRuler.Create(100,100,1,10,false,false,80));
            });
            Test("Calibration profile persists trial references and rejects stale writers", () =>
            {
                string path=Path.Combine(artifacts,"calibration-"+Guid.NewGuid()+".json");var first=DimensionCalibration.Open(path);var stale=DimensionCalibration.Open(path);
                first.X=1.02;first.Y=0.99;first.Trial=new CalibrationTrial{LengthX=100,LengthY=80,UsedX=1,UsedY=1,AxisX=true,AxisY=true};first.Save();
                var saved=DimensionCalibration.Open(path);Assert(Math.Abs(saved.X-1.02)<1e-12&&saved.Trial.LengthY==80&&saved.Trial.UsedX==1,"Profile/trial changed");
                Throws<IOException>(()=>stale.Save());
                string corrupt=Path.Combine(artifacts,"bad-calibration-"+Guid.NewGuid()+".json");File.WriteAllText(corrupt,"{\"X\":0,\"Y\":1}");Throws<ArgumentException>(()=>DimensionCalibration.Open(corrupt));
            });
            Test("Coordinate correction applies once to engraving, placement and laser-off retreat", () =>
            {
                var paths=Line();var operations=new List<MarkingOperation>{new MarkingOperation{Paths=paths,Speed=500,Power=80,LaserMode="M3"}};
                string code=MarkingGeometry.GCode(operations,1,2,4,5,2,3);
                Assert(code.Contains("G0 X2 Y6\r\n")&&code.Contains("G1 X22 Y6 F500 S80"),"Placement/engraving not corrected");
                Assert(code.EndsWith("G91\r\nG0 X8 Y15\r\nG90\r\nG4 P0\r\n"),"Retreat not corrected");
                Assert(code.Contains("G4 P0\r\nM5 S0\r\nG91"),"Laser left enabled for corrected retreat");Near(10,paths[0].Points[1].X);
                Throws<ArgumentException>(()=>MarkingGeometry.GCode(operations,0,0,0,0,0,1));
            });
            Test("Marking dialog uses saved calibration while keeping design geometry unchanged", () =>
            {
                var profile=DimensionCalibration.Open(DimensionCalibration.DefaultPath);profile.X=2;profile.Y=3;profile.Save();
                try
                {
                    var document=CalibrationRuler.Create(100,100,1,10,true,false,80);document.OriginX=4;document.OriginY=7;document.Retreat=true;document.RetreatX=2;document.RetreatY=5;
                    string before=LayoutDocument.Serialize(document);
                    using(var form=new MarkingForm(core,document))
                    {
                        form.Location=new Point(-2000,-2000);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();Method(form,"Generate",null,EventArgs.Empty);
                        var deadline=DateTime.UtcNow.AddSeconds(30);while((bool)Field(form,"busy")&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                        object prepared=Field(form,"prepared");Assert(prepared!=null,((Label)Field(form,"status")).Text);string code=(string)prepared.GetType().GetField("Code").GetValue(prepared);
                        Assert(code.Contains("G0 X48 Y45"),"Saved correction missing from ruler origin");Assert(code.EndsWith("G91\r\nG0 X4 Y15\r\nG90\r\nG4 P0\r\n"),"Saved correction missing from retreat");
                        Assert(form.GeneratedCalibration.X==2&&form.GeneratedCalibration.Y==3,"Generated factor snapshot missing");
                        Assert(before==LayoutDocument.Serialize(document),"Design dimensions were modified");form.Close();
                    }
                }
                finally {var restore=DimensionCalibration.Open(DimensionCalibration.DefaultPath);restore.X=restore.Y=1;restore.Trial=null;restore.Save();}
            });
            Test("Calibration dialog renders a ruler and saves a measured axis without double correction", () =>
            {
                var profile=DimensionCalibration.Open(DimensionCalibration.DefaultPath);profile.Trial=new CalibrationTrial{LengthX=100,LengthY=100,UsedX=1,UsedY=1,AxisX=true,AxisY=true};profile.Save();
                try
                {
                    using(var form=new CalibrationForm(core))
                    {
                        form.Location=new Point(-2000,-2000);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();
                        ((NumericUpDown)Field(form,"actualX")).Value=98;((NumericUpDown)Field(form,"actualY")).Value=101;
                        Application.DoEvents();Assert(((Button)Field(form,"apply")).Enabled,"Measured application unavailable");
                        using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(artifacts,"calibration-dialog.png"));}
                        Method(form,"ApplyMeasurement");var saved=DimensionCalibration.Open(DimensionCalibration.DefaultPath);
                        Assert(Math.Abs(saved.X-100.0/98)<1e-12&&Math.Abs(saved.Y-100.0/101)<1e-12,"Measurement ratio not saved");
                        Assert(saved.Trial==null&&!((Button)Field(form,"apply")).Enabled,"Same trial can be reapplied");
                        Method(form,"ApplyMeasurement");Assert(DimensionCalibration.Open(DimensionCalibration.DefaultPath).Revision==saved.Revision,"Repeated application changed the profile");form.Close();
                    }
                }
                finally {var restore=DimensionCalibration.Open(DimensionCalibration.DefaultPath);restore.X=restore.Y=1;restore.Trial=null;restore.Save();}
            });
            Test("Basic shapes keep editable physical dimensions and closed fillable outlines", () =>
            {
                var rectangle=new LayoutItem{Kind=LayoutKind.Rectangle,ShapeWidth=30,ShapeHeight=15};var bounds=rectangle.Bounds("");Near(30,bounds.Width);Near(15,bounds.Height);
                Assert(rectangle.Outlines("").Single().Closed,"Rectangle is open");rectangle.ShapeWidth=40;Near(40,rectangle.Bounds("").Width);
                rectangle.Mode=LayoutMode.Fill;Assert(rectangle.Operation("").Paths.Count>200,"Rectangle fill absent");
                var ellipse=new LayoutItem{Kind=LayoutKind.Ellipse,ShapeWidth=20,ShapeHeight=10};bounds=ellipse.Bounds("");Near(20,bounds.Width);Near(10,bounds.Height);Assert(ellipse.Outlines("").Single().Closed,"Ellipse is open");
                var polygon=new LayoutItem{Kind=LayoutKind.RegularPolygon,ShapeWidth=20,Sides=5};var points=polygon.Outlines("").Single().Points;
                Assert(points.Count==5,"Polygon side count wrong");double edge=Math.Sqrt(Math.Pow(points[1].X-points[0].X,2)+Math.Pow(points[1].Y-points[0].Y,2));
                for(int i=0;i<5;i++)Near(edge,Math.Sqrt(Math.Pow(points[(i+1)%5].X-points[i].X,2)+Math.Pow(points[(i+1)%5].Y-points[i].Y,2)));
                polygon.Sides=2;Throws<ArgumentException>(()=>polygon.Validate());
            });
            Test("Three-point arcs pass through the selected side and reject collinear points", () =>
            {
                var arc=LayoutDrawing.Arc(new PointF(-10,0),new PointF(0,10),new PointF(10,0));Near(-10,arc[0].X);Near(10,arc[arc.Count-1].X);Near(10,arc.Max(p=>p.Y));Assert(arc.All(p=>p.Y>=-0.001),"Arc uses wrong side");
                var lower=LayoutDrawing.Arc(new PointF(-10,0),new PointF(0,-10),new PointF(10,0));Near(-10,lower.Min(p=>p.Y));
                Throws<ArgumentException>(()=>LayoutDrawing.Arc(new PointF(0,0),new PointF(1,1),new PointF(2,2)));
            });
            Test("Background excludes engraving and serial reservations while preserving the original enable flag", () =>
            {
                var bg=new LayoutItem{Kind=LayoutKind.Text,Content="{serial}",Background=true,Enabled=true};var front=new LayoutItem{Kind=LayoutKind.Rectangle,ShapeWidth=10,ShapeHeight=10};
                var document=new LayoutDocument{Items=new List<LayoutItem>{bg,front}};Assert(!document.UsesSerial&&document.Operations("000001").Count==1,"Background enters machining/serial flow");Throws<InvalidOperationException>(()=>bg.Operation("000001"));
                string json=LayoutDocument.Serialize(document);Assert(json.Contains("\"Version\":2"),"Background layout can be silently processed by an older version");
                var restored=LayoutDocument.Deserialize(json);Assert(restored.Items[0].Background&&restored.Items[0].Enabled,"Background state lost");Near(10,restored.Items[1].ShapeWidth);
                var legacy=LayoutDocument.Deserialize("{\"Version\":1,\"Items\":[]}");Assert(legacy.Version==2,"Older layout not upgraded");
                bg.Background=false;Assert(document.UsesSerial&&bg.CanMark,"Unlock lost previous machining flag");bg.Background=true;front.Enabled=false;Throws<ArgumentException>(()=>document.Operations("000001"));
            });
            Test("Canvas gestures create every standard drawing tool and support cancellation", () =>
            {
                using(var canvas=new LayoutCanvas{Width=800,Height=600,Document=new LayoutDocument()})
                {
                    var created=new List<LayoutItem>();canvas.ShapeCreated+=item=>{created.Add(item);canvas.Document.Items.Add(item);canvas.Rebuild();};
                    Action<DrawingTool,PointF,PointF> drag=(tool,a,b)=>{canvas.SetTool(tool);CanvasMouse(canvas,"OnMouseDown",a,1);CanvasMouse(canvas,"OnMouseMove",b,0);CanvasMouse(canvas,"OnMouseUp",b,1);};
                    drag(DrawingTool.Rectangle,new PointF(10,10),new PointF(30,20));Near(20,created.Last().ShapeWidth);Near(10,created.Last().ShapeHeight);
                    drag(DrawingTool.Circle,new PointF(50,20),new PointF(60,20));Near(20,created.Last().ShapeWidth);Near(created.Last().ShapeWidth,created.Last().ShapeHeight);
                    drag(DrawingTool.Ellipse,new PointF(80,10),new PointF(110,25));Near(30,created.Last().ShapeWidth);Near(15,created.Last().ShapeHeight);
                    drag(DrawingTool.Line,new PointF(10,40),new PointF(30,50));Assert(!created.Last().Outlines("")[0].Closed,"Line is closed");
                    drag(DrawingTool.RegularPolygon,new PointF(90,50),new PointF(100,50));Assert(created.Last().Kind==LayoutKind.RegularPolygon,"Regular polygon absent");
                    canvas.SetTool(DrawingTool.Polygon);CanvasMouse(canvas,"OnMouseDown",new PointF(40,70),1);CanvasMouse(canvas,"OnMouseDown",new PointF(60,70),1);CanvasMouse(canvas,"OnMouseDown",new PointF(60,90),2);Assert(created.Last().Outlines("")[0].Closed,"Polygon is open");
                    canvas.SetTool(DrawingTool.Polyline);CanvasMouse(canvas,"OnMouseDown",new PointF(10,100),1);CanvasMouse(canvas,"OnMouseDown",new PointF(20,110),1);CanvasMouse(canvas,"OnMouseDown",new PointF(30,100),1);Method(canvas,"OnKeyDown",new KeyEventArgs(Keys.Enter));Assert(created.Last().Geometry[0].Points.Count==3&&!created.Last().Geometry[0].Closed,"Polyline wrong");
                    canvas.SetTool(DrawingTool.Arc);CanvasMouse(canvas,"OnMouseDown",new PointF(80,80),1);CanvasMouse(canvas,"OnMouseDown",new PointF(90,90),1);CanvasMouse(canvas,"OnMouseDown",new PointF(100,80),1);Near(90,created.Last().Bounds("").Bottom);
                    canvas.SetTool(DrawingTool.Freehand);CanvasMouse(canvas,"OnMouseDown",new PointF(10,80),1);CanvasMouse(canvas,"OnMouseMove",new PointF(20,85),0);CanvasMouse(canvas,"OnMouseMove",new PointF(30,80),0);CanvasMouse(canvas,"OnMouseUp",new PointF(40,85),1);Assert(created.Last().Geometry[0].Points.Count==4,"Freehand samples absent");
                    Assert(created.Count==9,"Drawing tool missing");canvas.SetTool(DrawingTool.Polyline);CanvasMouse(canvas,"OnMouseDown",new PointF(0,0),1);Method(canvas,"OnKeyDown",new KeyEventArgs(Keys.Escape));Assert(canvas.Tool==DrawingTool.Select&&created.Count==9,"Cancelled draft created an object");
                    canvas.SetTool(DrawingTool.Polygon);CanvasMouse(canvas,"OnMouseDown",new PointF(0,0),1);CanvasMouse(canvas,"OnMouseDown",new PointF(1,1),1);Method(canvas,"OnKeyDown",new KeyEventArgs(Keys.Back));Method(canvas,"OnKeyDown",new KeyEventArgs(Keys.Enter));Assert(created.Count==9,"Unfinished polygon created");
                }
            });
            Test("Object checkboxes lock mouse and numeric edits, keep foreground selectable and undo correctly", () =>
            {
                var front=new LayoutItem{Kind=LayoutKind.Rectangle,Name="加工用矩形",ShapeWidth=30,ShapeHeight=15,X=20,Y=20};
                var bg=new LayoutItem{Kind=LayoutKind.Rectangle,Name="材料テンプレート",ShapeWidth=100,ShapeHeight=60,Background=true};
                var document=new LayoutDocument{Items=new List<LayoutItem>{front,bg}};
                using(var form=new LayoutForm(core))
                {
                    typeof(LayoutForm).GetField("document",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,document);typeof(LayoutForm).GetField("saved",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,LayoutDocument.Serialize(document));Method(form,"RefreshScene",1);
                    form.Location=new Point(-2000,-2000);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();Application.DoEvents();var canvas=(LayoutCanvas)Field(form,"canvas");canvas.Fit();
                    Assert(!((PropertyGrid)Field(form,"properties")).Enabled&&!((CheckBox)Field(form,"markEnabled")).Enabled&&((CheckBox)Field(form,"background")).Checked,"Fixed properties editable");
                    CanvasMouse(canvas,"OnMouseDown",new PointF(5,5),1);CanvasMouse(canvas,"OnMouseMove",new PointF(15,15),0);CanvasMouse(canvas,"OnMouseUp",new PointF(15,15),1);Near(0,bg.X);Near(0,bg.Y);
                    CanvasMouse(canvas,"OnMouseDown",new PointF(100,60),1);CanvasMouse(canvas,"OnMouseMove",new PointF(120,80),0);CanvasMouse(canvas,"OnMouseUp",new PointF(120,80),1);Near(1,bg.Scale);
                    CanvasMouse(canvas,"OnMouseDown",new PointF(30,25),1);CanvasMouse(canvas,"OnMouseUp",new PointF(30,25),1);Assert(canvas.SelectedIndex==0,"Background blocks foreground selection");
                    var enabled=(CheckBox)Field(form,"markEnabled");enabled.Checked=false;Assert(!front.Enabled,"OFF checkbox failed");Throws<ArgumentException>(()=>document.Operations(""));
                    Method(form,"Undo",false);document=(LayoutDocument)Field(form,"document");Assert(document.Items[0].Enabled,"Checkbox undo failed");Method(form,"Select",1);((CheckBox)Field(form,"background")).Checked=false;Assert(((PropertyGrid)Field(form,"properties")).Enabled&&document.Items[1].CanMark,"Unlock did not restore edits/machining");Method(form,"Undo",false);
                    document=(LayoutDocument)Field(form,"document");Method(form,"Select",1);
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(artifacts,"layout-drawing.png"));}
                    File.WriteAllText(Path.Combine(artifacts,"drawing-template.lgrbl-layout"),LayoutDocument.Serialize(document));form.Close();
                }
            });
            Test("Native bitmap trace keeps image size, orientation, and holes", () =>
            {
                using(var bitmap=new Bitmap(100,100)) using(var graphics=Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);graphics.FillRectangle(Brushes.Black,10,10,70,70);graphics.FillRectangle(Brushes.White,30,30,20,20);
                    var item=LayoutBitmapTrace.Convert(bitmap,"画像トレース",new SizeF(20,20),true,2,false,0,true,0.2m,false);
                    Near(20,item.Bounds("").Width);Near(20,item.Bounds("").Height);Assert(item.Geometry.Count==2&&item.Geometry.All(p=>p.Closed),"Hole/border lost");
                    item.Mode=LayoutMode.Fill;var paths=item.Operation("").Paths;
                    foreach(var path in paths.Where(p=>p.Points[0].Y>10.2&&p.Points[0].Y<13.8)) Assert(path.Points.Max(p=>p.X)<6.1||path.Points.Min(p=>p.X)>9.9,"Fill crossed image hole");
                    Assert(bitmap.GetPixel(15,10).ToArgb()==Color.Black.ToArgb()&&bitmap.GetPixel(15,90).ToArgb()==Color.White.ToArgb(),"Input bitmap flipped/mutated");
                    using(var top=new Bitmap(100,100)) using(var g=Graphics.FromImage(top))
                    {g.Clear(Color.White);g.FillRectangle(Brushes.Black,10,10,20,20);var upper=LayoutBitmapTrace.Convert(top,"上",new SizeF(20,20),false,2,false,0,false,0.2m,false);Assert(upper.Geometry.SelectMany(p=>p.Points).Min(p=>p.Y)>13,"Image mirrored vertically");}
                }
            });
            Test("Blank bitmap trace fails without a partial object and travel optimization is reused", () =>
            {
                using(var bitmap=new Bitmap(80,80)) using(var graphics=Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);Throws<ArgumentException>(()=>LayoutBitmapTrace.Convert(bitmap,"白紙",new SizeF(10,10),false,2,false,0,false,0.2m,false));
                    graphics.FillRectangle(Brushes.Black,10,10,15,15);graphics.FillRectangle(Brushes.Black,45,45,15,15);
                    var traced=LayoutBitmapTrace.Convert(bitmap,"最適化",new SizeF(10,10),true,2,true,1,true,0.2m,true);Assert(traced.Geometry.Count==2,"Optimized trace lost contours");
                }
            });
            Test("Native ImageProcessor exports layout paths without replacing main G-code", () =>
            {
                string file=ImageFixture("layout-trace-input.bmp");
                core.LoadedFile.LoadMarkingCode(MarkingGeometry.GCode(Line(),500,80,"M3",0,0,0,0));int before=core.LoadedFile.Count;
                LayoutItem result=null;Exception error=null;using(var done=new ManualResetEvent(false))
                {
                    var processor=new LaserGRBL.RasterConverter.ImageProcessor(core,file,new Size(250,250),false);
                    LaserGRBL.RasterConverter.ImageProcessor.GenerationCompleteDlg completion=ex=>{error=ex;done.Set();};
                    LaserGRBL.RasterConverter.ImageProcessor.GenerationComplete+=completion;
                    try
                    {
                        processor.SelectedTool=LaserGRBL.RasterConverter.ImageProcessor.Tool.Vectorize;processor.Brightness=processor.Contrast=100;processor.Red=processor.Green=processor.Blue=100;
                        processor.UseThreshold=true;processor.Threshold=50;processor.UseSmoothing=true;processor.Smoothing=1;processor.UseOptimize=true;processor.Optimize=0.2m;
                        processor.TargetSize=new SizeF(20,20);processor.TargetOffset=new PointF(3,4);processor.BorderSpeed=700;processor.MaxPower=80;processor.LaserOn="M3";
                        processor.LayoutTraceOutput=item=>result=item;processor.Resume();processor.GenerateGCode();
                        Pump(()=>done.WaitOne(0));Assert(error==null,"Native conversion failed: "+error);Assert(result!=null,"No layout result");
                        Assert(core.LoadedFile.Count==before&&core.LoadedFile.IsMarkingProgram,"Image conversion replaced the main job");Near(3,result.X);Near(4,result.Y);Near(700,result.Speed);Near(80,result.Power);
                        File.Delete(file);var restored=LayoutDocument.Deserialize(LayoutDocument.Serialize(new LayoutDocument{Items=new List<LayoutItem>{result}}));Near(20,restored.Items[0].Bounds("").Width);
                        var shape=restored.Items[0];shape.Scale=2;shape.Rotation=90;Near(40,shape.Bounds("").Height);shape.Background=true;Assert(!shape.CanMark,"Image template still machining");
                    }
                    finally {LaserGRBL.RasterConverter.ImageProcessor.GenerationComplete-=completion;processor.Dispose();}
                }
            });
            Test("Image trace dialog reuses native controls and returns a layout object", () =>
            {
                string file=ImageFixture("native-trace-dialog.png");int before=core.LoadedFile.Count;
                using(var form=new LaserGRBL.RasterConverter.RasterToLaserForm(core,file,false,true))
                {
                    form.Location=new Point(-2200,-2200);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();
                    Pump(()=>!((bool)Field(form,"preventClose"))&&((Button)Field(form,"BtnCreate")).Enabled);
                    var processor=(LaserGRBL.RasterConverter.ImageProcessor)Field(form,"IP");Assert(processor.SelectedTool==LaserGRBL.RasterConverter.ImageProcessor.Tool.Vectorize,"Wrong native trace mode");
                    Assert(!((RadioButton)Field(form,"RbLineToLineTracing")).Enabled&&!((Control)Field(form,"CbFillingDirection")).Enabled,"Unsupported import mode selectable");
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(artifacts,"native-image-trace.png"));}
                    processor.TargetSize=new SizeF(25,25);processor.TargetOffset=PointF.Empty;processor.BorderSpeed=650;processor.MaxPower=80;processor.LaserOn="M3";
                    processor.GenerateGCode();Pump(()=>form.LayoutTraceResult!=null&&form.DialogResult==DialogResult.OK);
                    Assert(core.LoadedFile.Count==before,"Native dialog replaced the main file");Assert(form.LayoutTraceResult.Kind==LayoutKind.BitmapTrace,"Image object type missing");
                    var document=new LayoutDocument {Items=new List<LayoutItem>{form.LayoutTraceResult,new LayoutItem{Kind=LayoutKind.Text,Content="IMAGE",Y=27,Mode=LayoutMode.Fill}}};
                    File.WriteAllText(Path.Combine(artifacts,"image-template.lgrbl-layout"),LayoutDocument.Serialize(document));
                    using(var layout=new LayoutForm(core))
                    {
                        typeof(LayoutForm).GetField("document",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(layout,document);typeof(LayoutForm).GetField("saved",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(layout,LayoutDocument.Serialize(document));Method(layout,"RefreshScene",0);
                        layout.Location=new Point(-2200,-2200);layout.StartPosition=FormStartPosition.Manual;layout.ShowInTaskbar=false;layout.Show();Application.DoEvents();((LayoutCanvas)Field(layout,"canvas")).Fit();
                        using(var bitmap=new Bitmap(layout.Width,layout.Height)){layout.DrawToBitmap(bitmap,new Rectangle(Point.Empty,layout.Size));bitmap.Save(Path.Combine(artifacts,"layout-image.png"));}layout.Close();
                    }
                }
            });
            Test("Image trace cancellation returns no object and keeps the current job", () =>
            {
                string file=ImageFixture("cancel-trace.bmp");int before=core.LoadedFile.Count;
                using(var form=new LaserGRBL.RasterConverter.RasterToLaserForm(core,file,false,true))
                {
                    form.Location=new Point(-2200,-2200);form.StartPosition=FormStartPosition.Manual;form.ShowInTaskbar=false;form.Show();Pump(()=>!((bool)Field(form,"preventClose")));
                    var processor=(LaserGRBL.RasterConverter.ImageProcessor)Field(form,"IP");processor.TargetSize=new SizeF(50,50);processor.BorderSpeed=650;processor.MaxPower=80;processor.LaserOn="M3";processor.GenerateGCode();
                    Method(form,"BtnCancelClick",null,EventArgs.Empty);Application.DoEvents();Assert(form.LayoutTraceResult==null&&form.DialogResult==DialogResult.Cancel,"Cancelled trace accepted");
                    Assert(core.LoadedFile.Count==before,"Cancellation changed main job");
                }
            });
            Test("Standard bitmap trace still loads the main job through the original conversion path", () =>
            {
                using(var bitmap=new Bitmap(80,80))using(var graphics=Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);graphics.FillEllipse(Brushes.Black,10,10,60,60);
                    var config=new GrblFile.L2LConf{res=5,fres=3,markSpeed=600,borderSpeed=600,maxPower=80,lOn="M3",lOff="M5",pwm=true,dir=LaserGRBL.RasterConverter.ImageProcessor.Direction.None};
                    core.LoadedFile.LoadImagePotrace(bitmap,"standard-image.bmp",false,2,true,1,true,0.2m,true,config,false,core);
                    Assert(core.LoadedFile.Count>10&&!core.LoadedFile.IsMarkingProgram,"Standard image import no longer loads its normal G-code");
                }
            });
            core.Exiting();
        }
        Console.WriteLine("RESULT: "+passed+" passed, "+failed+" failed");
        Environment.Exit(failed==0 ? 0 : 1);
        return failed==0 ? 0 : 1;
    }

    private static void Pump(Func<bool> complete)
    {
        var deadline=DateTime.UtcNow.AddSeconds(45);
        while(!complete()&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
        Assert(complete(),"Asynchronous image conversion timed out");
    }
    private static string ImageFixture(string name)
    {
        string file=Path.Combine(artifacts,name);using(var bitmap=new Bitmap(100,100))using(var g=Graphics.FromImage(bitmap))
        {g.Clear(Color.White);g.FillEllipse(Brushes.Black,10,10,75,75);g.FillEllipse(Brushes.White,32,32,30,30);g.FillRectangle(Brushes.Black,75,75,15,15);bitmap.Save(file,name.EndsWith(".png")?System.Drawing.Imaging.ImageFormat.Png:System.Drawing.Imaging.ImageFormat.Bmp);}
        return file;
    }
    private static void CanvasMouse(LayoutCanvas canvas,string method,PointF position,int clicks)
    {
        PointF pixel=(PointF)canvas.GetType().GetMethod("Map",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{position});
        Method(canvas,method,new MouseEventArgs(MouseButtons.Left,clicks,(int)Math.Round(pixel.X),(int)Math.Round(pixel.Y),0));
    }
    private static object Field(object value,string name) { return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value); }
    private static void Method(object value,string name,params object[] args) { value.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name==name && m.GetParameters().Length==args.Length && m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(ok=>ok)).Invoke(value,args); }
    private static void Test(string name,Action action) { try { action(); passed++; Console.WriteLine("PASS "+name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex); } }
    private static void Assert(bool condition,string message) { if(!condition)throw new Exception(message); }
    private static void Near(double expected,double actual) { Assert(Math.Abs(expected-actual)<0.02,"Expected "+expected+", actual "+actual); }
    private static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    private static string JournalFile() { return Path.Combine(artifacts,"journal-"+Guid.NewGuid()+".json"); }
    private static List<MarkingPath> Line() { return new List<MarkingPath> { new MarkingPath(new[]{new PointF(0,0),new PointF(10,0)},false) }; }
    private static MarkingPath Box(float a,float b,float c,float d) { return new MarkingPath(new[]{new PointF(a,b),new PointF(c,b),new PointF(c,d),new PointF(a,d)},true); }
    private static void RenderQr(string value,string filename)
    {
        var paths=MarkingGeometry.QrFill(value,15,0.05);
        using(var image=new Bitmap(800,800)) using(var graphics=Graphics.FromImage(image)) using(var pen=new Pen(Color.Black,2.5f))
        {
            graphics.Clear(Color.White); graphics.SmoothingMode=SmoothingMode.None;
            foreach(var path in paths) graphics.DrawLines(pen,path.Points.Select(p=>new PointF(25+p.X*50,775-p.Y*50)).ToArray());
            image.Save(Path.Combine(artifacts,filename));
        }
    }
    private sealed class TestCore : GrblCore
    {
        public TestCore(Control sync):base(sync,null,null) { }
        public void Arm() { typeof(GrblCore).GetField("awaitingPhysicalCompletion",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(this,true); }
        public void Feed(string report) { typeof(GrblCore).GetMethod("ManageRealTimeStatus",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(this,new object[]{report}); }
        public void AddPending(GrblCommand command) { ((Queue<GrblCommand>)typeof(GrblCore).GetField("mPending",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(this)).Enqueue(command); }
        public void Reply(string line) { ManageCommandResponse(line); }
        public void Fail(DetectedIssue issue) { SetIssue(issue); }
        public void Footer() { OnJobEnd(); }
    }
}
