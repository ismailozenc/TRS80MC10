using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    // ====================================================================================================
    // Registers
    // ====================================================================================================

    /// <summary>
    /// Register panel: PC, SP, A, B, D, X with the condition-code flags. Values that changed since the previous
    /// stop are drawn in red, flags that are set are highlighted, and the next instruction is shown in colour.
    /// </summary>
    internal sealed class RegisterView : Control
    {
        struct Snapshot
        {
            public ushort Pc, Sp, X;
            public byte A, B, Cc;
            public long Cycles;
        }

        readonly Machine machine;
        readonly Font font = new Font("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Point);
        readonly Font bold = new Font("Consolas", 11f, FontStyle.Bold, GraphicsUnit.Point);
        readonly float cw;
        Snapshot cur, prev;
        bool hasCur;

        public RegisterView(Machine machine)
        {
            this.machine = machine;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Color.White;
            cw = CodePainter.CharWidth(font);
            Height = 8 * (font.Height + 2) + 10;
        }

        Snapshot Capture()
        {
            var c = machine.Cpu;
            return new Snapshot { Pc = c.PC, Sp = c.SP, X = c.X, A = c.A, B = c.B, Cc = (byte)(c.CC | 0xC0), Cycles = c.Cycles };
        }

        /// <summary>Take a new snapshot (when the CPU has run since the last one) and repaint.</summary>
        public void Reload()
        {
            if (!hasCur || machine.Cpu.Cycles != cur.Cycles)
            {
                prev = hasCur ? cur : Capture();
                cur = Capture();
                hasCur = true;
            }
            Invalidate();
        }

        static string Printable(byte b) => b >= 0x20 && b < 0x7F ? ((char)b).ToString() : ".";

        void Put(Graphics g, string s, int col, int y, Color color, bool strong)
        {
            TextRenderer.DrawText(g, s, strong ? bold : font, new Point(6 + (int)Math.Round(col * cw), y), color,
                                  TextFormatFlags.NoPadding);
        }

        void Reg(Graphics g, string name, string value, bool changed, int col, int y)
        {
            Put(g, name, col, y, Theme.Register, false);
            Put(g, value, col + name.Length + 1, y, changed ? Theme.Changed : Color.Black, changed);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (!hasCur) return;
            int lh = font.Height + 2;
            int y = 4;

            Reg(g, "PC", cur.Pc.ToString("X4"), cur.Pc != prev.Pc, 0, y);
            Reg(g, "SP", cur.Sp.ToString("X4"), cur.Sp != prev.Sp, 12, y);
            string where = machine.DescribeAddress(cur.Pc);
            if (where != null) Put(g, where, 24, y, Theme.Name, false);
            y += lh;

            ushort d = (ushort)((cur.A << 8) | cur.B);
            Reg(g, "A", cur.A.ToString("X2") + " '" + Printable(cur.A) + "'", cur.A != prev.A, 0, y);
            Reg(g, "B", cur.B.ToString("X2") + " '" + Printable(cur.B) + "'", cur.B != prev.B, 12, y);
            Reg(g, "D", d.ToString("X4"), cur.A != prev.A || cur.B != prev.B, 24, y);
            y += lh;

            byte atX = machine.Bus.Peek(cur.X);
            Reg(g, "X", cur.X.ToString("X4"), cur.X != prev.X, 0, y);
            Put(g, "[X]=" + atX.ToString("X2") + " '" + Printable(atX) + "'", 12, y, Theme.Register, false);
            y += lh;

            // Condition codes
            Put(g, "CC", 0, y, Theme.Register, false);
            Put(g, cur.Cc.ToString("X2"), 3, y, cur.Cc != prev.Cc ? Theme.Changed : Color.Black, cur.Cc != prev.Cc);
            string flags = "HINZVC";
            int[] masks = { 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };
            for (int i = 0; i < flags.Length; i++)
            {
                bool on = (cur.Cc & masks[i]) != 0;
                bool changed = ((cur.Cc ^ prev.Cc) & masks[i]) != 0;
                Color c = changed ? Theme.Changed : on ? Theme.Store : Color.FromArgb(185, 185, 185);
                Put(g, flags[i].ToString(), 7 + i * 2, y, c, on || changed);
            }
            if (machine.Cpu.Halted) Put(g, "WAI-halted", 21, y, Theme.Return, true);
            else if ((cur.Cc & 0x10) != 0) Put(g, "IRQs masked", 21, y, Theme.Register, false);
            y += lh;

            // Next instruction
            Put(g, "next", 0, y, Theme.Register, false);
            string dis = Disassembler.Disassemble(machine.Bus.Peek, cur.Pc, out int len, machine.SymbolFor, machine.CodeSymbolFor);
            CodePainter.DrawInstruction(g, dis, 5, font, bold, cw, 6, y);
            y += lh;

            var cpu = machine.Cpu;
            Put(g, $"cycles {cpu.Cycles:N0}   instructions {cpu.Instructions:N0}", 0, y, Theme.Register, false);
            y += lh;
            Put(g, $"illegal opcodes {cpu.IllegalCount}" + (cpu.IllegalCount > 0 ? $" (last at {cpu.LastIllegalPc:X4})" : ""), 0, y,
                cpu.IllegalCount > 0 ? Theme.Return : Theme.Register, cpu.IllegalCount > 0);
        }
    }

    // ====================================================================================================
    // Memory
    // ====================================================================================================

    /// <summary>
    /// Hex dump with colour: bytes that changed since the previous stop are red, the bytes under PC, X and SP are
    /// shaded, watched addresses are orange, zero bytes are dim. Scroll with the mouse wheel or the keyboard;
    /// double-click (or Enter) on a byte to change it. Reads use Bus.Peek, so looking never disturbs the machine.
    /// </summary>
    internal sealed class MemoryView : Control
    {
        readonly Machine machine;
        readonly Font font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
        readonly Font bold = new Font("Consolas", 10f, FontStyle.Bold, GraphicsUnit.Point);
        readonly float cw;
        readonly int lh;
        readonly byte[] before = new byte[0x10000];
        readonly byte[] snapshot = new byte[0x10000];
        long lastCycles = -1;
        int baseAddr;              // first row address (multiple of 16)
        int selected = -1;

        public event Action Edited;

        public MemoryView(Machine machine)
        {
            this.machine = machine;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            DoubleBuffered = true;
            TabStop = true;
            BackColor = Color.White;
            cw = CodePainter.CharWidth(font);
            lh = font.Height + 2;
            Buffer.BlockCopy(machine.Bus.Mem, 0, snapshot, 0, 0x10000);
            Buffer.BlockCopy(machine.Bus.Mem, 0, before, 0, 0x10000);
        }

        public ushort BaseAddress => (ushort)baseAddr;
        public int Selected => selected;

        public void GoTo(ushort addr)
        {
            baseAddr = Math.Max(0, Math.Min(0xFFF0, (addr & 0xFFF0) - 32));
            selected = addr;
            Invalidate();
        }

        /// <summary>Roll the change-detection snapshot (when the CPU has run) and repaint.</summary>
        public void Reload()
        {
            long cyc = machine.Cpu.Cycles;
            if (cyc != lastCycles)
            {
                Buffer.BlockCopy(snapshot, 0, before, 0, 0x10000);
                Buffer.BlockCopy(machine.Bus.Mem, 0, snapshot, 0, 0x10000);
                lastCycles = cyc;
            }
            Invalidate();
        }

        int Rows => Math.Max(1, ClientSize.Height / lh - 1);

        static Color RegionColor(Machine m, int a)
        {
            if (a < 0x20) return Color.FromArgb(140, 0, 140);                 // CPU internal registers
            if (!m.Bus.IsMapped((ushort)a)) return Color.FromArgb(170, 170, 170);
            if (m.Bus.IsRom((ushort)a)) return Color.FromArgb(60, 90, 140);
            return Color.Black;
        }

        void Put(Graphics g, string s, int col, int y, Color color, bool strong)
        {
            TextRenderer.DrawText(g, s, strong ? bold : font, new Point(4 + (int)Math.Round(col * cw), y), color,
                                  TextFormatFlags.NoPadding);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int y = 2;
            Put(g, "      ", 0, y, Theme.Address, false);
            for (int c = 0; c < 16; c++) Put(g, c.ToString("X2"), 6 + c * 3, y, Theme.Address, false);
            Put(g, "0123456789ABCDEF", 6 + 16 * 3 + 1, y, Theme.Address, false);
            y += lh;

            var cpu = machine.Cpu;
            for (int row = 0; row < Rows; row++)
            {
                int rowAddr = baseAddr + row * 16;
                if (rowAddr > 0xFFFF) break;
                Put(g, rowAddr.ToString("X4"), 0, y, RegionColor(machine, rowAddr), true);

                var ascii = new StringBuilder();
                string rowName = null;
                for (int c = 0; c < 16; c++)
                {
                    int a = rowAddr + c;
                    ushort ua = (ushort)a;
                    int col = 6 + c * 3;
                    int px = 4 + (int)Math.Round(col * cw);

                    Color back = Color.Empty;
                    if (a == cpu.PC) back = Theme.PcBack;
                    else if (a == cpu.X) back = Color.FromArgb(215, 232, 255);
                    else if (a == cpu.SP) back = Color.FromArgb(246, 218, 246);
                    if (machine.Bus.Watched[a]) back = Theme.WatchBack;
                    if (a == selected) back = Theme.SelectedBack;
                    if (back != Color.Empty)
                        using (var b = new SolidBrush(back))
                            g.FillRectangle(b, px - 1, y, (int)Math.Round(cw * 2) + 2, lh);

                    if (!machine.Bus.IsMapped(ua))
                    {
                        Put(g, "--", col, y, Color.FromArgb(185, 185, 185), false);
                        ascii.Append(' ');
                    }
                    else
                    {
                        byte v = machine.Bus.Peek(ua);
                        bool changed = a >= 0x20 && snapshot[a] != before[a];
                        Color color = changed ? Theme.Changed : v == 0 ? Color.FromArgb(175, 175, 175) : Color.Black;
                        Put(g, v.ToString("X2"), col, y, color, changed);
                        ascii.Append(v >= 0x20 && v < 0x7F ? (char)v : '.');
                    }
                    if (rowName == null) rowName = machine.SymbolFor(ua);
                }
                Put(g, ascii.ToString(), 6 + 16 * 3 + 1, y, Theme.Alu, false);
                if (rowName != null) Put(g, rowName, 6 + 16 * 3 + 18, y, Theme.Name, false);
                y += lh;
            }
        }

        // ---- interaction
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollRows(e.Delta > 0 ? -3 : 3);
        }

        void ScrollRows(int rows)
        {
            baseAddr = Math.Max(0, Math.Min(0xFFF0, baseAddr + rows * 16));
            Invalidate();
        }

        int AddressAt(Point p)
        {
            int row = (p.Y - 2) / lh - 1;
            if (row < 0 || row >= Rows) return -1;
            int col = (int)((p.X - 4) / cw) - 6;
            int c = col / 3;
            if (col < 0 || c > 15) return -1;
            int a = baseAddr + row * 16 + c;
            return a <= 0xFFFF ? a : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int a = AddressAt(e.Location);
            if (a >= 0) { selected = a; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (AddressAt(e.Location) >= 0) EditSelected();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int sel = selected < 0 ? baseAddr : selected;
            switch (e.KeyCode)
            {
                case Keys.Up: sel -= 16; break;
                case Keys.Down: sel += 16; break;
                case Keys.Left: sel -= 1; break;
                case Keys.Right: sel += 1; break;
                case Keys.PageUp: sel -= 16 * Rows; break;
                case Keys.PageDown: sel += 16 * Rows; break;
                case Keys.Enter: EditSelected(); e.Handled = true; return;
                default: return;
            }
            selected = Math.Max(0, Math.Min(0xFFFF, sel));
            if (selected < baseAddr) baseAddr = selected & 0xFFF0;
            else if (selected >= baseAddr + Rows * 16) baseAddr = Math.Min(0xFFF0, (selected & 0xFFF0) - (Rows - 1) * 16);
            baseAddr = Math.Max(0, baseAddr);
            e.Handled = true;
            Invalidate();
        }

        void EditSelected()
        {
            if (selected < 0) return;
            ushort a = (ushort)selected;
            string text = PromptDialog.Ask(FindForm(), "Write memory",
                $"New value for ${a:X4} ({machine.SymbolFor(a) ?? "no name"}), hex:", machine.Bus.Peek(a).ToString("X2"));
            if (text == null) return;
            if (!int.TryParse(text.Trim().TrimStart('$'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) || v < 0 || v > 255)
            {
                MessageBox.Show(FindForm(), "Enter a hex byte, 00 to FF.", "Write memory");
                return;
            }
            machine.Bus.Poke(a, (byte)v);
            Edited?.Invoke();
            Invalidate();
        }
    }

    // ====================================================================================================
    // Stack and call chain
    // ====================================================================================================

    /// <summary>
    /// Shows the 6803 stack above SP. Words that look like return addresses (the bytes just before them are a
    /// JSR or BSR) are listed as the call chain with the routine each one returns into; raw bytes follow.
    /// </summary>
    internal sealed class StackView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        public event Action<ushort> GoToRequested;

        public StackView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            list.Dock = DockStyle.Fill;
            list.AddColumn("Stack addr", 78);
            list.AddColumn("Kind", 70);
            list.AddColumn("Value", 60);
            list.AddColumn("Meaning", 360);
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ushort target) GoToRequested?.Invoke(target);
            };
            Controls.Add(list);
        }

        bool IsCallReturn(ushort ret, out ushort callSite, out string targetText)
        {
            callSite = 0;
            targetText = "";
            var bus = machine.Bus;
            ushort c3 = (ushort)(ret - 3);
            if (bus.Peek(c3) == 0xBD)
            {
                callSite = c3;
                ushort t = (ushort)((bus.Peek((ushort)(c3 + 1)) << 8) | bus.Peek((ushort)(c3 + 2)));
                targetText = machine.SymbolFor(t) ?? "$" + t.ToString("X4");
                return true;
            }
            ushort c2 = (ushort)(ret - 2);
            byte op = bus.Peek(c2);
            if (op == 0x8D)
            {
                callSite = c2;
                sbyte off = (sbyte)bus.Peek((ushort)(c2 + 1));
                ushort t = (ushort)(ret + off);
                targetText = machine.SymbolFor(t) ?? "$" + t.ToString("X4");
                return true;
            }
            if (op == 0xAD || op == 0x9D)
            {
                callSite = c2;
                targetText = op == 0xAD ? $"${bus.Peek((ushort)(c2 + 1)):X2},X (indirect)" : $"${bus.Peek((ushort)(c2 + 1)):X2} (direct)";
                return true;
            }
            return false;
        }

        public void Reload()
        {
            var cpu = machine.Cpu;
            ushort sp = cpu.SP;
            int top = machine.Config.RamBase + machine.Config.RamSize - 1;
            list.BeginUpdate();
            list.Items.Clear();

            var head = new ListViewItem(new[] { sp.ToString("X4"), "SP", "", sp <= top ? $"{top - sp} bytes used (stack grows down from ${top:X4})" : "SP is outside RAM" });
            head.ForeColor = Theme.Register;
            list.Items.Add(head);

            int shown = 0;
            for (int k = 0; k < 96 && shown < 24; k++)
            {
                ushort a = (ushort)(sp + 1 + k);
                if (a >= 0xFFFF) break;
                ushort w = (ushort)((machine.Bus.Peek(a) << 8) | machine.Bus.Peek((ushort)(a + 1)));
                if (!IsCallReturn(w, out ushort callSite, out string target)) continue;
                string where = machine.DescribeAddress(w) ?? "";
                var item = new ListViewItem(new[]
                {
                    a.ToString("X4"), "return", w.ToString("X4"),
                    $"back into {where}  (called at {callSite:X4}: {target})",
                });
                item.ForeColor = Theme.Call;
                item.Tag = w;
                list.Items.Add(item);
                shown++;
            }
            if (shown == 0) list.Items.Add(new ListViewItem(new[] { "", "", "", "no return addresses found above SP (the CPU is at top level)" }));

            // Raw bytes, 16 per row
            for (int row = 0; row < 4; row++)
            {
                int a = sp + 1 + row * 16;
                if (a > 0xFFFF) break;
                var sb = new StringBuilder();
                for (int c = 0; c < 16 && a + c <= 0xFFFF; c++) sb.Append(machine.Bus.Peek((ushort)(a + c)).ToString("X2")).Append(' ');
                var item = new ListViewItem(new[] { a.ToString("X4"), "bytes", "", sb.ToString() });
                item.ForeColor = Theme.Data;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
    }

    // ====================================================================================================
    // Hardware registers, vectors, keyboard
    // ====================================================================================================

    /// <summary>The 6803's internal registers decoded, the interrupt vectors, and what the keyboard matrix is doing.</summary>
    internal sealed class HardwareView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();

        public HardwareView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            list.Dock = DockStyle.Fill;
            list.AddColumn("Name", 90);
            list.AddColumn("Addr", 52);
            list.AddColumn("Value", 56);
            list.AddColumn("Meaning", 360);
            Controls.Add(list);
        }

        static string Bits(byte v, params string[] names)
        {
            var parts = new List<string>();
            for (int i = 0; i < names.Length; i++)
                if (names[i] != null && (v & (0x80 >> i)) != 0) parts.Add(names[i]);
            return parts.Count == 0 ? "-" : string.Join(" ", parts);
        }

        public static string KeyName(int index)
        {
            if (index == 0) return "@";
            if (index >= 1 && index <= 26) return ((char)('A' + index - 1)).ToString();
            if (index == 30) return "ENTER";
            if (index == 31) return "SPACE";
            if (index >= 32 && index <= 39) return ((char)('0' + index - 32)).ToString();
            if (index >= 40 && index <= 47) return "89:;,-./"[index - 40].ToString();
            return "key" + index;
        }

        void Add(string name, string addr, string value, string meaning, Color? color = null)
        {
            var item = new ListViewItem(new[] { name, addr, value, meaning });
            if (color.HasValue) item.ForeColor = color.Value;
            list.Items.Add(item);
        }

        public void Reload()
        {
            var cpu = machine.Cpu;
            var bus = machine.Bus;
            list.BeginUpdate();
            list.Items.Clear();

            Add("-- CPU internal registers --", "", "", "", Theme.Label);
            for (ushort r = 0; r < 0x15; r++)
            {
                string name = Hardware.NameFor(r);
                if (name == null) continue;
                byte v = bus.Peek(r);
                string meaning = "";
                switch (r)
                {
                    case 0x00: meaning = "port 1 direction (1 = output)"; break;
                    case 0x01: meaning = "port 2 direction (1 = output)"; break;
                    case 0x02:
                    {
                        var cols = new List<int>();
                        for (int c = 0; c < 8; c++) if ((v & (1 << c)) == 0) cols.Add(c);
                        meaning = cols.Count == 0 ? "no keyboard column strobed" : "keyboard column strobe low: " + string.Join(",", cols);
                        break;
                    }
                    case 0x03: meaning = "bit 1 low = Shift/Ctrl/Break down on the strobed column"; break;
                    case 0x08: meaning = "flags: " + Bits(v, "ICF", "OCF", "TOF", "EICI", "EOCI", "ETOI", "IEDG", "OLVL"); break;
                    case 0x09: meaning = $"free-running counter = {cpu.Frc:X4} (counts at E/4)"; break;
                    case 0x0B: meaning = $"output compare = {cpu.Ocr:X4}; counter-to-compare distance {(ushort)(cpu.Ocr - cpu.Frc)}"; break;
                    case 0x0D: meaning = $"input capture = {cpu.Icr:X4}"; break;
                    case 0x11: meaning = "serial status (TDRE set = ready)"; break;
                }
                Add(name, r.ToString("X4"), v.ToString("X2"), meaning);
            }

            Add("-- Interrupt vectors --", "", "", "", Theme.Label);
            string[] names = { "SCI", "TOI (timer overflow)", "OCI (output compare)", "ICI (input capture)", "IRQ", "SWI", "NMI", "RESET" };
            for (int i = 0; i < 8; i++)
            {
                ushort va = (ushort)(0xFFF0 + 2 * i);
                ushort target = (ushort)((bus.Peek(va) << 8) | bus.Peek((ushort)(va + 1)));
                Add("vec " + names[i].Split(' ')[0], va.ToString("X4"), target.ToString("X4"), machine.DescribeAddress(target) ?? names[i], Theme.Name);
            }

            Add("-- Keyboard --", "", "", "", Theme.Label);
            var kb = machine.Keyboard;
            var down = new List<string>();
            for (int c = 0; c < 8; c++)
            {
                byte rows = kb.ColumnRows(c);
                for (int r = 0; r < 6; r++)
                    if ((rows & (1 << r)) == 0) down.Add(KeyName(r * 8 + c));
            }
            if (kb.ShiftDown) down.Add("SHIFT");
            if (kb.CtrlDown) down.Add("CONTROL");
            if (kb.BreakDown) down.Add("BREAK");
            Add("keys down", "", "", down.Count == 0 ? "none" : string.Join(" ", down));
            Add("typing queue", "", kb.Pending.ToString(), kb.Pending == 0 ? "empty" : "keys still waiting to be pressed");
            Add("matrix cols", "BFFF", bus.Peek(0xBFFF).ToString("X2"), "row bits read at $BFFF (low = key down in the strobed column)");

            Add("-- Other devices --", "", "", "", Theme.Label);
            Add("video ctl", machine.Config.VideoControl.ToString("X4"), machine.VideoControl.Value.ToString("X2"), "video control latch (colour set / mode)");
            list.EndUpdate();
        }
    }

    // ====================================================================================================
    // Profile
    // ====================================================================================================

    /// <summary>Which routines the CPU spends its time in since the profile was cleared.</summary>
    internal sealed class ProfileView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        public event Action<ushort> GoToRequested;

        public ProfileView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
            var clear = new Button { Text = "Clear profile", AutoSize = true };
            clear.Click += (s, e) => { machine.ClearProfile(); Reload(); };
            bar.Controls.Add(clear);
            bar.Controls.Add(new Label
            {
                Text = "Instructions executed per routine since cleared. Double-click to open in the listing.",
                AutoSize = true,
                Padding = new Padding(8, 7, 0, 0),
            });
            list.Dock = DockStyle.Fill;
            list.AddColumn("Routine", 220);
            list.AddColumn("Addr", 52);
            list.AddColumn("Instructions", 100);
            list.AddColumn("Share", 200);
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ushort a) GoToRequested?.Invoke(a);
            };
            Controls.Add(list);
            Controls.Add(bar);
            list.BringToFront();
        }

        public void Reload()
        {
            List<ProfileEntry> all = machine.BuildProfile();
            long total = all.Sum(p => p.Count);
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ProfileEntry p in all.Take(80))
            {
                double share = total == 0 ? 0 : 100.0 * p.Count / total;
                string bar = new string('#', (int)Math.Round(share / 100.0 * 24)) + $" {share:0.0}%";
                var item = new ListViewItem(new[] { p.Name, p.Start.ToString("X4"), p.Count.ToString("N0", CultureInfo.InvariantCulture), bar });
                item.Tag = p.Start;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
    }

    // ====================================================================================================
    // BASIC handlers: break when a BASIC statement or function runs
    // ====================================================================================================

    /// <summary>
    /// Check a BASIC statement, function or operator to put a breakpoint on its handler in the ROM; then type
    /// that command in the emulator and the CPU stops where the firmware starts executing it.
    /// The names come from the symbol file (stm_PRINT, fn_SIN, op_ADD ...).
    /// </summary>
    internal sealed class BasicHandlersView : Panel
    {
        sealed class Entry
        {
            public ushort Address;
            public string Display;
            public int Order;
        }

        readonly Machine machine;
        readonly CheckedListBox list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        readonly Label help;
        readonly List<Entry> entries = new List<Entry>();
        int builtVersion = -1;
        bool syncing;

        public event Action BreakpointsChanged;
        public event Action<ushort> GoToRequested;

        public BasicHandlersView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            list.Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
            help = new Label
            {
                Dock = DockStyle.Top,
                Height = 58,
                Padding = new Padding(6, 4, 6, 0),
                Text = "Tick a BASIC statement, function or operator to stop the CPU at the start of its handler in the ROM. " +
                       "Then run the matching BASIC command (for example PRINT) and the debugger opens on it. " +
                       "Double-click an entry to see its code.",
            };
            list.ItemCheck += (s, e) =>
            {
                if (syncing || e.Index < 0 || e.Index >= entries.Count) return;
                ushort a = entries[e.Index].Address;
                if (e.NewValue == CheckState.Checked)
                {
                    if (machine.HasBreakpoint(a)) machine.SetBreakpointEnabled(a, true); else machine.Breakpoints.Add(a);
                }
                else machine.RemoveBreakpoint(a);
                BeginInvoke(new Action(() => BreakpointsChanged?.Invoke()));
            };
            list.DoubleClick += (s, e) =>
            {
                int i = list.SelectedIndex;
                if (i >= 0 && i < entries.Count) GoToRequested?.Invoke(entries[i].Address);
            };
            Controls.Add(list);
            Controls.Add(help);
            list.BringToFront();
        }

        static bool IsAutoName(string rest) =>
            rest.Length == 4 && rest.All(Uri.IsHexDigit);

        void Build()
        {
            builtVersion = machine.SymbolVersion;
            entries.Clear();
            foreach (var kv in machine.Symbols)
            {
                string n = kv.Value;
                string kind = null;
                int order = 0;
                if (n.StartsWith("stm_", StringComparison.Ordinal)) { kind = "statement"; order = 0; }
                else if (n.StartsWith("fn_", StringComparison.Ordinal)) { kind = "function"; order = 1; }
                else if (n.StartsWith("op_", StringComparison.Ordinal)) { kind = "operator"; order = 2; }
                if (kind == null) continue;
                string rest = n.Substring(n.IndexOf('_') + 1);
                if (IsAutoName(rest) && kind == "function") continue;          // analyzer's fn_XXXX names
                entries.Add(new Entry { Address = kv.Key, Display = $"{rest,-10} {kind,-10} ${kv.Key:X4}", Order = order });
            }
            entries.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Display, b.Display));
            syncing = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Entry e in entries) list.Items.Add(e.Display);
            list.EndUpdate();
            syncing = false;
        }

        public void Reload()
        {
            if (builtVersion != machine.SymbolVersion) Build();
            if (entries.Count == 0)
            {
                help.Text = "No BASIC handler names are loaded. Load symbols/mc10.sym (File > Load symbols) for the MC-10 ROM.";
                return;
            }
            syncing = true;
            for (int i = 0; i < entries.Count; i++)
            {
                bool on = machine.Breakpoints.Contains(entries[i].Address);
                if (list.GetItemChecked(i) != on) list.SetItemChecked(i, on);
            }
            syncing = false;
        }
    }

    // ====================================================================================================
    // Breakpoints
    // ====================================================================================================

    /// <summary>All breakpoints with enable/disable check boxes, their conditions and hit counts.</summary>
    internal sealed class BreakpointsView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        bool syncing;

        public event Action Changed;
        public event Action<ushort> GoToRequested;

        public BreakpointsView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
            AddButton(bar, "Add...", (s, e) => AddByText());
            AddButton(bar, "Condition...", (s, e) => EditCondition());
            AddButton(bar, "Remove", (s, e) => RemoveSelected());
            AddButton(bar, "Clear all", (s, e) => { machine.ClearBreakpoints(); Changed?.Invoke(); });
            list.Dock = DockStyle.Fill;
            list.CheckBoxes = true;
            list.AddColumn("Addr", 56);
            list.AddColumn("Name", 170);
            list.AddColumn("Condition", 120);
            list.AddColumn("Hits", 50);
            list.ItemChecked += (s, e) =>
            {
                if (syncing || !(e.Item.Tag is ushort a)) return;
                if (machine.Breakpoints.Contains(a) == e.Item.Checked) return;      // nothing to change
                machine.SetBreakpointEnabled(a, e.Item.Checked);
                BeginInvoke(new Action(() => Changed?.Invoke()));
            };
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ushort a) GoToRequested?.Invoke(a);
            };
            Controls.Add(list);
            Controls.Add(bar);
            list.BringToFront();
        }

        static void AddButton(Control parent, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            parent.Controls.Add(b);
        }

        ushort? Selected()
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ushort a) return a;
            return null;
        }

        void AddByText()
        {
            string t = PromptDialog.Ask(FindForm(), "Add breakpoint", "Address (hex) or label name, e.g. E71F or stm_PRINT:", "");
            if (string.IsNullOrWhiteSpace(t)) return;
            if (!machine.TryResolve(t, out ushort a))
            {
                MessageBox.Show(FindForm(), $"'{t.Trim()}' is not an address or a known label.", "Add breakpoint");
                return;
            }
            if (!machine.HasBreakpoint(a)) machine.Breakpoints.Add(a);
            Changed?.Invoke();
        }

        void EditCondition()
        {
            ushort? a = Selected();
            if (!a.HasValue) return;
            string current = machine.BreakConditions.TryGetValue(a.Value, out string c) ? c : "";
            string text = PromptDialog.Ask(FindForm(), "Breakpoint condition",
                "Stop only when (A==0D, X>=4200, [00F3]!=0, CC&04). Empty = always.", current);
            if (text == null) return;
            text = text.Trim();
            if (text.Length > 0 && !BreakCondition.IsValid(text, machine))
            {
                MessageBox.Show(FindForm(), "That is not a valid condition.", "Breakpoint condition");
                return;
            }
            if (text.Length == 0) machine.BreakConditions.Remove(a.Value); else machine.BreakConditions[a.Value] = text;
            Changed?.Invoke();
        }

        void RemoveSelected()
        {
            ushort? a = Selected();
            if (!a.HasValue) return;
            machine.RemoveBreakpoint(a.Value);
            Changed?.Invoke();
        }

        public void Reload()
        {
            syncing = true;
            ushort? keep = Selected();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ushort a in machine.AllBreakpoints())
            {
                machine.BreakConditions.TryGetValue(a, out string cond);
                machine.BreakHits.TryGetValue(a, out int hits);
                var item = new ListViewItem(new[] { a.ToString("X4"), machine.DescribeAddress(a) ?? "", cond ?? "", hits.ToString() });
                item.Tag = a;
                item.Checked = machine.Breakpoints.Contains(a);
                if (keep.HasValue && keep.Value == a) item.Selected = true;
                list.Items.Add(item);
            }
            list.EndUpdate();
            syncing = false;
        }
    }

    // ====================================================================================================
    // Write watchpoints
    // ====================================================================================================

    /// <summary>Addresses that stop the machine when written (for example a BASIC variable or a screen cell).</summary>
    internal sealed class WatchView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        readonly TextBox box = new TextBox { Width = 120, Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        public event Action Changed;

        public WatchView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
            bar.Controls.Add(new Label { Text = "Address / label", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            bar.Controls.Add(box);
            AddButton(bar, "Add write-watch", (s, e) => Add());
            AddButton(bar, "Remove", (s, e) => Remove());
            list.Dock = DockStyle.Fill;
            list.AddColumn("Addr", 56);
            list.AddColumn("Name", 170);
            list.AddColumn("Value", 60);
            list.AddColumn("Last writer PC", 120);
            Controls.Add(list);
            Controls.Add(bar);
            list.BringToFront();
        }

        static void AddButton(Control parent, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            parent.Controls.Add(b);
        }

        void Add()
        {
            if (!machine.TryResolve(box.Text, out ushort a))
            {
                MessageBox.Show(FindForm(), "Enter a hex address or a known label.", "Watchpoint");
                return;
            }
            machine.Bus.Watched[a] = true;
            Changed?.Invoke();
        }

        void Remove()
        {
            if (list.SelectedItems.Count == 0 || !(list.SelectedItems[0].Tag is ushort a)) return;
            machine.Bus.Watched[a] = false;
            Changed?.Invoke();
        }

        public void Reload()
        {
            list.BeginUpdate();
            list.Items.Clear();
            for (int a = 0; a < 0x10000; a++)
            {
                if (!machine.Bus.Watched[a]) continue;
                ushort ua = (ushort)a;
                string writer = machine.LastWatchAddress == ua ? "(stopped here last)" : "";
                var item = new ListViewItem(new[] { a.ToString("X4"), machine.SymbolFor(ua) ?? "", machine.Bus.Peek(ua).ToString("X2"), writer });
                item.Tag = ua;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
    }

    // ====================================================================================================
    // History
    // ====================================================================================================

    /// <summary>The most recent instructions, newest first, with the register values each one saw.</summary>
    internal sealed class HistoryView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        public event Action<ushort> GoToRequested;

        public HistoryView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            list.Dock = DockStyle.Fill;
            list.AddColumn("PC", 46);
            list.AddColumn("Where", 140);
            list.AddColumn("Instruction", 170);
            list.AddColumn("A", 30);
            list.AddColumn("B", 30);
            list.AddColumn("X", 42);
            list.AddColumn("SP", 42);
            list.AddColumn("CC", 30);
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ushort a) GoToRequested?.Invoke(a);
            };
            Controls.Add(list);
        }

        public void Reload()
        {
            var all = new List<HistoryEntry>(machine.History);
            list.BeginUpdate();
            list.Items.Clear();
            int shown = 0;
            for (int i = all.Count - 1; i >= 0 && shown < 200; i--, shown++)
            {
                HistoryEntry h = all[i];
                string text = h.Describe(machine.SymbolFor, machine.CodeSymbolFor);
                string mnemonic = text.Split(' ')[0];
                var item = new ListViewItem(new[]
                {
                    h.Pc.ToString("X4"), machine.DescribeAddress(h.Pc) ?? "", text,
                    h.A.ToString("X2"), h.B.ToString("X2"), h.X.ToString("X4"), h.Sp.ToString("X4"), h.Cc.ToString("X2"),
                });
                item.ForeColor = CodePainter.MnemonicColor(mnemonic, out bool unusedBold);
                item.Tag = h.Pc;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
    }
}
