// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LaserGRBL.Marking
{
    [DataContract]
    internal sealed class CalibrationTrial
    {
        [DataMember] public double LengthX, LengthY, UsedX, UsedY;
        [DataMember] public bool AxisX, AxisY;
        [DataMember] public string Created;
        public void Validate()
        {
            LayoutItem.Range(LengthX, 10, 1000); LayoutItem.Range(LengthY, 10, 1000);
            DimensionCalibration.ValidateFactor(UsedX); DimensionCalibration.ValidateFactor(UsedY);
            if (!AxisX && !AxisY) throw new InvalidDataException("測定する軸がありません。");
        }
    }

    [DataContract]
    internal sealed class DimensionCalibration
    {
        [DataMember] public double X = 1, Y = 1;
        [DataMember] public long Revision;
        [DataMember] public CalibrationTrial Trial;
        private string filename;
        public static string DefaultPath { get { return Path.Combine(GrblCore.DataPath, "dimension-calibration.json"); } }
        public static DimensionCalibration Open(string path)
        {
            DimensionCalibration profile;
            if (File.Exists(path)) using (FileStream stream = File.OpenRead(path))
                profile = (DimensionCalibration)new DataContractJsonSerializer(typeof(DimensionCalibration)).ReadObject(stream);
            else profile = new DimensionCalibration();
            if (profile == null || profile.Revision < 0) throw new InvalidDataException("寸法補正の記録が不正です。");
            ValidateFactor(profile.X); ValidateFactor(profile.Y);
            if (profile.Trial != null) profile.Trial.Validate();
            profile.filename = path; return profile;
        }
        public static void ValidateFactor(double value) { LayoutItem.Range(value, 0.1, 10); }
        public static double Calculate(double usedFactor, double target, double measured)
        {
            ValidateFactor(usedFactor); LayoutItem.Range(target, 0.001, 100000); LayoutItem.Range(measured, 0.001, 100000);
            double result = usedFactor * target / measured; ValidateFactor(result); return result;
        }
        public void Save()
        {
            ValidateFactor(X); ValidateFactor(Y); if (Trial != null) Trial.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filename)));
            using (FileStream gate = new FileStream(filename + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (File.Exists(filename) && Open(filename).Revision != Revision) throw new IOException("別の画面で寸法補正が変更されました。画面を開き直してください。");
                long nextRevision = checked(Revision + 1), previous = Revision; Revision = nextRevision;
                try
                {
                    string temp = filename + ".tmp";
                    using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    { new DataContractJsonSerializer(typeof(DimensionCalibration)).WriteObject(stream, this); stream.Flush(true); }
                    if (File.Exists(filename)) File.Replace(temp, filename, null); else File.Move(temp, filename);
                }
                catch { Revision = previous; throw; }
            }
        }
    }

    internal static class CalibrationRuler
    {
        public static LayoutDocument Create(double lengthX, double lengthY, double minor, double major, bool axisX, bool axisY, double power)
        {
            LayoutItem.Range(lengthX, 10, 1000); LayoutItem.Range(lengthY, 10, 1000);
            LayoutItem.Range(minor, 0.1, 100); LayoutItem.Range(major, minor, 1000);
            double ratio = major / minor;
            if (Math.Abs(ratio - Math.Round(ratio)) > 0.000001 || ratio > 10000) throw new ArgumentException("番号の間隔は目盛り間隔の整数倍にしてください。");
            if (!axisX && !axisY) throw new ArgumentException("XまたはYを選択してください。");
            LayoutDocument document = new LayoutDocument();
            if (axisX) document.Items.Add(Axis(lengthX, minor, (int)Math.Round(ratio), true, power));
            if (axisY) document.Items.Add(Axis(lengthY, minor, (int)Math.Round(ratio), false, power));
            document.Validate(); return document;
        }
        private static LayoutItem Axis(double length, double minor, int majorEvery, bool horizontal, double power)
        {
            int ticks = (int)Math.Floor(length / minor + 0.0000001);
            if (ticks > 2000) throw new ArgumentException("目盛りが多すぎます。間隔を広げてください。");
            List<MarkingPath> paths = new List<MarkingPath>();
            Action<float, float, float, float> line = (x1, y1, x2, y2) => paths.Add(new MarkingPath(new[] { new PointF(x1,y1), new PointF(x2,y2) }, false));
            if (horizontal) line(0, 8, (float)length, 8); else line(8, 0, 8, (float)length);
            Action<double, bool> mark = (position, numbered) =>
            {
                float p = (float)position, end = numbered ? 4 : 6;
                if (horizontal) line(p, 8, p, end); else line(8, p, end, p);
                if (numbered)
                {
                    var text = MarkingGeometry.Text(position.ToString("0.###", CultureInfo.InvariantCulture), "Arial", 2);
                    double width = text.SelectMany(t => t.Points).Max(t => t.X);
                    double x = horizontal ? Math.Max(0, Math.Min(length - width, position - width / 2)) : 0;
                    double y = horizontal ? 0 : Math.Max(0, Math.Min(length - 2, position - 1));
                    paths.AddRange(MarkingGeometry.Shift(text, x, y));
                }
            };
            for (int i = 0; i <= ticks; i++) mark(i * minor, i % majorEvery == 0 || Math.Abs(i * minor - length) < 0.000001);
            if (Math.Abs(ticks * minor - length) > 0.000001) mark(length, true);
            return new LayoutItem { Kind = LayoutKind.SVG, Name = horizontal ? "X定規" : "Y定規", X = horizontal ? 20 : 0, Y = horizontal ? 0 : 20,
                Mode = LayoutMode.Trace, Power = power, Geometry = paths.Select(p => new LayoutPath { Closed = p.Closed,
                    Points = p.Points.Select(q => new LayoutPoint { X = q.X, Y = q.Y }).ToList() }).ToList() };
        }
    }
}
