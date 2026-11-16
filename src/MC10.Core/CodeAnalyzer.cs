using System;
using System.Collections.Generic;
using System.Linq;

namespace MC10.Core
{
    /// <summary>Result of static analysis of a ROM image: which bytes are code, and the labels found.</summary>
    public sealed class Analysis
    {
        public int Base { get; }
        public int Size { get; }
        public byte[] Bytes { get; }
        /// <summary>First byte of each decoded instruction.</summary>
        public bool[] CodeStart { get; }
        /// <summary>Any byte that belongs to a decoded instruction.</summary>
        public bool[] CodeByte { get; }
        /// <summary>Bytes belonging to a detected table of code pointers.</summary>
        public bool[] PointerTable { get; }
        public Dictionary<ushort, string> Labels { get; } = new Dictionary<ushort, string>();
        public HashSet<ushort> Subroutines { get; } = new HashSet<ushort>();
        /// <summary>Target -> addresses of the JSR/BSR instructions that call it.</summary>
        public Dictionary<ushort, List<ushort>> Callers { get; } = new Dictionary<ushort, List<ushort>>();
        /// <summary>Target -> addresses of branches/jumps/data references to it.</summary>
        public Dictionary<ushort, List<ushort>> Refs { get; } = new Dictionary<ushort, List<ushort>>();
        /// <summary>Vector address -> name, e.g. $FFFE -> "reset".</summary>
        public Dictionary<ushort, string> VectorNames { get; } = new Dictionary<ushort, string>();

        readonly Dictionary<ushort, int> priority = new Dictionary<ushort, int>();

        public Analysis(byte[] bytes, int baseAddr)
        {
            Bytes = bytes;
            Base = baseAddr;
            Size = bytes.Length;
            CodeStart = new bool[Size];
            CodeByte = new bool[Size];
            PointerTable = new bool[Size];
        }

        public bool Contains(int addr) => addr >= Base && addr < Base + Size;
        public byte At(int addr) => Bytes[addr - Base];
        public bool IsCodeStart(ushort addr) => Contains(addr) && CodeStart[addr - Base];

        public int CodeByteCount => CodeByte.Count(b => b);
        public int InstructionCount => CodeStart.Count(b => b);

        internal void SetLabel(ushort addr, string name, int prio)
        {
            if (!priority.TryGetValue(addr, out int p) || prio < p)
            {
                Labels[addr] = name;
                priority[addr] = prio;
            }
        }

        internal static void AddRef(Dictionary<ushort, List<ushort>> map, ushort target, ushort from)
        {
            if (!map.TryGetValue(target, out var list)) map[target] = list = new List<ushort>();
            if (!list.Contains(from)) list.Add(from);
        }
    }

    /// <summary>
    /// Recursive-descent disassembly of an MC6803 ROM. Starting from the interrupt vectors it follows
    /// branches, jumps and calls, then names what it found: vectors, subroutines (sub_XXXX), branch/jump
    /// targets (loc_XXXX), data referenced by LDX/LDD #imm or absolute operands (data_XXXX), and
    /// tables of code pointers (tbl_XXXX with fn_XXXX entries).
    /// Limits: indirect jumps (JMP n,X) and calls through RAM are not followed, so code reached only
    /// that way is shown as data unless it appears in a pointer table.
    /// </summary>
    public static class CodeAnalyzer
    {
        // Priority: lower wins.
        const int PVector = 0, PSub = 1, PTable = 2, PJump = 3, PBranch = 4, PData = 5;

        static readonly string[] VectorNames =
            { "sci_isr", "tof_isr", "oci_isr", "ici_isr", "irq_isr", "swi_isr", "nmi_isr", "reset" };

        public static Analysis Analyze(byte[] rom, int baseAddr, bool followPointerTables = true)
        {
            var an = new Analysis(rom, baseAddr);
            var work = new Queue<ushort>();
            var subTargets = new HashSet<ushort>();
            var jumpTargets = new HashSet<ushort>();
            var branchTargets = new HashSet<ushort>();
            var dataRefs = new HashSet<ushort>();
            var external = new HashSet<ushort>();
            var vectorTargets = new Dictionary<ushort, string>();

            // 1. Interrupt vectors. Reset first so shared handlers get the reset name.
            for (int i = 7; i >= 0; i--)
            {
                ushort va = (ushort)(0xFFF0 + 2 * i);
                if (!an.Contains(va) || !an.Contains(va + 1)) continue;
                ushort target = (ushort)((an.At(va) << 8) | an.At(va + 1));
                an.VectorNames[va] = "vec_" + VectorNames[i].Replace("_isr", "");
                if (!an.Contains(target)) continue;
                if (!vectorTargets.ContainsKey(target)) vectorTargets[target] = VectorNames[i];
                work.Enqueue(target);
            }

            Flow(an, work, subTargets, jumpTargets, branchTargets, dataRefs, external);

            // 2. Tables of code pointers found in what is left over.
            if (followPointerTables)
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    var found = FindPointerTables(an);
                    if (found.Count == 0) break;
                    foreach (var t in found)
                    {
                        an.SetLabel(t.Start, "tbl_" + t.Start.ToString("X4"), PTable);
                        foreach (ushort target in t.Targets)
                        {
                            subTargets.Add(target);
                            Analysis.AddRef(an.Refs, target, t.Start);
                            work.Enqueue(target);
                        }
                    }
                    Flow(an, work, subTargets, jumpTargets, branchTargets, dataRefs, external);
                }
            }

