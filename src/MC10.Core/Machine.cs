using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MC10.Core
{
    public enum StopReason { None, Breakpoint, Watchpoint, IllegalOpcode, Budget, Reached }

    /// <summary>
    /// One executed instruction, kept in the history ring for the debugger. The registers are the values the
    /// instruction saw (before it ran). Only the instruction bytes are stored; the text is decoded on demand,
    /// so recording history costs no string formatting while the machine runs.
    /// </summary>
    public readonly struct HistoryEntry
    {
        public readonly long Cycle;
        public readonly ushort Pc;
        public readonly byte A, B, Cc;
        public readonly ushort X, Sp;
        public readonly byte Op0, Op1, Op2;

        public HistoryEntry(long cycle, ushort pc, byte a, byte b, ushort x, ushort sp, byte cc, byte op0, byte op1, byte op2)
        {
            Cycle = cycle; Pc = pc; A = a; B = b; X = x; Sp = sp; Cc = cc; Op0 = op0; Op1 = op1; Op2 = op2;
        }

        /// <summary>Disassembly of this entry, optionally with names for addresses.</summary>
        public string Describe(Func<ushort, string> symbol = null, Func<ushort, string> immSymbol = null)
        {
            byte o0 = Op0, o1 = Op1, o2 = Op2;
            ushort pc = Pc;
            Func<ushort, byte> read = addr =>
            {
                int d = (ushort)(addr - pc);
                return d == 0 ? o0 : d == 1 ? o1 : o2;
            };
            return Disassembler.Disassemble(read, pc, out int length, symbol, immSymbol);
        }

        public string Text => Describe();
    }

    /// <summary>One line of the execution profile: a routine and how many instructions ran inside it.</summary>
    public readonly struct ProfileEntry
    {
        public readonly ushort Start;
        public readonly string Name;
        public readonly long Count;
        public ProfileEntry(ushort start, string name, long count) { Start = start; Name = name; Count = count; }
    }

    /// <summary>
    /// The whole machine: CPU, memory map, ROM, RAM, screen, keyboard and sound.
    /// Also owns the debugging hooks (breakpoints, watchpoints, trace, history, coverage, profile).
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

        /// <summary>Enabled breakpoints. A breakpoint in <see cref="DisabledBreakpoints"/> is kept but does not stop.</summary>
        public readonly HashSet<ushort> Breakpoints = new HashSet<ushort>();
        public readonly HashSet<ushort> DisabledBreakpoints = new HashSet<ushort>();
        /// <summary>Optional condition per breakpoint address, e.g. "A==0D" (see <see cref="BreakCondition"/>).</summary>
        public readonly Dictionary<ushort, string> BreakConditions = new Dictionary<ushort, string>();
        /// <summary>How many times each breakpoint stopped the machine.</summary>
        public readonly Dictionary<ushort, int> BreakHits = new Dictionary<ushort, int>();

        /// <summary>Names loaded by the user (symbol file). These win over automatic names.</summary>
        public readonly Dictionary<ushort, string> Symbols = new Dictionary<ushort, string>();
        /// <summary>Names entered by hand in the debugger; saved separately so they survive a new symbols file.</summary>
        public readonly Dictionary<ushort, string> UserSymbols = new Dictionary<ushort, string>();
        /// <summary>Names produced by <see cref="CodeAnalyzer"/> for the loaded ROM.</summary>
        public readonly Dictionary<ushort, string> AutoSymbols = new Dictionary<ushort, string>();
        /// <summary>Incremented whenever any name changes, so views can refresh cheaply.</summary>
        public int SymbolVersion { get; private set; }
        public Analysis Analysis { get; private set; }
        public TextWriter Trace { get; set; }
        public StopReason LastStop { get; private set; }
        public ushort LastWatchAddress { get; private set; }

        readonly Queue<HistoryEntry> history = new Queue<HistoryEntry>();
        public int HistoryLimit { get; set; } = 512;
        bool watchFired;
        int callDepth;

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

        /// <summary>Cycles in one display frame (the keyboard state machine advances once per frame).</summary>
        public long FrameCycles => Math.Max(1000, (long)(Config.ClockHz / 60.0));

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
        /// <summary>Record executed addresses, instruction counts and indirect call/jump targets while running.</summary>
        public bool RecordCoverage = true;
        public readonly bool[] Executed = new bool[65536];
        /// <summary>Instructions executed at each address since the counters were cleared (the profile).</summary>
        public readonly int[] ExecCount = new int[65536];
        public readonly HashSet<ushort> RuntimeTargets = new HashSet<ushort>();

        public int CoveredInstructions { get { int n = 0; foreach (bool b in Executed) if (b) n++; return n; } }

        public void ClearCoverage()
        {
            Array.Clear(Executed, 0, Executed.Length);
            Array.Clear(ExecCount, 0, ExecCount.Length);
            RuntimeTargets.Clear();
        }

        /// <summary>Clear only the profile counters (coverage is kept).</summary>
        public void ClearProfile() => Array.Clear(ExecCount, 0, ExecCount.Length);

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
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
        /// Queue BASIC source (or any text) as key presses. Returns the number of characters that have no key.
        /// The text is fed to the keyboard one key at a time, so any length works.
        /// </summary>
        public int TypeText(string text) => Keyboard.TypeText(text);

        // ------------------------------------------------------------ symbols
        ushort[] routineIndex;
        Dictionary<string, ushort> nameIndex;

        void SymbolsChanged()
        {
            routineIndex = null;
            nameIndex = null;
            SymbolVersion++;
        }

        public void LoadSymbols(string path)
        {
            foreach (var kv in ParseSymbolFile(path)) Symbols[kv.Key] = kv.Value;
            SymbolsChanged();
        }

        /// <summary>Load hand-entered names (they also go into <see cref="Symbols"/>).</summary>
        public void LoadUserSymbols(string path)
        {
            foreach (var kv in ParseSymbolFile(path))
            {
                Symbols[kv.Key] = kv.Value;
                UserSymbols[kv.Key] = kv.Value;
            }
            SymbolsChanged();
        }

        static IEnumerable<KeyValuePair<ushort, string>> ParseSymbolFile(string path)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                string addrText = parts[1].TrimStart('$');
                if (addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) addrText = addrText.Substring(2);
                if (ushort.TryParse(addrText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort a))
                    yield return new KeyValuePair<ushort, string>(a, parts[0]);
            }
        }

        /// <summary>Give an address a name (an empty name removes the user's name). Used by the debugger's Rename command.</summary>
        public void SetSymbol(ushort addr, string name)
        {
            name = (name ?? "").Trim().Replace(' ', '_');
            if (name.Length == 0) { Symbols.Remove(addr); UserSymbols.Remove(addr); }
            else { Symbols[addr] = name; UserSymbols[addr] = name; }
            SymbolsChanged();
        }

        public void SaveUserSymbols(string path)
        {
            File.WriteAllLines(path, new[] { "; Names entered by hand in the debugger (NAME $ADDR)" }
                .Concat(UserSymbols.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} ${kv.Key:X4}")));
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
            if (Rom != null)
            {
                Analysis = CodeAnalyzer.Analyze(Rom.Data, Rom.Base, true, RuntimeTargets);
                foreach (var kv in Analysis.Labels) AutoSymbols[kv.Key] = kv.Value;
            }
            SymbolsChanged();
        }

        static bool IsRoutineName(string name) =>
            !name.StartsWith("loc_", StringComparison.Ordinal) && !name.StartsWith("data_", StringComparison.Ordinal) &&
            !name.StartsWith("tbl_", StringComparison.Ordinal);

        ushort[] RoutineIndex()
        {
            if (routineIndex != null) return routineIndex;
            var set = new SortedSet<ushort>();
            if (Analysis != null)
            {
                foreach (ushort a in Analysis.Subroutines) set.Add(a);
                foreach (var kv in AutoSymbols)
                    if (kv.Value == "reset" || kv.Value.EndsWith("_isr", StringComparison.Ordinal)) set.Add(kv.Key);
            }
            if (Rom != null)
                foreach (var kv in Symbols)
                    if (kv.Key >= Rom.Base && kv.Key < Rom.Base + Rom.Data.Length && IsRoutineName(kv.Value)) set.Add(kv.Key);
            routineIndex = set.ToArray();
            return routineIndex;
        }

        /// <summary>The start of the routine that contains <paramref name="addr"/> (ROM only), or null.</summary>
        public ushort? RoutineStartFor(ushort addr)
        {
            if (Rom == null || addr < Rom.Base || addr >= Rom.Base + Rom.Data.Length) return null;
            ushort[] idx = RoutineIndex();
            int lo = 0, hi = idx.Length - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (idx[mid] <= addr) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans >= 0 ? idx[ans] : (ushort?)null;
        }

        /// <summary>"name" for a labelled address, "name+offset" inside a routine, or null.</summary>
        public string DescribeAddress(ushort addr)
        {
            string s = CodeSymbolFor(addr);
            if (s != null) return s;
            ushort? r = RoutineStartFor(addr);
            if (!r.HasValue) return null;
            string n = CodeSymbolFor(r.Value);
            return n == null ? null : $"{n}+{addr - r.Value:X}";
        }

        void EnsureNames()
        {
            if (nameIndex != null) return;
            nameIndex = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in AutoSymbols) nameIndex[kv.Value] = kv.Key;
            foreach (var kv in Symbols) nameIndex[kv.Value] = kv.Key;       // user names win
        }

        /// <summary>Resolve text typed by the user: a hex address ($E000, 0xE000, E000) or a label name.</summary>
        public bool TryResolve(string text, out ushort addr)
        {
            addr = 0;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            EnsureNames();
            string hex = text.TrimStart('$');
            bool explicitHex = text.StartsWith("$", StringComparison.Ordinal) || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            bool isHex = ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort v);
            if (isHex && (explicitHex || !nameIndex.ContainsKey(text))) { addr = v; return true; }
            return nameIndex.TryGetValue(text, out addr);
        }

        public List<ListingLine> BuildListing()
        {
            if (Analysis == null) return new List<ListingLine>();
            string title = $"TRS-80 MC-10 ROM disassembly   CRC32 {Rom.Crc32():X8}";
            return Listing.Build(Analysis, SymbolFor, CodeSymbolFor, title, Symbols, DescribeAddress);
        }

        public void SaveSymbols(string path)
        {
            var all = new SortedDictionary<ushort, string>();
            foreach (var kv in AutoSymbols) all[kv.Key] = kv.Value;
            foreach (var kv in Symbols) all[kv.Key] = kv.Value;
            File.WriteAllLines(path, all.Select(kv => $"{kv.Value} ${kv.Key:X4}"));
        }

        /// <summary>
        /// Executed instructions per routine since the profile was cleared, busiest first. Code outside the
        /// ROM (BASIC machine-language programs) is grouped by 256-byte page.
        /// </summary>
        public List<ProfileEntry> BuildProfile()
        {
            var totals = new Dictionary<int, long>();
            for (int a = 0; a < ExecCount.Length; a++)
            {
                int n = ExecCount[a];
                if (n == 0) continue;
                ushort? r = RoutineStartFor((ushort)a);
                int key = r.HasValue ? r.Value : -(a >> 8) - 1;          // negative keys = RAM pages
                totals[key] = totals.TryGetValue(key, out long t) ? t + n : n;
            }
            var list = new List<ProfileEntry>();
            foreach (var kv in totals)
            {
                if (kv.Key >= 0)
                {
                    ushort start = (ushort)kv.Key;
                    list.Add(new ProfileEntry(start, CodeSymbolFor(start) ?? $"${start:X4}", kv.Value));
                }
                else
                {
                    ushort page = (ushort)((-kv.Key - 1) << 8);
                    list.Add(new ProfileEntry(page, $"RAM code ${page:X4}-${page + 0xFF:X4}", kv.Value));
                }
            }
            list.Sort((a, b) => b.Count.CompareTo(a.Count));
            return list;
        }

        // ------------------------------------------------------------ breakpoints
        public bool HasBreakpoint(ushort addr) => Breakpoints.Contains(addr) || DisabledBreakpoints.Contains(addr);

        /// <summary>Add a breakpoint, or remove it (and its condition) if one exists. Returns true if one now exists.</summary>
        public bool ToggleBreakpoint(ushort addr)
        {
            if (HasBreakpoint(addr))
            {
                RemoveBreakpoint(addr);
                return false;
            }
            Breakpoints.Add(addr);
            return true;
        }

        public void RemoveBreakpoint(ushort addr)
        {
            Breakpoints.Remove(addr);
            DisabledBreakpoints.Remove(addr);
            BreakConditions.Remove(addr);
            BreakHits.Remove(addr);
        }

        public void SetBreakpointEnabled(ushort addr, bool enabled)
        {
            if (!HasBreakpoint(addr)) return;
            if (enabled) { DisabledBreakpoints.Remove(addr); Breakpoints.Add(addr); }
            else { Breakpoints.Remove(addr); DisabledBreakpoints.Add(addr); }
        }

        public void ClearBreakpoints()
        {
            Breakpoints.Clear();
            DisabledBreakpoints.Clear();
            BreakConditions.Clear();
            BreakHits.Clear();
        }

        /// <summary>All breakpoint addresses, enabled or not, in address order.</summary>
        public List<ushort> AllBreakpoints() => Breakpoints.Concat(DisabledBreakpoints).OrderBy(a => a).ToList();

        bool ConditionMet(ushort pc)
        {
            if (BreakConditions.TryGetValue(pc, out string condition) && !string.IsNullOrWhiteSpace(condition))
            {
                // An invalid condition (null) stops, so the typing mistake is noticed rather than ignored.
                if (BreakCondition.Evaluate(condition, this) == false) return false;
            }
            BreakHits[pc] = BreakHits.TryGetValue(pc, out int hits) ? hits + 1 : 1;
            return true;
        }

        /// <summary>Hardware reset: CPU, timers and devices reset; RAM and ROM are preserved.</summary>
        public void Reset()
        {
            Cpu.Reset();
            Keyboard.Clear();
            VideoControl.Write(0, 0x10);
            history.Clear();
            callDepth = 0;
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
            if (!ignoreBreakpoint && Breakpoints.Contains(pc) && !Cpu.Halted && ConditionMet(pc))
            {
                LastStop = StopReason.Breakpoint;
                return LastStop;
            }

            // State before the instruction: trace and history show what the instruction saw.
            byte a = Cpu.A, b = Cpu.B, cc = (byte)(Cpu.CC | 0xC0);
            ushort x = Cpu.X, sp = Cpu.SP;
            long cycleBefore = Cpu.Cycles, instructionsBefore = Cpu.Instructions, illegalBefore = Cpu.IllegalCount;
            byte opcode = Bus.Peek(pc);
            byte op1 = 0, op2 = 0;
            if (HistoryLimit > 0)
            {
                op1 = Bus.Peek((ushort)(pc + 1));
                op2 = Bus.Peek((ushort)(pc + 2));
            }
            string traceText = null, traceBytes = null;
            if (Trace != null)
            {
                traceText = Disassembler.Disassemble(Bus.Peek, pc, out int len, SymbolFor, CodeSymbolFor);
                traceBytes = Disassembler.HexBytes(Bus.Peek, pc, len);
            }

            watchFired = false;
            Cpu.Step();
            // False when the CPU took an interrupt or idled in WAI instead of running the instruction at pc.
            bool executed = Cpu.Instructions != instructionsBefore;

            if (executed)
            {
                if (RecordCoverage)
                {
                    Executed[pc] = true;
                    if (ExecCount[pc] < int.MaxValue) ExecCount[pc]++;
                    // Indirect JMP n,X / JSR n,X / JSR dir: remember where they really went (analysis entry points).
                    if ((opcode == 0x6E || opcode == 0xAD || opcode == 0x9D) && Rom != null
                        && Cpu.PC >= Rom.Base && Cpu.PC < Rom.Base + Rom.Data.Length)
                        RuntimeTargets.Add(Cpu.PC);
                }
                if (Trace != null) WriteTrace(pc, traceBytes, traceText, a, b, x, sp, cc, cycleBefore);
                if (HistoryLimit > 0)
                {
                    history.Enqueue(new HistoryEntry(cycleBefore, pc, a, b, x, sp, cc, opcode, op1, op2));
                    while (history.Count > HistoryLimit) history.Dequeue();
                }
                // Call depth is only used to indent the trace. BASIC often abandons return addresses by
                // reloading the stack pointer (errors, back to the OK prompt), so that resets the depth.
                if (opcode == 0x8D || opcode == 0xAD || opcode == 0x9D || opcode == 0xBD) callDepth++;
                else if ((opcode == 0x39 || opcode == 0x3B) && callDepth > 0) callDepth--;
                else if (opcode == 0x35 || opcode == 0x8E || opcode == 0x9E || opcode == 0xAE || opcode == 0xBE) callDepth = 0;
            }
            else if (Cpu.PC != pc)
            {
                // The CPU took an interrupt instead of running the instruction at pc.
                callDepth++;
                if (Trace != null) Trace.WriteLine($"{pc:X4}  ; interrupt taken -> {Cpu.PC:X4} {SymbolFor(Cpu.PC)}");
            }

            if (Cpu.IllegalCount != illegalBefore) LastStop = StopReason.IllegalOpcode;
            else if (watchFired) LastStop = StopReason.Watchpoint;
            else LastStop = StopReason.None;
            return LastStop;
        }

        bool IsRoutineStart(ushort pc)
        {
            if (Analysis != null && Analysis.Subroutines.Contains(pc)) return true;
            return Symbols.TryGetValue(pc, out string n) && IsRoutineName(n);
        }

        void WriteTrace(ushort pc, string bytes, string text, byte a, byte b, ushort x, ushort sp, byte cc, long cycle)
        {
            if (IsRoutineStart(pc))
                Trace.WriteLine($"      ; ---- {CodeSymbolFor(pc) ?? "$" + pc.ToString("X4")} ----");
            string indent = new string(' ', Math.Min(callDepth, 24) * 2);
            Trace.WriteLine($"{pc:X4}  {bytes,-8}  {indent}{text,-26}  A={a:X2} B={b:X2} X={x:X4} SP={sp:X4} CC={cc:X2}  cyc={cycle}");
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
            long nextKey = Cpu.Cycles + FrameCycles;
            bool first = true;
            while (Cpu.Cycles < end)
            {
                if (!first && Cpu.PC == target) { LastStop = StopReason.None; return StopReason.Reached; }
                StopReason r = Step(ignoreBreakpoint: first);
                first = false;
                if (r != StopReason.None) return r;
                if (Cpu.Cycles >= nextKey) { Keyboard.Advance(); nextKey += FrameCycles; }
            }
            LastStop = StopReason.Budget;
            return LastStop;
        }

        /// <summary>
        /// Step over one instruction: a subroutine call (JSR/BSR) runs to its return.
        /// The keyboard keeps advancing while the call runs, so a routine that waits for a key can finish.
        /// Returns <see cref="StopReason.None"/> when the step completed normally.
        /// </summary>
        public StopReason StepOver()
        {
            byte op = Bus.Peek(Cpu.PC);
            var info = Opcodes.Table[op];
            if (info.Kind != Kind.Bsr && info.Kind != Kind.Jsr)
                return Step(ignoreBreakpoint: true);
            Disassembler.Disassemble(Bus.Peek, Cpu.PC, out int len, null);
            ushort ret = (ushort)(Cpu.PC + len);
            ushort sp = Cpu.SP;
            StopReason r = Step(ignoreBreakpoint: true);
            if (r != StopReason.None) return r;
            long end = Cpu.Cycles + 50_000_000;
            long nextKey = Cpu.Cycles + FrameCycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.PC == ret && Cpu.SP >= sp) return StopReason.None;
                r = Step();
                if (r != StopReason.None) return r;
                if (Cpu.Cycles >= nextKey) { Keyboard.Advance(); nextKey += FrameCycles; }
            }
            LastStop = StopReason.Budget;
            return LastStop;
        }

        /// <summary>
        /// Run until the current subroutine returns (an RTS/RTI that leaves the stack above where it started).
        /// Nested calls run to completion. Stops early on breakpoint, watchpoint or illegal opcode.
        /// </summary>
        public StopReason StepOut(long maxCycles = 50_000_000)
        {
            ushort startSp = Cpu.SP;
            long end = Cpu.Cycles + maxCycles;
            long nextKey = Cpu.Cycles + FrameCycles;
            bool first = true;
            while (Cpu.Cycles < end)
            {
                byte op = Bus.Peek(Cpu.PC);
                StopReason r = Step(ignoreBreakpoint: first);
                first = false;
                if (r != StopReason.None) return r;
                if ((op == 0x39 || op == 0x3B) && Cpu.SP > startSp) return StopReason.None;
                if (Cpu.Cycles >= nextKey) { Keyboard.Advance(); nextKey += FrameCycles; }
            }
            LastStop = StopReason.Budget;
            return LastStop;
        }

        public IReadOnlyCollection<HistoryEntry> History => history;
    }
}
