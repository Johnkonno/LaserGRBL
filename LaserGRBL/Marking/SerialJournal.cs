// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LaserGRBL.Marking
{
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
            journal.filename = path;
            return journal;
        }
        public string Format() { return Prefix + Next.ToString("D" + Digits, CultureInfo.InvariantCulture) + Suffix; }
        public void Begin()
        {
            if (Pending) throw new InvalidOperationException("前回の加工結果を確認してください。");
            checked { long next = Next + Increment; }
            PendingText = Format();
            Pending = true;
            Save();
        }
        public void Resolve(bool completed)
        {
            if (!Pending) throw new InvalidOperationException("処理中の連番はありません。");
            if (completed) Next = checked(Next + Increment);
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
