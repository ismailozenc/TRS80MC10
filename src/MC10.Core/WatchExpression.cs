using System;
using System.Globalization;
using System.Text;

namespace MC10.Core
{
    /// <summary>The value of a watch expression.</summary>
    public sealed class WatchResult
    {
        public bool Ok;
        public string Error = "";
        public int Value;
        public int Size = 1;          // 1 or 2 bytes
        public bool HasAddress;       // the value was read from memory at Address
        public ushort Address;
        public string Text = "";      // character or string form, when there is one
    }

    /// <summary>
    /// Watch expressions for the debugger.
    ///   A B D X SP PC CC        a register
    ///   4280  or  cursor_addr   the byte in memory at that address or label
    ///   cursor_addr.w           the two-byte value there (high byte first);  .b = byte,  .s = text
    ///   [X]  [SP+1].w           memory at the address held in a register (an offset in hex may follow)
    ///   [txtptr.w]              follow a pointer: the byte at the address stored in txtptr
    /// Register names win over hex numbers, so write $A or 000A for address $000A.
    /// </summary>
    public static class WatchExpression
    {
        public static WatchResult Evaluate(string text, Machine m) => Eval((text ?? "").Trim(), m, 0);

        static WatchResult Fail(string message) => new WatchResult { Ok = false, Error = message };

        static WatchResult Eval(string text, Machine m, int depth)
        {
            if (text.Length == 0) return Fail("empty");
            if (depth > 4) return Fail("nested too deeply");

            char size = 'b';
            if (HasSuffix(text))
            {
                size = char.ToLowerInvariant(text[text.Length - 1]);
                text = text.Substring(0, text.Length - 2).Trim();
            }

            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
            {
                string inner = text.Substring(1, text.Length - 2).Trim();
                int address;
                if (inner.StartsWith("[", StringComparison.Ordinal) || HasSuffix(inner))
                {
                    WatchResult p = Eval(inner, m, depth + 1);
                    if (!p.Ok) return p;
                    address = p.Value;
                }
                else if (!TryTerm(inner, m, out address, out bool unused))
                    return Fail($"'{inner}' is not a register, address or label");
                return ReadMemory(m, (ushort)address, size);
            }

            if (!TryTerm(text, m, out int value, out bool isRegister))
                return Fail($"'{text}' is not a register, address or label");
            if (!isRegister) return ReadMemory(m, (ushort)value, size);

            string reg = BaseOf(text).ToUpperInvariant();
            return new WatchResult { Ok = true, Value = value, Size = reg == "A" || reg == "B" || reg == "CC" ? 1 : 2 };
        }

        /// <summary>True when the text ends in .b, .w or .s outside any brackets.</summary>
        static bool HasSuffix(string text)
        {
            if (text.Length < 3 || text[text.Length - 2] != '.') return false;
            char c = char.ToLowerInvariant(text[text.Length - 1]);
            return c == 'b' || c == 'w' || c == 's';
        }

        static int SplitAt(string text)
        {
            int plus = text.LastIndexOf('+'), minus = text.LastIndexOf('-');
            int i = Math.Max(plus, minus);
            return i > 0 ? i : -1;
        }

        static string BaseOf(string text)
        {
            int i = SplitAt(text);
            return (i < 0 ? text : text.Substring(0, i)).Trim();
        }

        /// <summary>A register, address or label, optionally followed by +hex or -hex.</summary>
        static bool TryTerm(string text, Machine m, out int value, out bool isRegister)
        {
            value = 0;
            isRegister = false;
            int offset = 0;
            int i = SplitAt(text);
            string name = text.Trim();
            if (i > 0)
            {
                string off = text.Substring(i + 1).Trim().TrimStart('$');
                if (!int.TryParse(off, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset)) return false;
                if (text[i] == '-') offset = -offset;
                name = text.Substring(0, i).Trim();
            }
            Cpu6803 cpu = m.Cpu;
            switch (name.ToUpperInvariant())
            {
                case "A": value = cpu.A; isRegister = true; break;
                case "B": value = cpu.B; isRegister = true; break;
                case "D": value = cpu.D; isRegister = true; break;
                case "X": value = cpu.X; isRegister = true; break;
                case "SP": value = cpu.SP; isRegister = true; break;
                case "PC": value = cpu.PC; isRegister = true; break;
                case "CC": value = cpu.CC | 0xC0; isRegister = true; break;
                default:
                    if (!m.TryResolve(name, out ushort address)) return false;
                    value = address;
                    break;
            }
            value = (value + offset) & 0xFFFF;
            return true;
        }

        static WatchResult ReadMemory(Machine m, ushort address, char size)
        {
            var r = new WatchResult { Ok = true, HasAddress = true, Address = address };
            byte first = m.Bus.Peek(address);
            if (size == 'w')
            {
                r.Size = 2;
                r.Value = (first << 8) | m.Bus.Peek((ushort)(address + 1));
                r.Text = m.DescribeAddress((ushort)r.Value) ?? "";
            }
            else if (size == 's')
            {
                r.Value = first;
                var sb = new StringBuilder();
                for (int i = 0; i < 24; i++)
                {
                    byte b = m.Bus.Peek((ushort)(address + i));
                    if (b == 0) break;
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                r.Text = "\"" + sb + "\"";
            }
            else
            {
                r.Value = first;
                r.Text = first >= 0x20 && first < 0x7F ? "'" + (char)first + "'" : "";
            }
            return r;
        }
    }
}
