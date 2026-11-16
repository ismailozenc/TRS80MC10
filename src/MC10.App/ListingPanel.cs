using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    /// <summary>List box that double-buffers to avoid flicker while the PC highlight moves.</summary>
    internal sealed class FlickerFreeList : ListBox
    {
        public FlickerFreeList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }
    }

    /// <summary>
    /// Whole-ROM disassembly view: labels, subroutine banners, data, a red dot for breakpoints and a
    /// yellow bar on the current PC. Double-click a code line to toggle a breakpoint.
    /// </summary>
    public sealed class ListingPanel : Panel
    {
        readonly Machine machine;
        readonly Action<ushort> toggleBreakpoint;
        readonly FlickerFreeList list;
        readonly TextBox goBox;
        readonly CheckBox follow;
        readonly Label info;
        readonly Font mono = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
        readonly Font monoBold = new Font("Consolas", 10f, FontStyle.Bold, GraphicsUnit.Point);
        List<ListingLine> lines = new List<ListingLine>();
        Analysis built;
        int lastPc = -1;

        public ListingPanel(Machine machine, Action<ushort> toggleBreakpoint)
        {
            this.machine = machine;
            this.toggleBreakpoint = toggleBreakpoint;
            Dock = DockStyle.Fill;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
            bar.Controls.Add(new Label { Text = "Go to", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            goBox = new TextBox { Width = 130, Font = mono };
            goBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { GoToText(goBox.Text); e.Handled = e.SuppressKeyPress = true; }
            };
            bar.Controls.Add(goBox);
            var go = new Button { Text = "Go", AutoSize = true };
            go.Click += (s, e) => GoToText(goBox.Text);
            bar.Controls.Add(go);
            var pc = new Button { Text = "PC", AutoSize = true };
            pc.Click += (s, e) => GoTo(machine.Cpu.PC);
            bar.Controls.Add(pc);
            follow = new CheckBox { Text = "Follow PC", Checked = true, AutoSize = true, Padding = new Padding(8, 5, 0, 0) };
            bar.Controls.Add(follow);
            info = new Label { AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
            bar.Controls.Add(info);

            list = new FlickerFreeList
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                HorizontalExtent = 1000,
                Font = mono,
            };
            list.ItemHeight = mono.Height + 2;
            list.DrawItem += DrawLine;
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItem is ListingLine l && l.Kind == LineKind.Code) toggleBreakpoint(l.Address);
            };

            Controls.Add(list);
            Controls.Add(bar);
            list.BringToFront();
        }

        // ------------------------------------------------------------ data
        void Rebuild()
        {
            built = machine.Analysis;
            lines = machine.BuildListing();
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(lines.Cast<object>().ToArray());
            list.EndUpdate();
            var an = machine.Analysis;
            info.Text = an == null ? "no ROM loaded"
                : $"{an.InstructionCount} instructions, {an.Subroutines.Count} subroutines, {an.Labels.Count} labels";
            lastPc = -1;
        }

        /// <summary>Call whenever machine state changes. Cheap unless the ROM or the PC changed.</summary>
        public void Sync(bool running)
        {
            if (!Visible) return;
            if (!ReferenceEquals(built, machine.Analysis)) Rebuild();
            int pc = machine.Cpu.PC;
            if (follow.Checked && !running && pc != lastPc) GoTo((ushort)pc);
            lastPc = pc;
            list.Invalidate();
        }

        // ------------------------------------------------------------ navigation
        int FindIndex(ushort addr)
        {
            int lo = 0, hi = lines.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid].Address >= addr) { ans = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return ans;
        }

        public void GoTo(ushort addr)
        {
            if (!ReferenceEquals(built, machine.Analysis)) Rebuild();
            int idx = FindIndex(addr);
            if (idx < 0) return;
            // Select the code line itself (the banner/label lines above it share the address).
            while (idx + 1 < lines.Count && lines[idx].Address == addr && lines[idx].Kind != LineKind.Code
                   && lines[idx].Kind != LineKind.Data) idx++;
            list.SelectedIndex = idx;
            int rows = Math.Max(4, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
            list.TopIndex = Math.Max(0, idx - rows / 3);
        }

        void GoToText(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            string hex = text.TrimStart('$');
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort a)
                && (hex.Length >= 3 || !AnyNamed(text)))
            {
                GoTo(a);
                return;
            }
            foreach (var kv in machine.Symbols.Concat(machine.AutoSymbols))
                if (string.Equals(kv.Value, text, StringComparison.OrdinalIgnoreCase)) { GoTo(kv.Key); return; }
            info.Text = $"'{text}' not found";
        }

        bool AnyNamed(string text) =>
            machine.Symbols.Values.Concat(machine.AutoSymbols.Values).Any(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------ drawing
        static Color ColorFor(LineKind k)
        {
            switch (k)
            {
                case LineKind.Header:
                case LineKind.Comment: return Color.FromArgb(0, 128, 0);
                case LineKind.Label: return Color.FromArgb(0, 0, 160);
                case LineKind.Data: return Color.FromArgb(110, 110, 110);
                default: return Color.Black;
            }
        }

        void DrawLine(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lines.Count) return;
            ListingLine l = lines[e.Index];
            bool isCode = l.Kind == LineKind.Code;
            bool atPc = isCode && l.Address == machine.Cpu.PC;
            bool bp = isCode && machine.Breakpoints.Contains(l.Address);
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = atPc ? Color.FromArgb(255, 240, 150) : selected ? SystemColors.Highlight : SystemColors.Window;
            Color fore = selected && !atPc ? SystemColors.HighlightText : ColorFor(l.Kind);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (bp)
                using (var r = new SolidBrush(Color.Crimson))
                    e.Graphics.FillEllipse(r, e.Bounds.X + 3, e.Bounds.Y + 3, 9, 9);
            TextRenderer.DrawText(e.Graphics, l.Text, l.Kind == LineKind.Label ? monoBold : mono,
                new Point(e.Bounds.X + 16, e.Bounds.Y + 1), fore, TextFormatFlags.NoPadding);
        }
    }
}
