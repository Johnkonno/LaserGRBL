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
