using System;
using System.Collections.Generic;
using System.Linq;

namespace MC10.Core
{
    /// <summary>What an instruction's operand refers to.</summary>
    public sealed class OperandInfo
    {
        public Mode Mode;
        public int Length;            // instruction length in bytes
        public bool HasAddress;       // a memory operand, or a jump / branch / call target
        public ushort Address;
        public bool IsCodeTarget;     // the address is where execution goes (branch, JMP, JSR, BSR)
        public bool UsesX;            // indexed: the address depends on X
        public int Size;              // bytes of memory read or written: 1, 2, or 0 when there is none
        public bool IsImmediate;
        public int Immediate;
    }

    /// <summary>One row of the debugger's Autos list.</summary>
    public sealed class AutoItem
    {
        public string Name = "";
        public string Value = "";
        public string Detail = "";
        public bool HasAddress;
        public ushort Address;
        public bool IsInstruction;
    }

    /// <summary>
    /// Plain-language explanations of MC6803 instructions, and the "autos": the registers and memory that the
    /// previous and the next instruction use. Everything reads memory through side-effect-free delegates.
    /// </summary>
    public static class InstructionGuide
    {
        static readonly string[] BranchText =
        {
            "always", "never (a 2-byte no-op)",
            "if higher, unsigned > (C=0 and Z=0)", "if lower or same, unsigned <= (C=1 or Z=1)",
            "if carry clear, unsigned >= (C=0)", "if carry set, unsigned < (C=1)",
            "if not equal / not zero (Z=0)", "if equal / zero (Z=1)",
            "if no overflow (V=0)", "if overflow (V=1)",
            "if plus (N=0)", "if minus (N=1)",
            "if greater or equal, signed (N=V)", "if less, signed (N<>V)",
            "if greater, signed (Z=0 and N=V)", "if less or equal, signed (Z=1 or N<>V)",
        };

        // text, flags affected, registers involved
        static readonly Dictionary<Inh, string[]> InhText = new Dictionary<Inh, string[]>
        {
            { Inh.Nop, new[] { "Do nothing", "", "" } },
            { Inh.Tap, new[] { "Copy A into the condition codes", "all, from A", "A,CC" } },
            { Inh.Tpa, new[] { "Copy the condition codes into A", "", "A,CC" } },
            { Inh.Inx, new[] { "Add 1 to X", "Z", "X" } },
            { Inh.Dex, new[] { "Subtract 1 from X", "Z", "X" } },
            { Inh.Clv, new[] { "Clear the overflow flag", "V=0", "CC" } },
            { Inh.Sev, new[] { "Set the overflow flag", "V=1", "CC" } },
            { Inh.Clc, new[] { "Clear the carry flag", "C=0", "CC" } },
            { Inh.Sec, new[] { "Set the carry flag", "C=1", "CC" } },
            { Inh.Cli, new[] { "Enable interrupts (clear the I mask)", "I=0", "CC" } },
            { Inh.Sei, new[] { "Disable interrupts (set the I mask)", "I=1", "CC" } },
            { Inh.Sba, new[] { "Subtract B from A", "N Z V C", "A,B" } },
            { Inh.Cba, new[] { "Compare A with B (computes A - B, keeps only the flags)", "N Z V C", "A,B" } },
            { Inh.Tab, new[] { "Copy A into B", "N Z, V=0", "A,B" } },
            { Inh.Tba, new[] { "Copy B into A", "N Z, V=0", "A,B" } },
            { Inh.Aba, new[] { "Add B to A", "H N Z V C", "A,B" } },
            { Inh.Daa, new[] { "Decimal-adjust A after adding two BCD numbers", "N Z C", "A,CC" } },
            { Inh.Tsx, new[] { "Set X to SP + 1 (X then points at the top stack entry)", "", "X,SP" } },
            { Inh.Txs, new[] { "Set the stack pointer to X - 1", "", "X,SP" } },
            { Inh.Ins, new[] { "Add 1 to the stack pointer (drops one byte from the stack)", "", "SP" } },
            { Inh.Des, new[] { "Subtract 1 from the stack pointer (reserves one byte on the stack)", "", "SP" } },
            { Inh.Pula, new[] { "Pull A from the stack", "", "A,SP" } },
            { Inh.Pulb, new[] { "Pull B from the stack", "", "B,SP" } },
            { Inh.Psha, new[] { "Push A onto the stack", "", "A,SP" } },
            { Inh.Pshb, new[] { "Push B onto the stack", "", "B,SP" } },
            { Inh.Pulx, new[] { "Pull X from the stack (two bytes)", "", "X,SP" } },
            { Inh.Pshx, new[] { "Push X onto the stack (two bytes)", "", "X,SP" } },
            { Inh.Rts, new[] { "Return from subroutine: pull the return address from the stack and continue there", "", "SP" } },
            { Inh.Abx, new[] { "Add B (unsigned) to X", "", "B,X" } },
            { Inh.Rti, new[] { "Return from interrupt: pull CC, B, A, X and the return address from the stack", "all, restored", "SP" } },
            { Inh.Mul, new[] { "Multiply A by B (unsigned); the 16-bit result goes to D (A:B)", "C", "A,B" } },
            { Inh.Wai, new[] { "Push all registers and wait for an interrupt", "", "SP" } },
            { Inh.Swi, new[] { "Software interrupt: push all registers and jump through the vector at $FFFA", "I=1", "SP" } },
            { Inh.Xgdx, new[] { "Exchange D and X", "", "D,X" } },
            { Inh.Lsrd, new[] { "Shift D (A:B) right one bit: 0 enters bit 15, bit 0 goes to the carry", "N=0, Z V C", "D" } },
            { Inh.Asld, new[] { "Shift D (A:B) left one bit: 0 enters bit 0, bit 15 goes to the carry", "N Z V C", "D" } },
        };

