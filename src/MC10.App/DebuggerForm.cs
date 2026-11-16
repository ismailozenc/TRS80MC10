using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    /// <summary>
    /// Debugger window: disassembly with breakpoints, registers, hex memory with poke,
    /// write watchpoints, and the recent instruction history.
    /// Keys: F5 run/pause, F10 step, F11 step over, Ctrl+B toggle breakpoint at the selected line.
    /// </summary>
    public sealed class DebuggerForm : Form
    {
        readonly EmulatorSession session;
        readonly Machine machine;

        readonly ListBox disasm;
        readonly List<ushort> disasmAddrs = new List<ushort>();
        readonly TextBox regs, memory, history, pokeAddr, pokeValue, memAddr, watchAddr;
        readonly ListBox watches;
        readonly Label status;
        readonly Button runBtn;
        readonly Timer refresh;
        ushort memBase;
        readonly Font mono = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);

        public DebuggerForm(EmulatorSession session, Form owner)
        {
            this.session = session;
            machine = session.Machine;
            Text = "MC-10 Debugger";
            KeyPreview = true;
            Size = new Size(1100, 720);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(owner.Left + owner.Width + 8, owner.Top);

            // ---- toolbar
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
            runBtn = Btn(bar, "Run (F5)", (s, e) => session.Toggle());
            Btn(bar, "Step (F10)", (s, e) => session.StepInstruction());
            Btn(bar, "Step over (F11)", (s, e) => session.StepOverInstruction());
            Btn(bar, "Run to cursor", (s, e) => RunToCursor());
            Btn(bar, "Toggle BP (Ctrl+B)", (s, e) => ToggleBreakpointAtSelection());
            Btn(bar, "Reset", (s, e) => session.HardReset());
            Btn(bar, "Clear BPs", (s, e) => { machine.Breakpoints.Clear(); RefreshAll(); });

            status = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };

            // ---- left: disassembly
            disasm = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = mono,
                IntegralHeight = false,
                HorizontalScrollbar = true,
            };
            disasm.DoubleClick += (s, e) => ToggleBreakpointAtSelection();

            // ---- right column
            var right = new TableLayoutPanel { Dock = DockStyle.Right, Width = 430, ColumnCount = 1, RowCount = 6 };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

            right.Controls.Add(Group("Registers", regs = Mono(readOnly: true)), 0, 0);
            regs.Dock = DockStyle.Fill;

            var memPanel = new Panel { Dock = DockStyle.Fill };
            var memRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30 };
            memAddr = new TextBox { Width = 70, Text = "0000", Font = mono };
            memRow.Controls.Add(new Label { Text = "Addr", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            memRow.Controls.Add(memAddr);
            Btn(memRow, "Show", (s, e) => { memBase = ParseHex(memAddr.Text); RefreshMemory(); });
            Btn(memRow, "PC", (s, e) => { memBase = machine.Cpu.PC; memAddr.Text = memBase.ToString("X4"); RefreshMemory(); });
            memPanel.Controls.Add(memory = Mono(readOnly: true));
            memory.Dock = DockStyle.Fill;
            memPanel.Controls.Add(memRow);
            right.Controls.Add(Group("Memory", memPanel), 0, 1);

            var pokeRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
            pokeRow.Controls.Add(new Label { Text = "Addr", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            pokeAddr = new TextBox { Width = 60, Font = mono };
            pokeRow.Controls.Add(pokeAddr);
            pokeRow.Controls.Add(new Label { Text = "Value", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            pokeValue = new TextBox { Width = 40, Font = mono };
            pokeRow.Controls.Add(pokeValue);
            Btn(pokeRow, "Poke", (s, e) => PokeMemory());
            right.Controls.Add(Group("Write memory (hex)", pokeRow), 0, 2);

            var watchPanel = new Panel { Dock = DockStyle.Fill };
            var watchRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30 };
            watchAddr = new TextBox { Width = 70, Font = mono };
            watchRow.Controls.Add(watchAddr);
            Btn(watchRow, "Add write-watch", (s, e) => AddWatch());
            Btn(watchRow, "Remove", (s, e) => RemoveWatch());
            watches = new ListBox { Dock = DockStyle.Fill, Font = mono };
            watchPanel.Controls.Add(watches);
            watchPanel.Controls.Add(watchRow);
            right.Controls.Add(Group("Watchpoints (stop on write)", watchPanel), 0, 3);

            history = Mono(readOnly: true);
            history.Dock = DockStyle.Fill;
            right.Controls.Add(Group("History (last instructions)", history), 0, 4);

            Controls.Add(disasm);
            Controls.Add(right);
            Controls.Add(bar);
            Controls.Add(status);
            // Dock order: the fill control must come last among docked children.
            disasm.BringToFront();

            refresh = new Timer { Interval = 150 };
            refresh.Tick += (s, e) => RefreshAll();
            refresh.Start();

            KeyDown += (s, e) => HandleKey(e);
            FormClosing += (s, e) => { refresh.Stop(); };
            RefreshAll();
        }

        // ------------------------------------------------------------ helpers
        static Button Btn(Control parent, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        TextBox Mono(bool readOnly) => new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = readOnly,
            Font = mono,
            WordWrap = false,
        };

        static GroupBox Group(string title, Control inner)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Fill };
            inner.Dock = DockStyle.Fill;
            g.Controls.Add(inner);
            return g;
        }

        static ushort ParseHex(string s)
        {
            s = (s ?? "").Trim().TrimStart('$');
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort v) ? v : (ushort)0;
        }

        void HandleKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F5: session.Toggle(); e.Handled = true; break;
                case Keys.F10: session.StepInstruction(); e.Handled = true; break;
                case Keys.F11: session.StepOverInstruction(); e.Handled = true; break;
                case Keys.B when e.Control: ToggleBreakpointAtSelection(); e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------ actions
        void ToggleBreakpointAtSelection()
        {
            int i = disasm.SelectedIndex;
            if (i < 0 || i >= disasmAddrs.Count) return;
            ushort a = disasmAddrs[i];
            if (!machine.Breakpoints.Remove(a)) machine.Breakpoints.Add(a);
            RefreshAll();
        }

        void RunToCursor()
        {
            int i = disasm.SelectedIndex;
            if (i < 0 || i >= disasmAddrs.Count) return;
            session.Pause();
            StopReason r = machine.RunTo(disasmAddrs[i], 5_000_000);
            session.NotifyChanged();
            if (r != StopReason.Breakpoint && r != StopReason.None)
                status.Text = $"Stopped: {r}";
        }

        void PokeMemory()
        {
            ushort a = ParseHex(pokeAddr.Text);
            byte v = (byte)ParseHex(pokeValue.Text);
            machine.Bus.Poke(a, v);
            RefreshAll();
        }

        void AddWatch()
        {
            ushort a = ParseHex(watchAddr.Text);
            machine.Bus.Watched[a] = true;
            RefreshAll();
        }

        void RemoveWatch()
        {
            int i = watches.SelectedIndex;
            if (i < 0) return;
            ushort a = ParseHex(watches.Items[i].ToString().Substring(0, 4));
            machine.Bus.Watched[a] = false;
            RefreshAll();
        }

        // ------------------------------------------------------------ rendering
        public void RefreshAll()
        {
            if (IsDisposed) return;
            RefreshDisasm();
            RefreshRegs();
            RefreshHistory();
            RefreshWatches();
            RefreshMemory();
            runBtn.Text = session.Running ? "Pause (F5)" : "Run (F5)";
            status.Text = $"{(session.Running ? "RUNNING" : "paused")}   last stop: {machine.LastStop}   cycles: {machine.Cpu.Cycles:N0}" +
                          (machine.Rom != null ? $"   ROM CRC32 {machine.Rom.Crc32():X8}" : "   no ROM");
        }

        void RefreshDisasm()
        {
            ushort pc = machine.Cpu.PC;
            // Show a window of instructions starting 8 instructions before PC is not decodable
            // backwards, so the window starts at PC; the selected line is kept if still present.
            ushort keep = disasmAddrs.Count > 0 && disasm.SelectedIndex >= 0 ? disasmAddrs[disasm.SelectedIndex] : pc;
            disasm.BeginUpdate();
            disasm.Items.Clear();
            disasmAddrs.Clear();
            ushort a = pc;
            for (int n = 0; n < 60; n++)
            {
                string text = Disassembler.Disassemble(machine.Bus.Read, a, out int len, machine.SymbolFor);
                string mark = a == pc ? ">" : " ";
                string bp = machine.Breakpoints.Contains(a) ? "*" : " ";
                string sym = machine.SymbolFor(a);
                string label = sym != null ? $"{sym}:" : "";
                disasm.Items.Add($"{mark}{bp} {a:X4}  {Disassembler.HexBytes(machine.Bus.Read, a, len),-9} {label,-10}{text}");
                disasmAddrs.Add(a);
                a = (ushort)(a + len);
            }
            int sel = disasmAddrs.IndexOf(keep);
            disasm.SelectedIndex = sel >= 0 ? sel : 0;
            disasm.EndUpdate();
        }

        void RefreshRegs()
        {
            var c = machine.Cpu;
            var sb = new StringBuilder();
            sb.AppendLine($"PC  {c.PC:X4}    SP {c.SP:X4}");
            sb.AppendLine($"A   {c.A:X2}      B  {c.B:X2}      D {c.D:X4}");
            sb.AppendLine($"X   {c.X:X4}");
            byte cc = (byte)(c.CC | 0xC0);
            sb.AppendLine($"CC  {cc:X2}  H{Bit(cc, 0x20)} I{Bit(cc, 0x10)} N{Bit(cc, 0x08)} Z{Bit(cc, 0x04)} V{Bit(cc, 0x02)} C{Bit(cc, 0x01)}");
            sb.AppendLine($"cycles {c.Cycles:N0}   instructions {c.Instructions:N0}");
            sb.AppendLine($"illegal opcodes {c.IllegalCount}" + (c.IllegalCount > 0 ? $" (last at {c.LastIllegalPc:X4})" : ""));
            sb.AppendLine($"halted (WAI) {c.Halted}   NMI pending {c.NmiPending}   IRQ line {c.IrqLine}");
            sb.AppendLine($"timer FRC {c.Frc:X4}  OCR {c.Ocr:X4}  TCSR1 {c.Io[8]:X2}");
            regs.Text = sb.ToString();
        }

        static string Bit(byte v, int mask) => (v & mask) != 0 ? "1" : "0";

        void RefreshMemory()
        {
            var sb = new StringBuilder();
            for (int row = 0; row < 16; row++)
            {
                ushort baseAddr = (ushort)(memBase + row * 16);
                sb.Append(baseAddr.ToString("X4")).Append("  ");
                var ascii = new StringBuilder();
                for (int col = 0; col < 16; col++)
                {
                    byte v = machine.Bus.Read((ushort)(baseAddr + col));
                    sb.Append(v.ToString("X2")).Append(' ');
                    ascii.Append(v >= 0x20 && v < 0x7F ? (char)v : '.');
                }
                sb.Append(' ').Append(ascii).AppendLine();
            }
            memory.Text = sb.ToString();
        }

        void RefreshHistory()
        {
            var sb = new StringBuilder();
            var list = new List<HistoryEntry>(machine.History);
            int start = Math.Max(0, list.Count - 100);
            for (int i = start; i < list.Count; i++)
            {
                var h = list[i];
                sb.AppendLine($"{h.Pc:X4}  {h.Text,-26} A={h.A:X2} B={h.B:X2} X={h.X:X4} SP={h.Sp:X4} CC={h.Cc:X2}");
            }
            history.Text = sb.ToString();
            history.SelectionStart = history.TextLength;
            history.ScrollToCaret();
        }

        void RefreshWatches()
        {
            var keep = watches.SelectedIndex;
            watches.BeginUpdate();
            watches.Items.Clear();
            for (int a = 0; a < 0x10000; a++)
            {
                if (!machine.Bus.Watched[a]) continue;
                watches.Items.Add($"{a:X4}  now={machine.Bus.Read((ushort)a):X2}");
            }
            if (keep >= 0 && keep < watches.Items.Count) watches.SelectedIndex = keep;
            watches.EndUpdate();
        }
    }
}
