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
        public readonly Dictionary<ushort, string> Symbols = new Dictionary<ushort, string>();
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
            Cpu.Reset();
        }

        public void LoadRomFile(string path, int rawBase = -1)
        {
            int b = rawBase >= 0 ? rawBase : Config.RomBase;
            LoadRom(ImageLoader.Load(path, b));
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

        public string SymbolFor(ushort addr) => Symbols.TryGetValue(addr, out var s) ? s : null;

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
                text = Disassembler.Disassemble(Bus.Read, pc, out int len, SymbolFor);
                if (Trace != null)
                {
                    string bytes = Disassembler.HexBytes(Bus.Read, pc, len);
                    Trace.WriteLine(
                        $"{pc:X4}  {bytes,-8}  {text,-24}  A={Cpu.A:X2} B={Cpu.B:X2} X={Cpu.X:X4} SP={Cpu.SP:X4} CC={Cpu.CC | 0xC0:X2}  cyc={Cpu.Cycles}");
                }
            }

            long illegalBefore = Cpu.IllegalCount;
            watchFired = false;
            Cpu.Step();

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

        public IReadOnlyCollection<HistoryEntry> History => history;

    }
}
