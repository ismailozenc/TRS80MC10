using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    /// <summary>
    /// Draws one listing line with syntax colours. Code lines use the fixed layout produced by
    /// <see cref="Listing"/>: two spaces, address (4), two spaces, hex bytes (9 wide), a space, then the
    /// instruction. Every glyph is placed at a column position, so the monospaced columns always line up.
    /// </summary>
    internal static class CodePainter
    {
        const int HexCol = 8, DisCol = 18;

        static readonly HashSet<string> Branches = new HashSet<string>
        {
            "BRA", "BRN", "BHI", "BLS", "BCC", "BCS", "BNE", "BEQ", "BVC", "BVS", "BPL", "BMI", "BGE", "BLT", "BGT", "BLE",
        };

        static readonly HashSet<string> StackAndTransfer = new HashSet<string>
        {
            "PSHA", "PSHB", "PSHX", "PULA", "PULB", "PULX", "TAB", "TBA", "TAP", "TPA", "TSX", "TXS", "INS", "DES",
            "XGDX", "ABX", "CLC", "SEC", "CLI", "SEI", "CLV", "SEV",
        };

        /// <summary>Width of one character cell of a monospaced font.</summary>
        public static float CharWidth(Font font)
        {
            Size size = TextRenderer.MeasureText("MMMMMMMMMMMMMMMMMMMM", font, new Size(int.MaxValue, int.MaxValue),
                                                 TextFormatFlags.NoPadding);
            return size.Width / 20f;
        }

        public static Color MnemonicColor(string mnemonic, out bool bold)
        {
            bold = false;
            switch (mnemonic)
            {
                case "JSR":
                case "BSR": bold = true; return Theme.Call;
                case "JMP": return Theme.Call;
                case "RTS":
                case "RTI": bold = true; return Theme.Return;
                case "SWI":
                case "WAI":
                case "FCB": return Theme.Special;
                case "NOP": return Theme.Register;
            }
            if (Branches.Contains(mnemonic)) return Theme.Branch;
            if (mnemonic.StartsWith("LD", StringComparison.Ordinal)) return Theme.Load;
            if (mnemonic.StartsWith("ST", StringComparison.Ordinal)) return Theme.Store;
            if (mnemonic.StartsWith("CMP", StringComparison.Ordinal) || mnemonic.StartsWith("CPX", StringComparison.Ordinal) ||
                mnemonic.StartsWith("TST", StringComparison.Ordinal) || mnemonic.StartsWith("BIT", StringComparison.Ordinal) ||
                mnemonic == "CBA")
                return Theme.Compare;
            if (StackAndTransfer.Contains(mnemonic)) return Theme.Stack;
            return Theme.Alu;
        }

        static void Put(Graphics g, string s, int column, Color color, Font font, float cw, int x0, int y)
        {
            if (s.Length == 0) return;
            TextRenderer.DrawText(g, s, font, new Point(x0 + (int)Math.Round(column * cw), y), color, TextFormatFlags.NoPadding);
        }

        /// <summary>Draw a line of the listing at (x0, y).</summary>
        public static void DrawLine(Graphics g, string text, LineKind kind, Font font, Font bold, float cw, int x0, int y)
        {
            switch (kind)
            {
                case LineKind.Blank:
                    return;
                case LineKind.Header:
                case LineKind.Comment:
                    Put(g, text, 0, Theme.Comment, font, cw, x0, y);
                    return;
                case LineKind.Label:
                    Put(g, text, 0, Theme.Label, bold, cw, x0, y);
                    return;
                case LineKind.Data:
                    if (text.Length > HexCol)
                    {
                        Put(g, text.Substring(0, HexCol), 0, Theme.Address, font, cw, x0, y);
                        Put(g, text.Substring(HexCol), HexCol, Theme.Data, font, cw, x0, y);
                    }
                    else Put(g, text, 0, Theme.Data, font, cw, x0, y);
                    return;
            }

            // Code
            if (text.Length <= DisCol)
            {
                Put(g, text, 0, Theme.Alu, font, cw, x0, y);
                return;
            }
            Put(g, text.Substring(0, HexCol), 0, Theme.Address, font, cw, x0, y);
            Put(g, text.Substring(HexCol, DisCol - HexCol), HexCol, Theme.HexBytes, font, cw, x0, y);
            DrawInstruction(g, text.Substring(DisCol), DisCol, font, bold, cw, x0, y);
        }

        /// <summary>Draw "MNEMONIC operand" starting at a character column. Also used for the history and live views.</summary>
        public static void DrawInstruction(Graphics g, string dis, int column, Font font, Font bold, float cw, int x0, int y)
        {
            int space = dis.IndexOf(' ');
            string mnemonic = space < 0 ? dis : dis.Substring(0, space);
            Color mc = MnemonicColor(mnemonic, out bool isBold);
            Put(g, mnemonic, column, mc, isBold ? bold : font, cw, x0, y);
            if (space < 0) return;

            string rest = dis.Substring(space);
            int baseCol = column + space;
            int i = 0;
            while (i < rest.Length)
            {
                char c = rest[i];
                if (c == ' ') { i++; continue; }
                if (c == ';')
                {
                    Put(g, rest.Substring(i), baseCol + i, Theme.Comment, font, cw, x0, y);
                    break;
                }
                int j = i + 1;
                Color color;
                if (c == '$')
                {
                    while (j < rest.Length && Uri.IsHexDigit(rest[j])) j++;
                    color = Theme.Number;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    while (j < rest.Length && (char.IsLetterOrDigit(rest[j]) || rest[j] == '_')) j++;
                    color = rest.Substring(i, j - i) == "X" ? Theme.Register : Theme.Name;
                }
                else color = Theme.Register;      // '#', ',' and anything else
                Put(g, rest.Substring(i, j - i), baseCol + i, color, font, cw, x0, y);
                i = j;
            }
        }
    }
}
