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
                Throws<ArgumentException>(()=>LayoutDocument.Deserialize("{\"Version\":2,\"Items\":[]}"));
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
            core.Exiting();
        }
        Console.WriteLine("RESULT: "+passed+" passed, "+failed+" failed");
        Environment.Exit(failed==0 ? 0 : 1);
        return failed==0 ? 0 : 1;
    }

    private static object Field(object value,string name) { return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value); }
    private static void Method(object value,string name,params object[] args) { value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,args); }
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
