using System;
using System.Globalization;

namespace MC10.Core
{
    /// <summary>
    /// Tiny expression language for conditional breakpoints: one comparison between two values.
    /// Values are a register (A B D X SP PC CC), a memory byte written <c>[hex address]</c>, or a hex number
    /// (a leading $ is optional). Operators: == != &lt; &gt; &lt;= &gt;= and &amp; (true when the bitwise AND is not zero).
    /// Examples: <c>A==0D</c>, <c>X&gt;=4200</c>, <c>[00F3]!=0</c>, <c>CC&amp;04</c>.
    /// </summary>
    public static class BreakCondition
    {
        // Two-character operators first so "<=" is not mistaken for "<".
        static readonly string[] Ops = { "==", "!=", "<=", ">=", "<", ">", "=", "&" };

        /// <summary>True or false when the condition could be evaluated; null if the text is not a valid condition.</summary>
        public static bool? Evaluate(string text, Machine m)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            foreach (string op in Ops)
            {
                int i = text.IndexOf(op, StringComparison.Ordinal);
                if (i <= 0) continue;
                if (!TryValue(text.Substring(0, i), m, out int left)) return null;
                if (!TryValue(text.Substring(i + op.Length), m, out int right)) return null;
                switch (op)
                {
                    case "==":
                    case "=": return left == right;
                    case "!=": return left != right;
                    case "<=": return left <= right;
                    case ">=": return left >= right;
                    case "<": return left < right;
                    case ">": return left > right;
                    case "&": return (left & right) != 0;
                    default: return null;
                }
            }
            return null;
        }

        /// <summary>True if the text is a valid condition (used to reject typos when the user enters one).</summary>
        public static bool IsValid(string text, Machine m) => Evaluate(text, m).HasValue;

        static bool TryValue(string token, Machine m, out int value)
        {
            token = token.Trim().ToUpperInvariant();
            value = 0;
            switch (token)
            {
                case "A": value = m.Cpu.A; return true;
                case "B": value = m.Cpu.B; return true;
                case "D": value = m.Cpu.D; return true;
                case "X": value = m.Cpu.X; return true;
                case "SP": value = m.Cpu.SP; return true;
                case "PC": value = m.Cpu.PC; return true;
                case "CC": value = m.Cpu.CC | 0xC0; return true;
            }
            if (token.Length > 2 && token[0] == '[' && token[token.Length - 1] == ']')
            {
                if (!TryHex(token.Substring(1, token.Length - 2), out int address) || address < 0 || address > 0xFFFF)
                    return false;
                value = m.Bus.Peek((ushort)address);
                return true;
            }
            return TryHex(token, out value);
        }

        static bool TryHex(string s, out int value)
        {
            s = s.Trim().TrimStart('$');
            if (s.StartsWith("0X", StringComparison.Ordinal)) s = s.Substring(2);
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
