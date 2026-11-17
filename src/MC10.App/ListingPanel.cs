using System;
using System.Collections.Generic;
using System.Drawing;
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
    /// Disassembly view with syntax colours. Two modes:
    /// the whole-ROM listing (labels, banners with callers, data) and a live window that disassembles from the
    /// program counter, so code in RAM (BASIC EXEC programs, RAM hooks) can be followed too.
    /// Double-click or Ctrl+B toggles a breakpoint, Enter follows a jump/call/table target, Backspace goes back,
    /// F2 renames a label, Shift+F12 lists references, Ctrl+F / F3 search. Green shading marks executed code.
    /// </summary>
    public sealed class ListingPanel : Panel
    {
        const int DisCol = 18;
        const int LiveInstructions = 100;
        const int GutterWidth = 18;

        readonly Machine machine;
        readonly bool live;
        readonly FlickerFreeList list;
        readonly TextBox goBox;
        readonly CheckBox follow, showCoverage;
        readonly Label info;
        readonly Font mono = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
        readonly Font monoBold = new Font("Consolas", 10f, FontStyle.Bold, GraphicsUnit.Point);
        readonly float charWidth;
        readonly Stack<ushort> backStack = new Stack<ushort>();

        List<ListingLine> lines = new List<ListingLine>();
        Analysis built;
        int builtSymbols = -1;
        int lastPc = -1;
        ushort manualStart;
        string lastFind = "";
        DateTime lastLiveBuild = DateTime.MinValue;

        /// <summary>Raised when the user chooses "Run to this line".</summary>
        public event Action<ushort> RunToRequested;
        /// <summary>Raised when breakpoints or names were changed here, so other views can refresh.</summary>
        public event Action ListingChanged;
        /// <summary>Raised when Enter is pressed on an operand that lies outside the ROM (code = jump/call/branch target).</summary>
        public event Action<ushort, bool> ExternalTargetRequested;

        public ListingPanel(Machine machine, bool live)
        {
            this.machine = machine;
            this.live = live;
            Dock = DockStyle.Fill;
            charWidth = CodePainter.CharWidth(mono);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Go to", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            goBox = new TextBox { Width = 120, Font = mono };
            goBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { GoToText(goBox.Text); e.Handled = e.SuppressKeyPress = true; }
            };
            bar.Controls.Add(goBox);
            AddButton(bar, "Go", (s, e) => GoToText(goBox.Text));
            AddButton(bar, "PC", (s, e) => { follow.Checked = true; GoToPc(); });
            AddButton(bar, "< Back", (s, e) => Back());
            follow = new CheckBox { Text = "Follow PC", Checked = true, AutoSize = true, Padding = new Padding(6, 5, 0, 0) };
            bar.Controls.Add(follow);
            showCoverage = new CheckBox { Text = "Executed in green", Checked = true, AutoSize = true, Padding = new Padding(6, 5, 0, 0) };
            showCoverage.CheckedChanged += (s, e) => list.Invalidate();
            bar.Controls.Add(showCoverage);
            info = new Label { AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
            bar.Controls.Add(info);

            list = new FlickerFreeList
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                HorizontalExtent = 1200,
                Font = mono,
            };
            list.ItemHeight = mono.Height + 2;
            list.DrawItem += DrawLine;
            list.MouseDoubleClick += (s, e) => ToggleBreakpointAtSelection();
            list.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0) list.SelectedIndex = i;
            };
            list.KeyDown += (s, e) => HandleKey(e);
            list.ContextMenuStrip = BuildMenu();

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

        static void AddMenuItem(ContextMenuStrip menu, string text, string keys, EventHandler click)
        {
            var item = new ToolStripMenuItem(text, null, click);
            if (keys != null) item.ShortcutKeyDisplayString = keys;
            menu.Items.Add(item);
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            AddMenuItem(menu, "Toggle breakpoint", "Ctrl+B", (s, e) => ToggleBreakpointAtSelection());
            AddMenuItem(menu, "Breakpoint condition...", null, (s, e) => EditCondition());
            AddMenuItem(menu, "Run to this line", null, (s, e) =>
            {
                ushort? a = SelectedAddress();
                if (a.HasValue) RunToRequested?.Invoke(a.Value);
            });
            AddMenuItem(menu, "Set next instruction (PC) here", null, (s, e) =>
            {
                ListingLine l = SelectedLine;
                if (l == null || l.Kind != LineKind.Code) return;
                machine.Cpu.PC = l.Address;
                ListingChanged?.Invoke();
            });
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "Follow jump / call / table target", "Enter", (s, e) => FollowTarget());
            AddMenuItem(menu, "Back", "Backspace", (s, e) => Back());
            AddMenuItem(menu, "Rename label...", "F2", (s, e) => RenameAtSelection());
            AddMenuItem(menu, "Show references...", "Shift+F12", (s, e) => ShowReferences());
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "Find...", "Ctrl+F", (s, e) => Find(false));
            AddMenuItem(menu, "Find next", "F3", (s, e) => Find(true));
            AddMenuItem(menu, "Copy line", "Ctrl+C", (s, e) => CopySelectedLine());
            return menu;
        }

        // ------------------------------------------------------------ data
        ListingLine SelectedLine
        {
            get
            {
                int i = list.SelectedIndex;
                return i >= 0 && i < lines.Count ? lines[i] : null;
            }
        }

        /// <summary>Address of the selected line, or null when nothing (or a header line) is selected.</summary>
        public ushort? SelectedAddress()
        {
            ListingLine l = SelectedLine;
            if (l == null || l.Kind == LineKind.Header || l.Kind == LineKind.Blank) return null;
            return l.Address;
        }

        void SetItems()
        {
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(lines.Cast<object>().ToArray());
            list.EndUpdate();
        }

        void RebuildRom()
        {
            built = machine.Analysis;
            builtSymbols = machine.SymbolVersion;
            lines = machine.BuildListing();
            SetItems();
            var an = machine.Analysis;
            info.Text = an == null ? "no ROM loaded"
                : $"{an.InstructionCount} instructions, {an.Subroutines.Count} subroutines, {an.Labels.Count} labels";
            lastPc = -1;
        }

        /// <summary>Rebuild after names changed, keeping the selection on the same address.</summary>
        void RebuildKeepingSelection()
        {
            ushort? keep = SelectedAddress();
            int top = list.TopIndex;
            if (live) BuildLive(follow.Checked ? machine.Cpu.PC : manualStart);
            else RebuildRom();
            if (keep.HasValue)
            {
                int idx = IndexOfAddress(keep.Value);
                if (idx >= 0)
                {
                    list.SelectedIndex = idx;
                    list.TopIndex = Math.Max(0, Math.Min(top, lines.Count - 1));
                }
            }
        }

        // ------------------------------------------------------------ syncing with the machine
        /// <summary>Call whenever machine state changes. Cheap unless the ROM, the names or the PC changed.</summary>
        public void Sync(bool running)
        {
            if (!Visible) return;
            if (live) { SyncLive(running); return; }

            if (!ReferenceEquals(built, machine.Analysis) || builtSymbols != machine.SymbolVersion) RebuildRom();
            int pc = machine.Cpu.PC;
            if (follow.Checked && !running && pc != lastPc) NavigateTo((ushort)pc, false);
            lastPc = pc;
            list.Invalidate();
        }

        void SyncLive(bool running)
        {
            ushort pc = machine.Cpu.PC;
            if (!follow.Checked)
            {
                if (lines.Count == 0) BuildLive(manualStart);
                list.Invalidate();
                return;
            }
            if (running)
            {
                // Re-centre at most once a second, and only when the PC has left the window.
                int idx = IndexOfCode(pc);
                if (idx < 0 && (DateTime.UtcNow - lastLiveBuild).TotalSeconds >= 1) BuildLive(pc);
                else if (idx >= 0 && list.SelectedIndex != idx) list.SelectedIndex = idx;
                list.Invalidate();
                return;
            }
            BuildLive(pc);                       // paused: always rebuild so edited memory shows
        }

        /// <summary>Start address of the live window: a few instructions before the PC when the ROM analysis allows.</summary>
        ushort ContextStart(ushort pc)
        {
            Analysis an = machine.Analysis;
            if (an == null || !an.IsCodeStart(pc)) return pc;
            ushort current = pc;
            int found = 0;
            for (int back = 1; back <= 24 && found < 6; back++)
            {
                int candidate = pc - back;
                if (candidate < an.Base) break;
                if (!an.IsCodeStart((ushort)candidate)) continue;
                Disassembler.Disassemble(machine.Bus.Peek, (ushort)candidate, out int length);
                if (candidate + length == current)
                {
                    current = (ushort)candidate;
                    found++;
                }
            }
            return current;
        }

        void BuildLive(ushort at)
        {
            lastLiveBuild = DateTime.UtcNow;
            bool followPc = follow.Checked;
            ushort start = followPc ? ContextStart(at) : at;
            var result = new List<ListingLine>();
            ushort a = start;
            for (int n = 0; n < LiveInstructions; n++)
            {
                string name = machine.CodeSymbolFor(a);
                if (name != null) result.Add(new ListingLine(a, LineKind.Label, name + ":"));
                string dis = Disassembler.Disassemble(machine.Bus.Peek, a, out int len, machine.SymbolFor, machine.CodeSymbolFor);
                string hex = Disassembler.HexBytes(machine.Bus.Peek, a, len);
                result.Add(new ListingLine(a, LineKind.Code, $"  {a:X4}  {hex,-9} {dis}"));
                a = (ushort)(a + len);
            }
            lines = result;
            SetItems();
            int idx = IndexOfCode(at);
            if (idx >= 0)
            {
                list.SelectedIndex = idx;
                int rows = Math.Max(4, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
                list.TopIndex = Math.Max(0, idx - Math.Min(rows / 3, 10));
            }
            string where = machine.DescribeAddress(machine.Cpu.PC);
            info.Text = followPc ? $"live from PC {machine.Cpu.PC:X4}" + (where != null ? $"  ({where})" : "")
                                 : $"live from ${start:X4} (not following PC)";
            lastPc = machine.Cpu.PC;
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

        int IndexOfCode(ushort addr)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Kind == LineKind.Code && lines[i].Address == addr) return i;
            return -1;
        }

        int IndexOfAddress(ushort addr)
        {
            if (live) return IndexOfCode(addr);
            int idx = FindIndex(addr);
            if (idx < 0) return -1;
            // Prefer the code or data line itself over the banner/label lines that share its address.
            while (idx + 1 < lines.Count && lines[idx].Address == addr && lines[idx].Kind != LineKind.Code
                   && lines[idx].Kind != LineKind.Data) idx++;
            return idx;
        }

        /// <summary>Show the line at an address. Previous position goes on the back stack (Backspace).</summary>
        public void GoTo(ushort addr) => NavigateTo(addr, true);

        void GoToPc() => NavigateTo(machine.Cpu.PC, false);

        void NavigateTo(ushort addr, bool pushHistory)
        {
            if (pushHistory)
            {
                ushort? origin = SelectedAddress();
                if (origin.HasValue && origin.Value != addr) backStack.Push(origin.Value);
            }
            if (live)
            {
                if (pushHistory) follow.Checked = false;      // the user picked an address: stop following the PC
                manualStart = addr;
                BuildLive(follow.Checked ? machine.Cpu.PC : addr);
                return;
            }
            if (!ReferenceEquals(built, machine.Analysis) || builtSymbols != machine.SymbolVersion) RebuildRom();
            int idx = IndexOfAddress(addr);
            if (idx < 0) return;
            SelectIndex(idx);
        }

        void SelectIndex(int idx)
        {
            list.SelectedIndex = idx;
            int rows = Math.Max(4, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
            list.TopIndex = Math.Max(0, idx - rows / 3);
        }

        void Back()
        {
            if (backStack.Count == 0) { info.Text = "nothing to go back to"; return; }
            NavigateTo(backStack.Pop(), false);
        }

        void GoToText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (machine.TryResolve(text, out ushort a)) NavigateTo(a, true);
            else info.Text = $"'{text.Trim()}' not found";
        }

        // ------------------------------------------------------------ actions
        /// <summary>Toggle a breakpoint on the selected line (used by the toolbar, Ctrl+B and double-click).</summary>
        public void ToggleBreakpointAtSelection()
        {
            ListingLine l = SelectedLine;
            if (l == null || l.Kind == LineKind.Header || l.Kind == LineKind.Blank || l.Kind == LineKind.Data) return;
            machine.ToggleBreakpoint(l.Address);
            list.Invalidate();
            ListingChanged?.Invoke();
        }

        void EditCondition()
        {
            ushort? a = SelectedAddress();
            if (!a.HasValue) return;
            string current = machine.BreakConditions.TryGetValue(a.Value, out string c) ? c : "";
            string text = PromptDialog.Ask(FindForm(), "Breakpoint condition",
                "Stop only when this is true (examples: A==0D   X>=4200   [00F3]!=0   CC&04). Leave empty to always stop.", current);
            if (text == null) return;
            text = text.Trim();
            if (text.Length > 0 && !BreakCondition.IsValid(text, machine))
            {
                MessageBox.Show(FindForm(), "That is not a valid condition. Use one comparison such as A==0D, X>=4200 or [00F3]!=0.",
                    "Breakpoint condition");
                return;
            }
            if (!machine.HasBreakpoint(a.Value)) machine.Breakpoints.Add(a.Value);
            if (text.Length == 0) machine.BreakConditions.Remove(a.Value);
            else machine.BreakConditions[a.Value] = text;
            list.Invalidate();
            ListingChanged?.Invoke();
        }

        void RenameAtSelection()
        {
            ushort? a = SelectedAddress();
            if (!a.HasValue) return;
            string current = machine.CodeSymbolFor(a.Value) ?? "";
            string name = PromptDialog.Ask(FindForm(), "Rename label",
                $"Name for ${a.Value:X4} (leave empty to remove your name):", current);
            if (name == null) return;
            machine.SetSymbol(a.Value, name);
            RebuildKeepingSelection();
            ListingChanged?.Invoke();
        }

        void ShowReferences()
        {
            ushort? a = SelectedAddress();
            if (!a.HasValue) return;
            var items = new List<string>();
            var targets = new List<ushort>();
            Analysis an = machine.Analysis;
            if (an != null)
            {
                if (an.Callers.TryGetValue(a.Value, out var callers))
                    foreach (ushort c in callers.OrderBy(x => x))
                    {
                        items.Add($"{c:X4}  call  {machine.DescribeAddress(c)}");
                        targets.Add(c);
                    }
                if (an.Refs.TryGetValue(a.Value, out var refs))
                    foreach (ushort r in refs.OrderBy(x => x))
                    {
                        items.Add($"{r:X4}  ref   {machine.DescribeAddress(r)}");
                        targets.Add(r);
                    }
            }
            if (items.Count == 0)
            {
                info.Text = "no references recorded for this address";
                return;
            }
            string name = machine.CodeSymbolFor(a.Value) ?? "$" + a.Value.ToString("X4");
            var dlg = new ListDialog($"References to {name} (${a.Value:X4}) - double-click to go there", items,
                                     i => { if (i >= 0 && i < targets.Count) NavigateTo(targets[i], true); });
            dlg.Show(FindForm());
        }

        void Find(bool next)
        {
            if (!next || lastFind.Length == 0)
            {
                string t = PromptDialog.Ask(FindForm(), "Find", "Text to find in the listing:", lastFind);
                if (string.IsNullOrEmpty(t)) return;
                lastFind = t;
            }
            int start = list.SelectedIndex + 1;
            for (int k = 0; k < lines.Count; k++)
            {
                int i = (start + k) % lines.Count;
                if (lines[i].Text.IndexOf(lastFind, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SelectIndex(i);
                    return;
                }
            }
            info.Text = $"'{lastFind}' not found";
        }

        void CopySelectedLine()
        {
            ListingLine l = SelectedLine;
            if (l != null && l.Text.Length > 0) Clipboard.SetText(l.Text);
        }

        /// <summary>The address named by the operand of the selected line (jump, call, branch, table entry, or data).</summary>
        bool TryOperandTarget(ListingLine l, out ushort target, out bool isCode)
        {
            target = 0;
            isCode = false;
            string t = l.Text;
            string operand;
            if (l.Kind == LineKind.Code && t.Length > DisCol)
            {
                string dis = t.Substring(DisCol);
                int sp = dis.IndexOf(' ');
                if (sp < 0) return false;
                string mnemonic = dis.Substring(0, sp);
                isCode = mnemonic == "JSR" || mnemonic == "JMP" || mnemonic == "BSR" ||
                         (mnemonic.Length == 3 && mnemonic[0] == 'B' && mnemonic != "BIT");
                operand = dis.Substring(sp + 1);
            }
            else if (l.Kind == LineKind.Data)
            {
                int f = t.IndexOf("FDB", StringComparison.Ordinal);
                if (f < 0) return false;
                operand = t.Substring(f + 3);
                isCode = true;
            }
            else return false;

            int semi = operand.IndexOf(';');
            if (semi >= 0) operand = operand.Substring(0, semi);
            operand = operand.Trim();
            if (operand.EndsWith(",X", StringComparison.Ordinal)) return false;     // indexed: no fixed target
            if (operand.StartsWith("#", StringComparison.Ordinal)) operand = operand.Substring(1);
            return machine.TryResolve(operand, out target);
        }

        void FollowTarget()
        {
            ListingLine l = SelectedLine;
            if (l == null) return;
            if (!TryOperandTarget(l, out ushort target, out bool isCode))
            {
                info.Text = "no jump, call or table target on this line";
                return;
            }
            bool inRom = machine.Rom != null && target >= machine.Rom.Base && target < machine.Rom.Base + machine.Rom.Data.Length;
            if (inRom || live) NavigateTo(target, true);
            else ExternalTargetRequested?.Invoke(target, isCode);
        }

        void HandleKey(KeyEventArgs e)
        {
            bool handled = true;
            if (e.KeyCode == Keys.Enter) FollowTarget();
            else if (e.KeyCode == Keys.Back) Back();
            else if (e.KeyCode == Keys.F2) RenameAtSelection();
            else if (e.KeyCode == Keys.F12 && e.Shift) ShowReferences();
            else if (e.KeyCode == Keys.B && e.Control) ToggleBreakpointAtSelection();
            else if (e.KeyCode == Keys.F && e.Control) Find(false);
            else if (e.KeyCode == Keys.F3) Find(true);
            else if (e.KeyCode == Keys.G && e.Control) { goBox.Focus(); goBox.SelectAll(); }
            else if (e.KeyCode == Keys.C && e.Control) CopySelectedLine();
            else handled = false;
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        // ------------------------------------------------------------ drawing
        void DrawLine(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lines.Count) return;
            ListingLine l = lines[e.Index];
            bool isCode = l.Kind == LineKind.Code;
            bool atPc = isCode && l.Address == machine.Cpu.PC;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool covered = isCode && showCoverage.Checked && machine.Executed[l.Address];

            Color back = Color.White;
            if (l.Kind == LineKind.Comment || l.Kind == LineKind.Label) back = Theme.BannerBack;
            if (covered) back = Theme.CoveredBack;
            if (selected) back = Theme.SelectedBack;
            if (atPc) back = Theme.PcBack;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);

            if (isCode)
            {
                if (machine.Breakpoints.Contains(l.Address))
                {
                    bool conditional = machine.BreakConditions.ContainsKey(l.Address);
                    using (var r = new SolidBrush(conditional ? Color.DarkOrange : Color.Crimson))
                        e.Graphics.FillEllipse(r, e.Bounds.X + 3, e.Bounds.Y + 3, 10, 10);
                }
                else if (machine.DisabledBreakpoints.Contains(l.Address))
                {
                    using (var pen = new Pen(Color.Crimson))
                        e.Graphics.DrawEllipse(pen, e.Bounds.X + 3, e.Bounds.Y + 3, 9, 9);
                }
                if (atPc)
                {
                    var tip = new[]
                    {
                        new Point(e.Bounds.X + 12, e.Bounds.Y + 8),
                        new Point(e.Bounds.X + 5, e.Bounds.Y + 3),
                        new Point(e.Bounds.X + 5, e.Bounds.Y + 13),
                    };
                    if (!machine.Breakpoints.Contains(l.Address))
                        using (var arrow = new SolidBrush(Color.FromArgb(200, 140, 0))) e.Graphics.FillPolygon(arrow, tip);
                }
            }

            CodePainter.DrawLine(e.Graphics, l.Text, l.Kind, mono, monoBold, charWidth, e.Bounds.X + GutterWidth, e.Bounds.Y + 1);
        }
    }
}
