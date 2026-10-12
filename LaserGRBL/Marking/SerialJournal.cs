// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LaserGRBL.Marking
{
    public sealed class MarkingVariables
    {
        public DateTime Timestamp = DateTime.Now;
        public string Lot, Product, OperatorName;
        public int Digits = 6;
    }

    [DataContract]
    public sealed class SerialJournal
    {
        [DataMember] public long Next = 1;
        [DataMember] public long Increment = 1;
        [DataMember] public bool Pending;
        [DataMember] public string PendingText;
        [DataMember] public string Prefix = "";
        [DataMember] public string Suffix = "";
        [DataMember] public int Digits = 6;
        [DataMember(EmitDefaultValue = false)] public string Pattern;
        [DataMember(EmitDefaultValue = false)] public string PatternMarker;
        [DataMember(EmitDefaultValue = false)] public string Lot, Product, OperatorName;
        [DataMember] public bool UseFixedTime;
        [DataMember(EmitDefaultValue = false)] public DateTime? FixedTime, ReservedAt;
        [DataMember(EmitDefaultValue = false)] public long? ReservedNumber;
        [DataMember] public long Revision;
        private string filename;

        public static SerialJournal Open(string path)
        {
            SerialJournal journal;
            if (File.Exists(path))
                using (FileStream stream = File.OpenRead(path)) journal = (SerialJournal)new DataContractJsonSerializer(typeof(SerialJournal)).ReadObject(stream);
            else journal = new SerialJournal();
            if (journal.Next < 0 || journal.Increment < 1 || journal.Digits < 1 || journal.Digits > 18 || journal.Revision < 0)
                throw new InvalidDataException("連番の記録が不正です。記録ファイルを確認してください。");
            ValidateMarker(journal.PatternMarker ?? "n");
            if (journal.UseFixedTime && !journal.FixedTime.HasValue)
                throw new InvalidDataException("指定日時の記録を確認してください。");
            if (journal.Pattern != null) journal.Format(journal.Timestamp(journal.Next));
            journal.filename = path;
            return journal;
        }
        public string Format()
        {
            return Format(Timestamp(Next));
        }
        public DateTime Timestamp(long number)
        {
            if (UseFixedTime && FixedTime.HasValue) return NormalizeTime(FixedTime.Value);
            return NormalizeTime(ReservedNumber == number && ReservedAt.HasValue ? ReservedAt.Value : DateTime.Now);
        }
        internal static DateTime NormalizeTime(DateTime value)
        {
            // JSON journal timestamps retain milliseconds; quantize before creating the printable text.
            DateTimeKind kind = value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Local : value.Kind;
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, kind);
        }
        public string Format(DateTime timestamp)
        {
            return Pattern == null ? Prefix + Next.ToString("D" + Digits, CultureInfo.InvariantCulture) + Suffix :
                Render(Pattern, Next, PatternMarker ?? "n", new MarkingVariables {
                    Timestamp = NormalizeTime(timestamp), Lot = Lot, Product = Product, OperatorName = OperatorName, Digits = Digits });
        }
        public static string Render(string pattern, long number)
        {
            return Render(pattern, number, "n");
        }
        private static void ValidateMarker(string marker)
        {
            if (marker == null || marker.Length != 1 || "nN#@".IndexOf(marker[0]) < 0)
                throw new ArgumentException("連番指定文字は n・N・#・@ から選んでください。");
        }
        public static string Render(string pattern, long number, string marker)
        {
            return Render(pattern, number, marker, new MarkingVariables());
        }
        public static string Render(string pattern, long number, string marker, MarkingVariables variables)
        {
            ValidateMarker(marker);
            if (variables == null || variables.Digits < 1 || variables.Digits > 18)
                throw new ArgumentException("差し込み設定を確認してください。");
            if (number < 0) throw new ArgumentException("番号は0以上で指定してください。");
            if (string.IsNullOrEmpty(pattern) || pattern.Length > 256)
                throw new ArgumentException("フォーマットは1～256文字で指定してください。");
            StringBuilder result = new StringBuilder();
            bool hasReplacement = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '\\')
                {
                    if (++i == pattern.Length) throw new ArgumentException("末尾の \\ には固定文字を続けてください。");
                    result.Append(pattern[i]);
                }
                else if (pattern[i] == '{')
                {
                    int end = pattern.IndexOf('}', i + 1);
                    if (end < 0) throw new ArgumentException("差し込みの末尾に } を付けてください。");
                    result.Append(ExpandToken(pattern.Substring(i + 1, end - i - 1), number, variables));
                    hasReplacement = true;
                    i = end;
                }
                else if (pattern[i] == '}') throw new ArgumentException("固定の括弧は \\{ と \\} で指定してください。");
                else if (pattern[i] == marker[0])
                {
                    int start = i;
                    while (i + 1 < pattern.Length && pattern[i + 1] == marker[0]) i++;
                    int width = i - start + 1;
                    if (width > 18) throw new ArgumentException(marker + " の連続は1～18個で指定してください。");
                    result.Append(number.ToString("D" + width, CultureInfo.InvariantCulture));
                    hasReplacement = true;
                }
                else result.Append(pattern[i]);
                if (result.Length > 4096) throw new ArgumentException("差し込み後の文字列は4096文字以内にしてください。");
            }
            if (!hasReplacement) throw new ArgumentException("連番部分を " + marker + " で指定するか、{date} などを挿入してください。");
            if (string.IsNullOrWhiteSpace(result.ToString())) throw new ArgumentException("打刻内容が空です。");
            return result.ToString();
        }
        private static string ExpandToken(string token, long number, MarkingVariables variables)
        {
            int colon = token.IndexOf(':');
            string name = (colon < 0 ? token : token.Substring(0, colon)).ToLowerInvariant();
            string format = colon < 0 ? null : token.Substring(colon + 1);
            if (format != null && format.Length == 0) throw new ArgumentException("差し込みの書式が空です。");
            if (name == "serial" || name == "count")
            {
                int width = variables.Digits;
                if (format != null && (!int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out width) || width < 1 || width > 18))
                    throw new ArgumentException("{serial:5} のように桁数を1～18で指定してください。");
                return number.ToString("D" + width, CultureInfo.InvariantCulture);
            }
            int sign = name.IndexOfAny(new[] { '+', '-' });
            int offset = 0;
            if (sign >= 0)
            {
                if (!int.TryParse(name.Substring(sign), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset) || Math.Abs((long)offset) > 36500)
                    throw new ArgumentException("日付の加減算は-36500～36500日で指定してください。");
                name = name.Substring(0, sign);
            }
            if (name == "date" || name == "time" || name == "datetime")
            {
                try
                {
                    string defaultFormat = name == "date" ? "yyyyMMdd" : name == "time" ? "HHmmss" : "yyyyMMdd-HHmmss";
                    return variables.Timestamp.AddDays(offset).ToString(format ?? defaultFormat, CultureInfo.InvariantCulture);
                }
                catch (ArgumentException) { throw new ArgumentException("日付・時刻の書式または加減算の範囲を確認してください。"); }
                catch (FormatException) { throw new ArgumentException("日付・時刻の書式を確認してください。"); }
            }
            if (sign >= 0 || format != null) throw new ArgumentException("この差し込みには書式や日数を指定できません：" + token);
            switch (name)
            {
                case "year": return variables.Timestamp.ToString("yyyy", CultureInfo.InvariantCulture);
                case "yy": return variables.Timestamp.ToString("yy", CultureInfo.InvariantCulture);
                case "month": return variables.Timestamp.ToString("MM", CultureInfo.InvariantCulture);
                case "day": return variables.Timestamp.ToString("dd", CultureInfo.InvariantCulture);
                case "hour": return variables.Timestamp.ToString("HH", CultureInfo.InvariantCulture);
                case "minute": return variables.Timestamp.ToString("mm", CultureInfo.InvariantCulture);
                case "second": return variables.Timestamp.ToString("ss", CultureInfo.InvariantCulture);
                case "weekday": return variables.Timestamp.ToString("ddd", CultureInfo.InvariantCulture);
                case "dayofyear": return variables.Timestamp.DayOfYear.ToString("D3", CultureInfo.InvariantCulture);
                case "lot": return RequiredValue(variables.Lot, "ロット名");
                case "product": return RequiredValue(variables.Product, "品名");
                case "operator": return RequiredValue(variables.OperatorName, "担当者名");
                default: throw new ArgumentException("未対応の差し込みです：{" + token + "}");
            }
        }
        private static string RequiredValue(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException(name + "を1～128文字で入力してください。");
            return value;
        }
        public void Begin()
        {
            DateTime timestamp = Timestamp(Next);
            Begin(timestamp, Format(timestamp));
        }
        public void Begin(DateTime timestamp, string generatedText)
        {
            if (Pending) throw new InvalidOperationException("前回の加工結果を確認してください。");
            checked { long next = Next + Increment; }
            timestamp = NormalizeTime(timestamp);
            if (Format(timestamp) != generatedText) throw new InvalidOperationException("差し込み設定が生成済みの内容と異なります。生成し直してください。");
            PendingText = generatedText;
            ReservedAt = timestamp;
            ReservedNumber = Next;
            Pending = true;
            Save();
        }
        public void Resolve(bool completed)
        {
            if (!Pending) throw new InvalidOperationException("処理中の連番はありません。");
            if (completed)
            {
                Next = checked(Next + Increment);
                ReservedAt = null;
                ReservedNumber = null;
            }
            Pending = false;
            PendingText = null;
            Save();
        }
        public void Save()
        {
            // Serialize writers and reject a stale session before it can reuse another session's number.
            using (FileStream gate = new FileStream(filename + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (File.Exists(filename) && Open(filename).Revision != Revision)
                    throw new IOException("別の画面で連番が変更されました。画面を開き直してください。");
                Revision = checked(Revision + 1);
                string temporary = filename + ".tmp";
                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(SerialJournal)).WriteObject(stream, this);
                    stream.Flush(true);
                }
                if (File.Exists(filename)) File.Replace(temporary, filename, null);
                else File.Move(temporary, filename);
            }
        }
    }
}