        // text with {0} = what is operated on, flags affected
        static readonly Dictionary<Unop, string[]> UnopText = new Dictionary<Unop, string[]>
        {
            { Unop.Neg, new[] { "Negate {0} (two's complement: 0 minus the value)", "N Z V C" } },
            { Unop.Com, new[] { "Invert every bit of {0}", "N Z, V=0, C=1" } },
            { Unop.Lsr, new[] { "Shift {0} right one bit: 0 enters bit 7, bit 0 goes to the carry", "N=0, Z V C" } },
            { Unop.Ror, new[] { "Rotate {0} right through the carry: the carry enters bit 7, bit 0 goes to the carry", "N Z V C" } },
            { Unop.Asr, new[] { "Shift {0} right one bit keeping the sign bit; bit 0 goes to the carry", "N Z V C" } },
            { Unop.Asl, new[] { "Shift {0} left one bit: 0 enters bit 0, bit 7 goes to the carry", "N Z V C" } },
            { Unop.Rol, new[] { "Rotate {0} left through the carry: the carry enters bit 0, bit 7 goes to the carry", "N Z V C" } },
            { Unop.Dec, new[] { "Subtract 1 from {0}", "N Z V (carry unchanged)" } },
            { Unop.Inc, new[] { "Add 1 to {0}", "N Z V (carry unchanged)" } },
            { Unop.Tst, new[] { "Test {0}: set N and Z from its value, nothing is changed", "N Z, V=0, C=0" } },
            { Unop.Clr, new[] { "Set {0} to zero", "N=0, Z=1, V=0, C=0" } },
        };

        static ushort Word(Func<ushort, byte> read, ushort a) => (ushort)((read(a) << 8) | read((ushort)(a + 1)));

        static bool IsJump(OpInfo info) => info.Kind == Kind.UnaryMem && info.Sub == (byte)Unop.Jmp;

        /// <summary>Decode the operand of the instruction at <paramref name="pc"/>; <paramref name="x"/> is used for indexed addresses.</summary>
        public static OperandInfo Operand(Func<ushort, byte> read, ushort pc, ushort x)
        {
            OpInfo info = Opcodes.Table[read(pc)];
            var o = new OperandInfo { Mode = info.Mode };
            ushort next = (ushort)(pc + 1);
            switch (info.Mode)
            {
                case Mode.Inh:
                    o.Length = 1;
                    break;
                case Mode.Rel:
                    o.Length = 2;
                    o.HasAddress = true;
                    o.Address = (ushort)(pc + 2 + (sbyte)read(next));
                    break;
                case Mode.Imm:
                    o.Length = 2;
                    o.IsImmediate = true;
                    o.Immediate = read(next);
                    break;
                case Mode.Imm16:
                    o.Length = 3;
                    o.IsImmediate = true;
                    o.Immediate = Word(read, next);
                    break;
                case Mode.Dir:
                    o.Length = 2;
                    o.HasAddress = true;
                    o.Address = read(next);
                    break;
                case Mode.Idx:
                    o.Length = 2;
                    o.HasAddress = true;
                    o.UsesX = true;
                    o.Address = (ushort)(x + read(next));
                    break;
                default:
                    o.Length = 3;
                    o.HasAddress = true;
                    o.Address = Word(read, next);
                    break;
            }
            o.IsCodeTarget = info.Kind == Kind.Branch || info.Kind == Kind.Bsr || info.Kind == Kind.Jsr || IsJump(info);
            bool wide = info.Kind == Kind.Alu16 || info.Kind == Kind.St16;
            o.Size = !o.HasAddress || o.IsCodeTarget ? 0 : wide ? 2 : 1;
            return o;
        }