            // 3. Names.
            foreach (var kv in vectorTargets)
                an.SetLabel(kv.Key, kv.Value, PVector);
            foreach (ushort a in subTargets)
            {
                if (an.Contains(a)) { an.Subroutines.Add(a); an.SetLabel(a, "sub_" + a.ToString("X4"), PSub); }
            }
            foreach (ushort a in jumpTargets)
                if (an.Contains(a)) an.SetLabel(a, "loc_" + a.ToString("X4"), PJump);
            foreach (ushort a in branchTargets)
                if (an.Contains(a)) an.SetLabel(a, "loc_" + a.ToString("X4"), PBranch);
            foreach (ushort a in dataRefs)
                if (an.Contains(a) && !an.IsCodeStart(a)) an.SetLabel(a, "data_" + a.ToString("X4"), PData);
            foreach (ushort a in external)
                an.SetLabel(a, "ramfn_" + a.ToString("X4"), PSub);
            foreach (var kv in an.Labels.Where(l => l.Value.StartsWith("sub_") && an.Callers.ContainsKey(l.Key) == false
                                                      && an.Refs.TryGetValue(l.Key, out var r) && r.Count > 0).ToList())
                an.Labels[kv.Key] = "fn_" + kv.Key.ToString("X4");     // reached only via a pointer table
            return an;
        }

        // ------------------------------------------------------------ control flow
        static void Flow(Analysis an, Queue<ushort> work, HashSet<ushort> subs, HashSet<ushort> jumps,
                         HashSet<ushort> branches, HashSet<ushort> dataRefs, HashSet<ushort> external)
        {
            while (work.Count > 0)
            {
                int addr = work.Dequeue();
                while (an.Contains(addr))
                {
                    if (an.CodeStart[addr - an.Base]) break;           // already decoded
                    byte op = an.At(addr);
                    OpInfo info = Opcodes.Table[op];
                    if (info.Kind == Kind.Illegal) break;
                    int len = 1 + Disassembler.OperandBytes(info.Mode);
                    if (!an.Contains(addr + len - 1)) break;
                    bool overlap = false;
                    for (int i = 0; i < len; i++) if (an.CodeByte[addr - an.Base + i]) overlap = true;
                    if (overlap) break;

                    an.CodeStart[addr - an.Base] = true;
                    for (int i = 0; i < len; i++) an.CodeByte[addr - an.Base + i] = true;
                    ushort here = (ushort)addr;
                    bool stop = false;

                    switch (info.Kind)
                    {
                        case Kind.Inh:
                            if ((Inh)info.Sub == Inh.Rts || (Inh)info.Sub == Inh.Rti) stop = true;
                            break;
                        case Kind.Branch:
                        {
                            ushort t = RelTarget(an, addr);
                            if (info.Sub != 1)                         // BRN never branches
                            {
                                AddTarget(an, work, branches, external, t, here, enqueue: true);
                                Analysis.AddRef(an.Refs, t, here);
                            }
                            if (info.Sub == 0) stop = true;            // BRA
                            break;
                        }
                        case Kind.Bsr:
                        {
                            ushort t = RelTarget(an, addr);
                            AddTarget(an, work, subs, external, t, here, enqueue: true);
                            Analysis.AddRef(an.Callers, t, here);
                            break;
                        }
                        case Kind.Jsr:
                            if (info.Mode == Mode.Ext)
                            {
                                ushort t = Word(an, addr + 1);
                                AddTarget(an, work, subs, external, t, here, enqueue: true);
                                Analysis.AddRef(an.Callers, t, here);
                            }
                            break;
                        case Kind.UnaryMem:
                            if ((Unop)info.Sub == Unop.Jmp)
                            {
                                if (info.Mode == Mode.Ext)
                                {
                                    ushort t = Word(an, addr + 1);
                                    AddTarget(an, work, jumps, external, t, here, enqueue: true);
                                    Analysis.AddRef(an.Refs, t, here);
                                }
                                stop = true;                           // JMP never falls through
                            }
                            break;
                        case Kind.Alu16:
                            if (info.Mode == Mode.Imm16)
                            {
                                ushort v = Word(an, addr + 1);
                                Op16 op16 = (Op16)info.Sub;
                                if ((op16 == Op16.Ldx || op16 == Op16.Ldd) && an.Contains(v))
                                {
                                    dataRefs.Add(v);
                                    Analysis.AddRef(an.Refs, v, here);
                                }
                            }
                            else if (info.Mode == Mode.Ext) NoteDataRef(an, dataRefs, Word(an, addr + 1), here);
                            break;
                        case Kind.Alu8:
                        case Kind.St8:
                        case Kind.St16:
                            if (info.Mode == Mode.Ext) NoteDataRef(an, dataRefs, Word(an, addr + 1), here);
                            break;
                    }
                    addr += len;
                    if (stop) break;
                }
            }
        }

        static void NoteDataRef(Analysis an, HashSet<ushort> dataRefs, ushort target, ushort from)
        {
            if (!an.Contains(target)) return;
            dataRefs.Add(target);
            Analysis.AddRef(an.Refs, target, from);
        }

        static void AddTarget(Analysis an, Queue<ushort> work, HashSet<ushort> set, HashSet<ushort> external,
                              ushort target, ushort from, bool enqueue)
        {
            if (an.Contains(target))
            {
                set.Add(target);
                if (enqueue) work.Enqueue(target);
            }
            else if (target >= 0x0020)                            // outside the ROM: e.g. a RAM hook such as JSR $42A9
            {
                external.Add(target);
            }
        }

        static ushort RelTarget(Analysis an, int addr)
        {
            sbyte off = (sbyte)an.At(addr + 1);
            return (ushort)(addr + 2 + off);
        }

        static ushort Word(Analysis an, int addr) => (ushort)((an.At(addr) << 8) | an.At(addr + 1));

        // ------------------------------------------------------------ pointer tables
        sealed class Table { public ushort Start; public List<ushort> Targets = new List<ushort>(); }

        static List<Table> FindPointerTables(Analysis an)
        {
            var result = new List<Table>();
            int end = an.Base + an.Size;
            int i = an.Base;
            while (i + 1 < end)
            {
                if (i >= 0xFFF0 || !Free(an, i) || !Free(an, i + 1)) { i++; continue; }
                var t = new Table { Start = (ushort)i };
                int j = i;
                while (j + 1 < end && j < 0xFFF0 && Free(an, j) && Free(an, j + 1))
                {
                    ushort w = Word(an, j);
                    if (!LooksLikeCode(an, w)) break;
                    t.Targets.Add(w);
                    j += 2;
                }
                if (t.Targets.Count >= 3)
                {
                    for (int k = i; k < j; k++) an.PointerTable[k - an.Base] = true;
                    result.Add(t);
                    i = j;
                }
                else i++;
            }
            return result;
        }

        static bool Free(Analysis an, int addr) => !an.CodeByte[addr - an.Base] && !an.PointerTable[addr - an.Base];

        /// <summary>
        /// Dry-run decode from <paramref name="target"/>: accepts only an address that is already a decoded
        /// instruction, or that decodes cleanly for a few instructions without overlapping existing code.
        /// </summary>
        static bool LooksLikeCode(Analysis an, ushort target)
        {
            if (!an.Contains(target)) return false;
            if (an.CodeStart[target - an.Base]) return true;
            if (an.CodeByte[target - an.Base]) return false;           // lands inside an instruction
            int addr = target;
            for (int n = 0; n < 6; n++)
            {
                if (!an.Contains(addr)) return false;
                OpInfo info = Opcodes.Table[an.At(addr)];
                if (info.Kind == Kind.Illegal) return false;
                int len = 1 + Disassembler.OperandBytes(info.Mode);
                if (!an.Contains(addr + len - 1)) return false;
                for (int k = 0; k < len; k++) if (an.CodeByte[addr - an.Base + k]) return false;
                bool terminator = info.Kind == Kind.Inh && ((Inh)info.Sub == Inh.Rts || (Inh)info.Sub == Inh.Rti)
                                  || info.Kind == Kind.UnaryMem && (Unop)info.Sub == Unop.Jmp
                                  || info.Kind == Kind.Branch && info.Sub == 0;
                if (terminator) return n >= 1 || info.Kind == Kind.Inh;
                addr += len;
            }
            return true;
        }
    }
}
