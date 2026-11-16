using System;
using System.Text;

namespace MC10.Core
{
    /// <summary>One character cell as the renderer sees it.</summary>
    public readonly struct Cell
    {
        public readonly byte Code;
        public readonly int Foreground;
        public readonly int Background;
        public Cell(byte code, int fg, int bg) { Code = code; Foreground = fg; Background = bg; }
        public bool IsSemigraphic => Code >= 0x80;
    }

    /// <summary>
    /// Text-screen model: reads the 32 x 16 video RAM and interprets each byte.
    /// Codes $20-$7E render as characters. Codes $80-$FF are treated as 2x2 block graphics
    /// with bits 0-3 selecting the four blocks (top-left, top-right, bottom-left, bottom-right).
    /// That block layout is an assumption, not documented in the manual; see docs/ASSUMPTIONS.md.
    /// </summary>
    public sealed class Video
    {
        public const int Columns = 32, Rows = 16;

        readonly Bus bus;
        readonly int vramBase;
        readonly VideoControlDevice control;

        public Video(Bus bus, int vramBase, VideoControlDevice control)
        {
            this.bus = bus;
            this.vramBase = vramBase;
            this.control = control;
        }

        public Cell GetCell(int row, int col)
        {
            byte code = bus.Mem[vramBase + row * Columns + col];
            return new Cell(code, control.Foreground, control.Background);
        }

        /// <summary>Plain-text dump of the screen (semigraphics mapped to Unicode quadrant characters).</summary>
        public string TextDump()
        {
            var sb = new StringBuilder();
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Columns; c++)
                    sb.Append(CharFor(GetCell(r, c).Code));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Printable character for a code, or a quadrant glyph for block graphics.</summary>
        public static char CharFor(byte code)
        {
            if (code >= 0x20 && code <= 0x7E) return (char)code;
            if (code >= 0x80) return QuadrantChars[code & 0x0F];
            return ' ';
        }

        // Index = 4-bit block mask (bit0 TL, bit1 TR, bit2 BL, bit3 BR)
        static readonly char[] QuadrantChars =
        {
            ' ', '▘', '▝', '▀', '▖', '▌', '▞', '▛',
            '▗', '▚', '▐', '▜', '▄', '▙', '▟', '█'
        };

        /// <summary>Renders the screen to an RGBA-free 32-bit ARGB buffer, 8 x 8 pixels per cell.</summary>
        public int[] RenderArgb(Func<int, int> paletteArgb, int cellW = 8, int cellH = 8)
        {
            int w = Columns * cellW, h = Rows * cellH;
            var px = new int[w * h];
            for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                Cell cell = GetCell(r, c);
                int bg = paletteArgb(cell.Background);
                int fg = paletteArgb(cell.Foreground);
                for (int y = 0; y < cellH; y++)
                for (int x = 0; x < cellW; x++)
                {
                    int color = bg;
                    if (cell.IsSemigraphic)
                    {
                        int qx = x * 2 / cellW, qy = y * 2 / cellH;      // 0 or 1
                        int bit = qy * 2 + qx;                           // TL=0, TR=1, BL=2, BR=3
                        if (((cell.Code >> bit) & 1) != 0) color = fg;
                    }
                    px[(r * cellH + y) * w + c * cellW + x] = color;
                }
            }
            return px;
        }
    }
}
