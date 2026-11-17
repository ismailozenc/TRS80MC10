using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    /// <summary>
    /// Autos: the registers and memory used by the instruction that just ran and the one about to run,
    /// with their current values. Values that changed since the previous stop are red.
    /// </summary>
    internal sealed class AutosView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        Dictionary<string, string> before = new Dictionary<string, string>();
        Dictionary<string, string> current = new Dictionary<string, string>();
        long lastCycles = -1;

        /// <summary>Double-click: the address and whether it is code (an instruction row) or data.</summary>
        public event Action<ushort, bool> GoToRequested;

        public AutosView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;
            list.Dock = DockStyle.Fill;
            list.AddColumn("Name", 130);
            list.AddColumn("Value", 60);
            list.AddColumn("Detail", 380);
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is AutoItem a && a.HasAddress)
                    GoToRequested?.Invoke(a.Address, a.IsInstruction);
            };
            Controls.Add(list);
        }

        public void Reload()
        {
            List<AutoItem> items = InstructionGuide.Autos(machine);
            if (machine.Cpu.Cycles != lastCycles)
            {
                before = current;
                current = new Dictionary<string, string>();
                foreach (AutoItem a in items)
                    if (!a.IsInstruction) current[a.Name] = a.Value;
                lastCycles = machine.Cpu.Cycles;
            }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (AutoItem a in items)
            {
                var row = new ListViewItem(new[] { a.Name, a.Value, a.Detail }) { Tag = a };
                if (a.IsInstruction) row.ForeColor = Theme.Name;
                else if (before.TryGetValue(a.Name, out string old) && old != a.Value) row.ForeColor = Theme.Changed;
                list.Items.Add(row);
            }
            list.EndUpdate();
        }
    }

    /// <summary>
    /// Watch list: expressions that are re-evaluated at every stop (and four times a second while running).
    /// See <see cref="WatchExpression"/> for the syntax. Changed values are red.
    /// </summary>
    internal sealed class WatchListView : Panel
    {
        readonly Machine machine;
        readonly BufferedListView list = new BufferedListView();
        readonly TextBox box = new TextBox { Width = 150, Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        readonly List<string> expressions = new List<string>();
        Dictionary<string, int> before = new Dictionary<string, int>();
        Dictionary<string, int> current = new Dictionary<string, int>();
        long lastCycles = -1;

        public event Action<ushort> GoToRequested;

        public WatchListView(Machine machine)
        {
            this.machine = machine;
            Dock = DockStyle.Fill;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2), WrapContents = false };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { AddFromBox(); e.Handled = e.SuppressKeyPress = true; }
            };
            bar.Controls.Add(box);
            AddButton(bar, "Add", (s, e) => AddFromBox());
            AddButton(bar, "Remove", (s, e) => RemoveSelected());
            AddButton(bar, "Clear", (s, e) => { expressions.Clear(); Reload(); });
            AddButton(bar, "BASIC pointers", (s, e) => AddBasicSet());

            var help = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                Padding = new Padding(6, 4, 6, 0),
                Text = "Examples:  A   X   cursor_addr.w   4280   [X]   [SP+1].w   [txtptr.w]   4000.s\r\n" +
                       "A name or address shows the byte stored there; .w = two bytes, .s = text, [ ] = memory at that address.\r\n" +
                       "Double-click a row to see it in the Memory tab.",
            };

            list.Dock = DockStyle.Fill;
            list.AddColumn("Expression", 140);
            list.AddColumn("Hex", 56);
            list.AddColumn("Decimal", 64);
            list.AddColumn("Text / points to", 170);
            list.AddColumn("Read from", 130);
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is WatchResult r && r.Ok)
                    GoToRequested?.Invoke(r.HasAddress ? r.Address : (ushort)r.Value);
            };
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            };

            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(help);
            list.BringToFront();
        }

        static void AddButton(Control parent, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            parent.Controls.Add(b);
        }

        /// <summary>Add an expression (ignored if already present).</summary>
        public void AddExpression(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || expressions.Contains(text)) return;
            expressions.Add(text);
        }

        void AddFromBox()
        {
            string text = box.Text.Trim();
            if (text.Length == 0) return;
            WatchResult r = WatchExpression.Evaluate(text, machine);
            if (!r.Ok)
            {
                MessageBox.Show(FindForm(), "Cannot watch that: " + r.Error, "Watch");
                return;
            }
            AddExpression(text);
            box.Clear();
            Reload();
        }

        void RemoveSelected()
        {
            if (list.SelectedIndices.Count == 0) return;
            int i = list.SelectedIndices[0];
            if (i >= 0 && i < expressions.Count) expressions.RemoveAt(i);
            Reload();
        }

        /// <summary>The pointers that show what BASIC is doing; only those whose labels are loaded are added.</summary>
        void AddBasicSet()
        {
            string[] set =
            {
                "txtptr.w", "[txtptr.w]", "cursor_addr.w", "basic_txttab.w", "basic_vartab.w", "basic_arytab.w",
                "basic_aryend.w", "basic_fretop.w", "basic_memtop.w",
            };
            int added = 0;
            foreach (string s in set)
                if (WatchExpression.Evaluate(s, machine).Ok) { AddExpression(s); added++; }
            if (added == 0)
                MessageBox.Show(FindForm(), "These need the labels in symbols/mc10.sym, which are not loaded.", "Watch");
            Reload();
        }

        public void Reload()
        {
            var results = new List<WatchResult>();
            foreach (string e in expressions) results.Add(WatchExpression.Evaluate(e, machine));

            if (machine.Cpu.Cycles != lastCycles)
            {
                before = current;
                current = new Dictionary<string, int>();
                for (int i = 0; i < expressions.Count; i++)
                    if (results[i].Ok) current[expressions[i]] = results[i].Value;
                lastCycles = machine.Cpu.Cycles;
            }

            int selected = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < expressions.Count; i++)
            {
                WatchResult r = results[i];
                ListViewItem row;
                if (!r.Ok)
                {
                    row = new ListViewItem(new[] { expressions[i], "?", "", r.Error, "" });
                    row.ForeColor = Theme.Register;
                }
                else
                {
                    string hex = r.Value.ToString(r.Size == 2 ? "X4" : "X2");
                    string from = r.HasAddress ? "$" + r.Address.ToString("X4") + " " + (machine.SymbolFor(r.Address) ?? "") : "register";
                    row = new ListViewItem(new[] { expressions[i], hex, r.Value.ToString(), r.Text, from });
                    if (before.TryGetValue(expressions[i], out int old) && old != r.Value) row.ForeColor = Theme.Changed;
                }
                row.Tag = r;
                if (i == selected) row.Selected = true;
                list.Items.Add(row);
            }
            list.EndUpdate();
        }
    }
}