        /// <summary>Would a branch with this condition (opcode low nibble) be taken with these condition codes?</summary>
        public static bool BranchTaken(int condition, byte cc)
        {
            bool c = (cc & 0x01) != 0, v = (cc & 0x02) != 0, z = (cc & 0x04) != 0, n = (cc & 0x08) != 0;
            switch (condition & 0x0F)
            {
                case 0: return true;
                case 1: return false;
                case 2: return !(c || z);
                case 3: return c || z;
                case 4: return !c;
                case 5: return c;
                case 6: return !z;
                case 7: return z;
                case 8: return !v;
                case 9: return v;
                case 10: return !n;
                case 11: return n;
                case 12: return n == v;
                case 13: return n != v;
                case 14: return !z && n == v;
                default: return z || n != v;
            }
        }

        /// <summary>The flags that are set, e.g. "I Z C", or "none".</summary>
        public static string FlagsSet(byte cc)
        {
            var parts = new List<string>();
            string names = "HINZVC";
            for (int i = 0; i < 6; i++)
                if ((cc & (0x20 >> i)) != 0) parts.Add(names[i].ToString());
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        static string Named(ushort addr, Func<ushort, string> symbol)
        {
            string n = symbol?.Invoke(addr);
            return n == null ? $"${addr:X4}" : $"{n} (${addr:X4})";
        }

        static string OperandPhrase(OpInfo info, OperandInfo o, Func<ushort, byte> read, ushort pc,
                                    Func<ushort, string> symbol, Func<ushort, string> immSymbol)
        {
            bool wide = info.Kind == Kind.Alu16 || info.Kind == Kind.St16;
            string where = wide ? "the two bytes at " : "the byte at ";
            switch (info.Mode)
            {
                case Mode.Imm:
                {
                    int v = o.Immediate;
                    string ch = v >= 0x20 && v < 0x7F ? $", '{(char)v}'" : "";
                    return $"the value ${v:X2} ({v}{ch})";
                }
                case Mode.Imm16:
                {
                    string n = immSymbol?.Invoke((ushort)o.Immediate);
                    return $"the value ${o.Immediate:X4}" + (n != null ? $" (the address of {n})" : $" ({o.Immediate})");
                }
                case Mode.Idx:
                    return where + $"X+${read((ushort)(pc + 1)):X2}";
                default:
                    return where + Named(o.Address, symbol);
            }
        }

        static void Build(OpInfo info, OperandInfo o, Func<ushort, byte> read, ushort pc,
                          Func<ushort, string> symbol, Func<ushort, string> immSymbol, out string text, out string flags)
        {
            string reg = info.RegB ? "B" : "A";
            string m = o.HasAddress || o.IsImmediate ? OperandPhrase(info, o, read, pc, symbol, immSymbol) : "";
            string target = info.Mode == Mode.Idx ? $"the address X+${read((ushort)(pc + 1)):X2}"
                          : o.HasAddress ? Named(o.Address, symbol) : "";
            text = "";
            flags = "";
            switch (info.Kind)
            {
                case Kind.Alu8:
                    switch ((AluOp)info.Sub)
                    {
                        case AluOp.Sub: text = $"Subtract {m} from {reg}"; flags = "N Z V C"; break;
                        case AluOp.Cmp: text = $"Compare {reg} with {m} (computes {reg} minus it, keeps only the flags)"; flags = "N Z V C"; break;
                        case AluOp.Sbc: text = $"Subtract {m} and the carry from {reg}"; flags = "N Z V C"; break;
                        case AluOp.And: text = $"AND {reg} with {m}"; flags = "N Z, V=0"; break;
                        case AluOp.Bit: text = $"Test bits: AND {reg} with {m}, keeping only the flags"; flags = "N Z, V=0"; break;
                        case AluOp.Ld: text = $"Load {reg} with {m}"; flags = "N Z, V=0"; break;
                        case AluOp.Eor: text = $"Exclusive-OR {reg} with {m}"; flags = "N Z, V=0"; break;
                        case AluOp.Adc: text = $"Add {m} plus the carry to {reg}"; flags = "H N Z V C"; break;
                        case AluOp.Or: text = $"OR {reg} with {m}"; flags = "N Z, V=0"; break;
                        default: text = $"Add {m} to {reg}"; flags = "H N Z V C"; break;
                    }
                    break;
                case Kind.St8:
                    text = $"Store {reg} into {m}";
                    flags = "N Z, V=0";
                    break;
                case Kind.Alu16:
                    switch ((Op16)info.Sub)
                    {
                        case Op16.Subd: text = $"Subtract {m} from D (A:B)"; flags = "N Z V C"; break;
                        case Op16.Addd: text = $"Add {m} to D (A:B)"; flags = "N Z V C"; break;
                        case Op16.Cpx: text = $"Compare X with {m} (computes X minus it, keeps only the flags)"; flags = "N Z V C"; break;
                        case Op16.Lds: text = $"Load the stack pointer with {m}"; flags = "N Z, V=0"; break;
                        case Op16.Ldd: text = $"Load D (A:B) with {m}"; flags = "N Z, V=0"; break;
                        default: text = $"Load X with {m}"; flags = "N Z, V=0"; break;
                    }
                    break;
                case Kind.St16:
                {
                    string r16 = (Op16)info.Sub == Op16.Sts ? "the stack pointer" : (Op16)info.Sub == Op16.Std ? "D (A:B)" : "X";
                    text = $"Store {r16} into {m}, high byte first";
                    flags = "N Z, V=0";
                    break;
                }
                case Kind.Jsr:
                case Kind.Bsr:
                    text = $"Call the subroutine at {target}: push the return address, then jump there";
                    break;
                case Kind.Branch:
                    text = $"Branch to {target} {BranchText[info.Sub & 0x0F]}";
                    break;
                case Kind.UnaryMem:
                case Kind.UnaryReg:
                    if (IsJump(info)) { text = $"Jump to {target}"; break; }
                    if (UnopText.TryGetValue((Unop)info.Sub, out string[] u))
                    {
                        text = string.Format(u[0], info.Kind == Kind.UnaryReg ? reg : m);
                        flags = u[1];
                    }
                    break;
                case Kind.Inh:
                    if (InhText.TryGetValue((Inh)info.Sub, out string[] t))
                    {
                        text = t[0];
                        flags = t[1];
                    }
                    break;
                default:
                    text = "Not a 6803 instruction: this byte is data, or the CPU has run into data";
                    break;
            }
        }

        /// <summary>The registers an instruction reads or writes, in display order.</summary>
        public static List<string> Registers(OpInfo info, OperandInfo o)
        {
            var set = new HashSet<string>();
            switch (info.Kind)
            {
                case Kind.Alu8:
                    set.Add(info.RegB ? "B" : "A");
                    if ((AluOp)info.Sub == AluOp.Adc || (AluOp)info.Sub == AluOp.Sbc) set.Add("CC");
                    break;
                case Kind.St8:
                    set.Add(info.RegB ? "B" : "A");
                    break;
                case Kind.UnaryReg:
                case Kind.UnaryMem:
                    if (info.Kind == Kind.UnaryReg) set.Add(info.RegB ? "B" : "A");
                    if ((Unop)info.Sub == Unop.Ror || (Unop)info.Sub == Unop.Rol) set.Add("CC");
                    break;
                case Kind.Alu16:
                case Kind.St16:
                    switch ((Op16)info.Sub)
                    {
                        case Op16.Cpx:
                        case Op16.Ldx:
                        case Op16.Stx: set.Add("X"); break;
                        case Op16.Lds:
                        case Op16.Sts: set.Add("SP"); break;
                        default: set.Add("D"); break;
                    }
                    break;
                case Kind.Jsr:
                case Kind.Bsr:
                    set.Add("SP");
                    break;
                case Kind.Branch:
                    set.Add("CC");
                    break;
                case Kind.Inh:
                    if (InhText.TryGetValue((Inh)info.Sub, out string[] t))
                        foreach (string r in t[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(r);
                    break;
            }
            if (o.UsesX) set.Add("X");
            return new[] { "A", "B", "D", "X", "SP", "CC" }.Where(set.Contains).ToList();
        }

        /// <summary>What the instruction at <paramref name="pc"/> does, which flags it changes, its size and time.</summary>
        public static string Describe(Func<ushort, byte> read, ushort pc, ushort x,
                                      Func<ushort, string> symbol = null, Func<ushort, string> immSymbol = null)
        {
            OpInfo info = Opcodes.Table[read(pc)];
            OperandInfo o = Operand(read, pc, x);
            Build(info, o, read, pc, symbol, immSymbol, out string text, out string flags);
            string s = text + ".";
            if (info.Kind == Kind.Illegal) return s;
            s += flags.Length > 0 ? $"  Flags: {flags}." : "  Flags: unchanged.";
            return s + $"  {o.Length} byte{(o.Length == 1 ? "" : "s")}, {info.Cycles} cycles.";
        }

        static string RegisterText(Cpu6803 cpu, string name)
        {
            switch (name)
            {
                case "A": return $"A=${cpu.A:X2}";
                case "B": return $"B=${cpu.B:X2}";
                case "D": return $"D=${cpu.D:X4}";
                case "X": return $"X=${cpu.X:X4}";
                case "SP": return $"SP=${cpu.SP:X4}";
                default: return $"CC=${cpu.CC | 0xC0:X2} ({FlagsSet(cpu.CC)})";
            }
        }

        /// <summary><see cref="Describe"/> plus the values involved right now (for the instruction about to run).</summary>
        public static string DescribeLive(Machine m, ushort pc)
        {
            Cpu6803 cpu = m.Cpu;
            Func<ushort, byte> read = m.Bus.Peek;
            string s = Describe(read, pc, cpu.X, m.SymbolFor, m.CodeSymbolFor);
            OpInfo info = Opcodes.Table[read(pc)];
            if (info.Kind == Kind.Illegal) return s;
            OperandInfo o = Operand(read, pc, cpu.X);
            var now = new List<string>();
            if (info.Kind == Kind.Branch)
                now.Add(BranchTaken(info.Sub, cpu.CC) ? $"the branch will be taken, to ${o.Address:X4}" : "the branch will not be taken");
            else if (o.HasAddress)
            {
                if (o.UsesX) now.Add($"the address is ${o.Address:X4}");
                if (o.Size == 1) now.Add($"[${o.Address:X4}]=${read(o.Address):X2}");
                else if (o.Size == 2) now.Add($"[${o.Address:X4}]=${Word(read, o.Address):X4}");
            }
            if (info.Kind == Kind.Inh && (Inh)info.Sub == Inh.Rts)
            {
                ushort ret = Word(read, (ushort)(cpu.SP + 1));
                now.Add($"it returns to ${ret:X4}" + (m.DescribeAddress(ret) != null ? $" ({m.DescribeAddress(ret)})" : ""));
            }
            foreach (string r in Registers(info, o)) now.Add(RegisterText(cpu, r));
            return now.Count == 0 ? s : s + "  Now: " + string.Join(", ", now) + ".";
        }

        /// <summary>The first instructions at an address (stops after a return or jump), for hover previews.</summary>
        public static List<string> Preview(Machine m, ushort addr, int count)
        {
            var lines = new List<string>();
            ushort a = addr;
            for (int n = 0; n < count; n++)
            {
                string name = m.CodeSymbolFor(a);
                if (name != null) lines.Add(name + ":");
                OpInfo info = Opcodes.Table[m.Bus.Peek(a)];
                string dis = Disassembler.Disassemble(m.Bus.Peek, a, out int len, m.SymbolFor, m.CodeSymbolFor);
                lines.Add($"  {a:X4}  {dis}");
                bool ends = info.Kind == Kind.Illegal || IsJump(info) || info.Name == "BRA" ||
                            (info.Kind == Kind.Inh && ((Inh)info.Sub == Inh.Rts || (Inh)info.Sub == Inh.Rti));
                if (ends) break;
                a = (ushort)(a + len);
            }
            return lines;
        }

        static string Printable(int v) => v >= 0x20 && v < 0x7F ? $" '{(char)v}'" : "";

        /// <summary>
        /// The registers and memory used by the instruction that just ran and by the one about to run,
        /// with their current values (like the Autos window of a source-level debugger).
        /// </summary>
        public static List<AutoItem> Autos(Machine m)
        {
            var items = new List<AutoItem>();
            Cpu6803 cpu = m.Cpu;
            var registers = new HashSet<string>();
            var memory = new List<OperandInfo>();

            void Collect(OpInfo info, OperandInfo o)
            {
                foreach (string r in Registers(info, o)) registers.Add(r);
                if (o.Size > 0 && !memory.Any(x => x.Address == o.Address && x.Size == o.Size)) memory.Add(o);
            }

            if (m.History.Count > 0)
            {
                HistoryEntry h = m.History.Last();
                Func<ushort, byte> bytes = addr =>
                {
                    int d = (ushort)(addr - h.Pc);
                    return d == 0 ? h.Op0 : d == 1 ? h.Op1 : h.Op2;
                };
                items.Add(new AutoItem
                {
                    Name = "previous", Value = h.Pc.ToString("X4"), Detail = h.Describe(m.SymbolFor, m.CodeSymbolFor),
                    HasAddress = true, Address = h.Pc, IsInstruction = true,
                });
                Collect(Opcodes.Table[h.Op0], Operand(bytes, h.Pc, h.X));
            }

            ushort pc = cpu.PC;
            OpInfo next = Opcodes.Table[m.Bus.Peek(pc)];
            OperandInfo nextOperand = Operand(m.Bus.Peek, pc, cpu.X);
            string nextText = Disassembler.Disassemble(m.Bus.Peek, pc, out int length, m.SymbolFor, m.CodeSymbolFor);
            if (next.Kind == Kind.Branch) nextText += BranchTaken(next.Sub, cpu.CC) ? "   (will be taken)" : "   (will not be taken)";
            items.Add(new AutoItem { Name = "next", Value = pc.ToString("X4"), Detail = nextText, HasAddress = true, Address = pc, IsInstruction = true });
            Collect(next, nextOperand);

            foreach (string r in new[] { "A", "B", "D", "X", "SP", "CC" })
            {
                if (!registers.Contains(r)) continue;
                var item = new AutoItem { Name = r };
                switch (r)
                {
                    case "A": item.Value = cpu.A.ToString("X2"); item.Detail = cpu.A + Printable(cpu.A); break;
                    case "B": item.Value = cpu.B.ToString("X2"); item.Detail = cpu.B + Printable(cpu.B); break;
                    case "D": item.Value = cpu.D.ToString("X4"); item.Detail = cpu.D.ToString(); break;
                    case "X":
                        item.Value = cpu.X.ToString("X4");
                        item.Detail = m.SymbolFor(cpu.X) ?? m.DescribeAddress(cpu.X) ?? cpu.X.ToString();
                        item.HasAddress = true; item.Address = cpu.X;
                        break;
                    case "SP":
                        item.Value = cpu.SP.ToString("X4");
                        item.Detail = $"top of stack: {m.Bus.Peek((ushort)(cpu.SP + 1)):X2} {m.Bus.Peek((ushort)(cpu.SP + 2)):X2}";
                        item.HasAddress = true; item.Address = cpu.SP;
                        break;
                    default:
                        item.Value = (cpu.CC | 0xC0).ToString("X2");
                        item.Detail = "flags set: " + FlagsSet(cpu.CC);
                        break;
                }
                items.Add(item);
            }

            foreach (OperandInfo o in memory)
            {
                string name = m.SymbolFor(o.Address);
                var item = new AutoItem { Name = name ?? $"[${o.Address:X4}]", HasAddress = true, Address = o.Address };
                if (o.Size == 2)
                {
                    ushort w = Word(m.Bus.Peek, o.Address);
                    item.Value = w.ToString("X4");
                    item.Detail = $"two bytes at ${o.Address:X4} = {w}" + (m.DescribeAddress(w) != null ? $", points to {m.DescribeAddress(w)}" : "");
                }
                else
                {
                    byte v = m.Bus.Peek(o.Address);
                    item.Value = v.ToString("X2");
                    item.Detail = $"byte at ${o.Address:X4} = {v}" + Printable(v);
                }
                items.Add(item);
            }
            return items;
        }
    }
}
