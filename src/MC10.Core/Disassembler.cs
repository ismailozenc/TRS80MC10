using System;

namespace MC10.Core
{
    /// <summary>MC6803 disassembler driven by the shared opcode table.</summary>
    public static class Disassembler
    {
        /// <summary>
        /// Disassemble the instruction at <paramref name="pc"/>.
        /// <paramref name="read"/> supplies bytes; <paramref name="symbol"/> optionally names addresses.
        /// </summary>
        public static string Disassemble(Func<ushort, byte> read, ushort pc, out int length,
                                         Func<ushort, string> symbol = null)
        {
            byte op = read(pc);
            OpInfo info = Opcodes.Table[op];
            if (info.Kind == Kind.Illegal)
            {
                length = 1;
                return $"FCB ${op:X2}          ; illegal opcode";
            }

            length = 1 + OperandBytes(info.Mode);
            string operand;
            switch (info.Mode)
            {
                case Mode.Inh:
                    operand = "";
                    break;
                case Mode.Rel:
                {
                    sbyte off = (sbyte)read((ushort)(pc + 1));
                    ushort target = (ushort)(pc + 2 + off);
                    operand = Name(target, symbol);
                    break;
                }
                case Mode.Imm:
                    operand = $"#${read((ushort)(pc + 1)):X2}";
                    break;
                case Mode.Imm16:
                    operand = $"#${Word(read, (ushort)(pc + 1)):X4}";
                    break;
                case Mode.Dir:
                    operand = $"${read((ushort)(pc + 1)):X2}";
                    break;
                case Mode.Idx:
                    operand = $"${read((ushort)(pc + 1)):X2},X";
                    break;
                default:                                   // Ext
                {
                    ushort a = Word(read, (ushort)(pc + 1));
                    operand = Name(a, symbol);
                    break;
                }
            }
            return operand.Length == 0 ? info.Name : $"{info.Name,-5} {operand}";
        }

        static string Name(ushort addr, Func<ushort, string> symbol)
        {
            string s = symbol?.Invoke(addr);
            return s ?? $"${addr:X4}";
        }

        static ushort Word(Func<ushort, byte> read, ushort a)
            => (ushort)((read(a) << 8) | read((ushort)(a + 1)));

        public static int OperandBytes(Mode m) => m switch
        {
            Mode.Inh => 0,
            Mode.Imm16 => 2,
            Mode.Ext => 2,
            _ => 1,
        };

        /// <summary>Hex bytes of the instruction at pc, e.g. "86 10".</summary>
        public static string HexBytes(Func<ushort, byte> read, ushort pc, int length)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(read((ushort)(pc + i)).ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
