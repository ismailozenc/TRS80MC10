using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MC10.Core
{
    public enum StopReason { None, Breakpoint, Watchpoint, IllegalOpcode, Budget }

    /// <summary>One executed instruction, kept in the history ring for the debugger.</summary>
    public readonly struct HistoryEntry
    {
        public readonly long Cycle;
        public readonly ushort Pc;
        public readonly byte A, B, Cc;
        public readonly ushort X, Sp;
        public readonly string Text;
        public HistoryEntry(long cycle, ushort pc, byte a, byte b, ushort x, ushort sp, byte cc, string text)
        {
            Cycle = cycle; Pc = pc; A = a; B = b; X = x; Sp = sp; Cc = cc; Text = text;
        }
    }

    /// <summary>
    /// The whole machine: CPU, memory map, ROM, RAM, screen, keyboard and sound.
    /// Also owns the debugging hooks (breakpoints, watchpoints, trace, history).
    /// </summary>
    public sealed class Machine
    {
        public MachineConfig Config { get; }
        public Bus Bus { get; }
        public Cpu6803 Cpu { get; }
        public Eprom Rom { get; private set; }
        public Video Video { get; }
        public MatrixKeyboard Keyboard { get; }
        public SoundDevice Sound { get; }
        public VideoControlDevice VideoControl { get; }

        public readonly HashSet<ushort> Breakpoints = new HashSet<ushort>();
        /// <summary>Names loaded by the user (symbol file). These win over automatic names.</summary>
        public readonly Dictionary<ushort, string> Symbols = new Dictionary<ushort, string>();
        /// <summary>Names produced by <see cref="CodeAnalyzer"/> for the loaded ROM.</summary>
        public readonly Dictionary<ushort, string> AutoSymbols = new Dictionary<ushort, string>();
        public Analysis Analysis { get; private set; }
        public TextWriter Trace { get; set; }
        public StopReason LastStop { get; private set; }
        public ushort LastWatchAddress { get; private set; }

        readonly Queue<HistoryEntry> history = new Queue<HistoryEntry>();
        public int HistoryLimit { get; set; } = 512;
        bool watchFired;

        public Machine(MachineConfig config = null)
        {
            Config = config ?? MachineConfig.Default();
            Bus = new Bus();
            Bus.MapRam(Config.InternalRamBase, Config.InternalRamSize);
            Bus.MapRam(Config.RamBase, Config.RamSize);

            Cpu = new Cpu6803(Bus);
            // Cycle count is read through a delegate so devices stay independent of the CPU.
            Sound = new SoundDevice(() => Cpu.Cycles);
            Keyboard = new MatrixKeyboard(() => Cpu.Io[2]);
            Cpu.Port2Read = Keyboard.ReadPort2;
            VideoControl = new VideoControlDevice();
            Bus.MapDevice(0xBFFF, Keyboard);
            Bus.MapDevice((ushort)Config.SoundRegister, Sound);
            Bus.MapDevice((ushort)Config.VideoControl, VideoControl);
            Video = new Video(Bus, Config.VideoBase, VideoControl);

            Bus.WatchHook = (addr, isWrite, value) =>
            {
                if (!isWrite) return;               // only writes stop execution
                watchFired = true;
                LastWatchAddress = addr;
            };
        }

        // ------------------------------------------------------------ setup
        /// <summary>Install a ROM image covering the configured window. Segments outside the window are ignored.</summary>
        public void LoadRom(List<ImageLoader.Segment> segments, EpromMode? mode = null)
        {
            int romEnd = Config.RomBase + Config.RomSize;
            foreach (int dev in new[] { Config.SoundRegister, Config.VideoControl })
                if (dev >= Config.RomBase && dev < romEnd)
                    throw new InvalidOperationException(
                        $"Device address ${dev:X4} lies inside the ROM window ${Config.RomBase:X4}-${romEnd - 1:X4}. Change the config.");

            byte[] image = ImageLoader.Flatten(segments, Config.RomBase, Config.RomSize);
            Rom = new Eprom(Config.RomBase, image, mode ?? Config.RomMode);
            Bus.MapRom(Rom);
            Reanalyze();
            Cpu.Reset();
        }

        public void LoadRomFile(string path, int rawBase = -1)
        {
            int b = rawBase >= 0 ? rawBase : Config.RomBase;
            LoadRom(ImageLoader.Load(path, b));
        }

        // ---- dynamic analysis: what the running firmware really executed
        /// <summary>Record executed addresses and indirect call/jump targets while stepping.</summary>
        public bool RecordCoverage = true;
        public readonly bool[] Executed = new bool[65536];
        public readonly HashSet<ushort> RuntimeTargets = new HashSet<ushort>();

        public int CoveredInstructions { get { int n = 0; foreach (bool b in Executed) if (b) n++; return n; } }

        public void ClearCoverage()
        {
            Array.Clear(Executed, 0, Executed.Length);
            RuntimeTargets.Clear();
        }

        /// <summary>Re-run the static analysis using the call targets observed at run time.</summary>
        public void ReanalyzeWithRunData() => Reanalyze();

        public void SaveCoverage(string path)
        {
            var lines = new List<string> { "; executed instruction address ranges" };
            int i = 0;
            while (i < 65536)
            {
                if (!Executed[i]) { i++; continue; }
                int s = i;
                while (i < 65536 && Executed[i]) i++;
                lines.Add($"${s:X4} ${i - 1:X4}");
            }
            lines.Add("; runtime targets");
            foreach (ushort t in RuntimeTargets.OrderBy(x => x)) lines.Add($"T ${t:X4}");
            File.WriteAllLines(path, lines);
        }

        /// <summary>Load a .cov (ranges) or .targets (one address per line) file produced by tools/mc10_label.py or SaveCoverage.</summary>
        public void LoadRunData(string path)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.Length == 0) continue;
                string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                bool target = p[0] == "T";
                if (target) p = p.Skip(1).ToArray();
                ushort a, b;
                if (p.Length == 0 || !TryHex(p[0], out a)) continue;
                if (p.Length > 1 && TryHex(p[1], out b) && !target)
                    for (int x = a; x <= b; x++) Executed[x] = true;
                else if (target || Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase))
                    RuntimeTargets.Add(a);
                else Executed[a] = true;
            }
        }

        static bool TryHex(string s, out ushort v)
        {
            s = s.TrimStart('$');
            return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
        }

        /// <summary>Queue BASIC source (or any text) as key presses. Returns the number of characters that have no key.</summary>
        public int TypeText(string text)
        {
            int skipped = 0;
            foreach (char ch in text.Replace("\r\n", "\r").Replace('\n', '\r'))
                if (!Keyboard.TapChar(ch)) skipped++;
            return skipped;
        }

        public void LoadSymbols(string path)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                string addrText = parts[1].TrimStart('$');
                if (addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) addrText = addrText.Substring(2);
                if (ushort.TryParse(addrText, System.Globalization.NumberStyles.HexNumber, null, out ushort a))
                    Symbols[a] = parts[0];
            }
        }

        /// <summary>User or automatic label for a code/data address (no hardware register names).</summary>
        public string CodeSymbolFor(ushort addr)
        {
            if (Symbols.TryGetValue(addr, out var s)) return s;
            return AutoSymbols.TryGetValue(addr, out s) ? s : null;
        }

        /// <summary>Label for any address, including the 6803 registers and the keyboard latch.</summary>
        public string SymbolFor(ushort addr) => CodeSymbolFor(addr) ?? Hardware.NameFor(addr);

        /// <summary>Re-run the static analysis on the loaded ROM and refresh the automatic labels.</summary>
        public void Reanalyze()
        {
            AutoSymbols.Clear();
            Analysis = null;
            if (Rom == null) return;
            Analysis = CodeAnalyzer.Analyze(Rom.Data, Rom.Base, true, RuntimeTargets);
            foreach (var kv in Analysis.Labels) AutoSymbols[kv.Key] = kv.Value;
        }

        public List<ListingLine> BuildListing()
        {
            if (Analysis == null) return new List<ListingLine>();
            string title = $"TRS-80 MC-10 ROM disassembly   CRC32 {Rom.Crc32():X8}";
            return Listing.Build(Analysis, SymbolFor, CodeSymbolFor, title);
        }

        public void SaveSymbols(string path)
        {
            var all = new SortedDictionary<ushort, string>();
            foreach (var kv in AutoSymbols) all[kv.Key] = kv.Value;
            foreach (var kv in Symbols) all[kv.Key] = kv.Value;
            File.WriteAllLines(path, all.Select(kv => $"{kv.Value} ${kv.Key:X4}"));
        }

        /// <summary>Hardware reset: CPU, timers and devices reset; RAM and ROM are preserved.</summary>
        public void Reset()
        {
            Cpu.Reset();
            Keyboard.Clear();
            VideoControl.Write(0, 0x10);
            history.Clear();
            LastStop = StopReason.None;
        }

        // ------------------------------------------------------------ execution
        /// <summary>
        /// Execute one instruction. Breakpoints are checked before the instruction runs,
        /// unless <paramref name="ignoreBreakpoint"/> is set (used when resuming from one).
        /// </summary>
        public StopReason Step(bool ignoreBreakpoint = false)
        {
            ushort pc = Cpu.PC;
            if (!ignoreBreakpoint && Breakpoints.Contains(pc) && !Cpu.Halted)
            {
                LastStop = StopReason.Breakpoint;
                return LastStop;
            }

            string text = null;
            if (Trace != null || HistoryLimit > 0)
            {
                text = Disassembler.Disassemble(Bus.Read, pc, out int len, SymbolFor, CodeSymbolFor);
                if (Trace != null)
                {
                    string bytes = Disassembler.HexBytes(Bus.Read, pc, len);
                    Trace.WriteLine(
                        $"{pc:X4}  {bytes,-8}  {text,-24}  A={Cpu.A:X2} B={Cpu.B:X2} X={Cpu.X:X4} SP={Cpu.SP:X4} CC={Cpu.CC | 0xC0:X2}  cyc={Cpu.Cycles}");
                }
            }

            long illegalBefore = Cpu.IllegalCount;
            watchFired = false;
            byte opcode = 0;
            if (RecordCoverage) { Executed[pc] = true; opcode = Bus.Read(pc); }
            Cpu.Step();
            // Indirect JMP/JSR n,X and JSR dir: remember where they really went.
            if (RecordCoverage && (opcode == 0x6E || opcode == 0xAD || opcode == 0x9D) && Rom != null
                && Cpu.PC >= Rom.Base && Cpu.PC < Rom.Base + Rom.Data.Length)
                RuntimeTargets.Add(Cpu.PC);

            if (HistoryLimit > 0)
            {
                history.Enqueue(new HistoryEntry(Cpu.Cycles, pc, Cpu.A, Cpu.B, Cpu.X, Cpu.SP, (byte)(Cpu.CC | 0xC0), text ?? ""));
                while (history.Count > HistoryLimit) history.Dequeue();
            }

            if (Cpu.IllegalCount != illegalBefore) LastStop = StopReason.IllegalOpcode;
            else if (watchFired) LastStop = StopReason.Watchpoint;
            else LastStop = StopReason.None;
            return LastStop;
        }

        /// <summary>
        /// Run until the cycle budget is used or something stops execution.
        /// The first instruction ignores breakpoints so that resuming from one makes progress.
        /// </summary>
        public StopReason RunCycles(long budget)
        {
            long end = Cpu.Cycles + budget;
            bool first = true;
            while (Cpu.Cycles < end)
            {
                StopReason r = Step(ignoreBreakpoint: first);
                first = false;
                if (r != StopReason.None) return r;
            }
            LastStop = StopReason.Budget;
            return LastStop;
        }

        /// <summary>Run until the program counter reaches <paramref name="target"/> (or another stop).</summary>
        public StopReason RunTo(ushort target, long maxCycles = 50_000_000)
        {
            long end = Cpu.Cycles + maxCycles;
            bool first = true;
            while (Cpu.Cycles < end)
            {
                if (!first && Cpu.PC == target) { LastStop = StopReason.Breakpoint; return LastStop; }
                StopReason r = Step(ignoreBreakpoint: first);
                first = false;
                if (r != StopReason.None) return r;
            }
            LastStop = StopReason.Budget;
            return LastStop;
        }

        /// <summary>
        /// Step over one instruction: a subroutine call (JSR/BSR) runs to its return.
        /// Returns the stop reason; the instruction-level Step is used for everything else.
        /// </summary>
        public StopReason StepOver()
        {
            byte op = Bus.Read(Cpu.PC);
            var info = Opcodes.Table[op];
            if (info.Kind != Kind.Bsr && info.Kind != Kind.Jsr)
                return Step(ignoreBreakpoint: true);
            Disassembler.Disassemble(Bus.Read, Cpu.PC, out int len, null);
            ushort ret = (ushort)(Cpu.PC + len);
            ushort sp = Cpu.SP;
            StopReason r = Step(ignoreBreakpoint: true);
            if (r != StopReason.None) return r;
            long end = Cpu.Cycles + 50_000_000;
            while (Cpu.Cycles < end)
            {
                if (Cpu.PC == ret && Cpu.SP >= sp) return StopReason.Breakpoint;
                r = Step();
                if (r != StopReason.None) return r;
            }
            return StopReason.Budget;
        }

        /// <summary>
        /// Run until the current subroutine returns (an RTS/RTI that leaves the stack above where it started).
        /// Nested calls run to completion. Stops early on breakpoint, watchpoint or illegal opcode.
        /// </summary>
        public StopReason StepOut(long maxCycles = 50_000_000)
        {
            ushort startSp = Cpu.SP;
            long end = Cpu.Cycles + maxCycles;
            bool first = true;
            while (Cpu.Cycles < end)
            {
                byte op = Bus.Read(Cpu.PC);
                StopReason r = Step(ignoreBreakpoint: first);
                first = false;
                if (r != StopReason.None) return r;
                if ((op == 0x39 || op == 0x3B) && Cpu.SP > startSp) return StopReason.None;
            }
            LastStop = StopReason.Budget;
            return LastStop;
        }

        public IReadOnlyCollection<HistoryEntry> History => history;

    }
}
