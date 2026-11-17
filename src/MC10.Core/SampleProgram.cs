using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MC10.Core
{
    /// <summary>
    /// A BASIC sample file. Lines starting with ";;" are notes for the emulator and are never typed:
    ///   ;; title: Counting loop
    ///   ;; summary: what the program shows
    ///   ;; look: something to try in the debugger (may repeat)
    ///   ;; break: stm_FOR, stm_NEXT      (labels worth a breakpoint)
    /// Everything else is the BASIC program, typed into the MC-10 exactly as written.
    /// </summary>
    public sealed class SampleProgram
    {
        public string FileName { get; private set; } = "";
        public string Title { get; private set; } = "";
        public string Summary { get; private set; } = "";
        public List<string> Look { get; } = new List<string>();
        public List<string> Breaks { get; } = new List<string>();
        public string Code { get; private set; } = "";

        public static SampleProgram Parse(string fileName, string text)
        {
            var s = new SampleProgram { FileName = fileName, Title = Path.GetFileNameWithoutExtension(fileName) };
            var code = new StringBuilder();
            var summary = new List<string>();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (!line.StartsWith(";;", StringComparison.Ordinal))
                {
                    if (line.Trim().Length > 0) code.Append(line.Trim()).Append('\r');
                    continue;
                }
                string note = line.Substring(2).Trim();
                int colon = note.IndexOf(':');
                string key = colon > 0 ? note.Substring(0, colon).Trim().ToLowerInvariant() : "";
                string value = colon > 0 ? note.Substring(colon + 1).Trim() : note;
                switch (key)
                {
                    case "title": s.Title = value; break;
                    case "look": s.Look.Add(value); break;
                    case "break":
                        s.Breaks.AddRange(value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0));
                        break;
                    case "summary": summary.Add(value); break;
                    default: if (note.Length > 0) summary.Add(note); break;
                }
            }
            s.Summary = string.Join(" ", summary);
            s.Code = code.ToString();
            return s;
        }

        public static SampleProgram Load(string path) => Parse(Path.GetFileName(path), File.ReadAllText(path));

        /// <summary>The notes as readable text (shown before the program is typed).</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Title).AppendLine();
            if (Summary.Length > 0) sb.AppendLine(Summary).AppendLine();
            if (Look.Count > 0)
            {
                sb.AppendLine("Things to look at in the debugger:");
                foreach (string l in Look) sb.AppendLine("  - " + l);
                sb.AppendLine();
            }
            if (Breaks.Count > 0) sb.AppendLine("Suggested breakpoints: " + string.Join(", ", Breaks)).AppendLine();
            sb.AppendLine("Program:");
            foreach (string l in Code.Split(new[] { '\r' }, StringSplitOptions.RemoveEmptyEntries)) sb.AppendLine("  " + l);
            return sb.ToString();
        }
    }
}
