using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    /// <summary>
    /// Debugger window.
    /// Left: live disassembly from the PC, the whole-ROM listing, the label list, BASIC handler breakpoints
    /// and the execution profile. Right: registers, then memory, stack/call chain, hardware registers,
    /// breakpoints, write-watchpoints and instruction history.
    /// Keys: F5 run/pause, F10 step, F11 step over, Shift+F11 step out, F9 or Ctrl+B toggle breakpoint,
    /// Enter follow target, Backspace back, F2 rename, Shift+F12 references, Ctrl+F find, Ctrl+G go to.
    /// </summary>
    public sealed class DebuggerForm : Form
    {
        readonly EmulatorSession session;
        readonly Machine machine;

        readonly TabControl codeTabs, sideTabs;
        readonly TabPage livePage, romPage, labelsPage, basicPage, profilePage;
        readonly TabPage memoryPage, autosPage, watchListPage, stackPage, hardwarePage, breakPage, watchPage, historyPage;
        readonly AutosView autos;
        readonly WatchListView watchList;
        readonly ListingPanel liveListing, romListing;
        readonly ListBox labelList;
        readonly TextBox labelFilter, memAddr;
        readonly RegisterView registers;
        readonly MemoryView memory;
        readonly StackView stack;
        readonly HardwareView hardware;
        readonly ProfileView profile;
        readonly BasicHandlersView basic;
        readonly BreakpointsView breakpoints;
        readonly WatchView watches;
        readonly HistoryView history;
        readonly Label status;
        readonly Button runBtn;
        readonly Timer refresh;
        readonly Font mono = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);

        readonly List<ushort> labelAddrs = new List<ushort>();
        int labelsVersion = -1;
        string labelsFilterText = null;
        long lastCycles = -1;
        int lastPc = -1, lastBreakCount = -1, lastSymbolVersion = -1;
        bool refreshing;

        public DebuggerForm(EmulatorSession session, Form owner)
        {
            this.session = session;
            machine = session.Machine;
            Text = "MC-10 Debugger";
            KeyPreview = true;
            Size = new Size(1320, 800);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Max(0, owner.Left + owner.Width + 8), Math.Max(0, owner.Top));

            // ---- toolbar
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4), WrapContents = false };
            runBtn = Btn(bar, "Run (F5)", (s, e) => session.Toggle());
            Btn(bar, "Step (F10)", (s, e) => session.StepInstruction());
            Btn(bar, "Step over (F11)", (s, e) => session.StepOverInstruction());
            Btn(bar, "Step out (Shift+F11)", (s, e) => session.StepOutInstruction());
            Btn(bar, "Run to cursor", (s, e) => RunToCursor());
            Btn(bar, "Toggle BP (F9)", (s, e) => ActiveListing().ToggleBreakpointAtSelection());
            Btn(bar, "Reset", (s, e) => session.HardReset());
            Btn(bar, "Clear BPs", (s, e) => { machine.ClearBreakpoints(); RefreshAll(); });
            Btn(bar, "Save my labels", (s, e) => SaveUserLabels());

            status = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };

            // ---- left: code tabs
            liveListing = new ListingPanel(machine, true);
            romListing = new ListingPanel(machine, false);
            foreach (ListingPanel p in new[] { liveListing, romListing })
            {
                p.ListingChanged += RefreshAll;
                p.RunToRequested += RunTo;
                p.ExternalTargetRequested += (addr, isCode) =>
                {
                    if (isCode) ShowLive(addr); else ShowMemory(addr);
                };
            }

            labelFilter = new TextBox { Dock = DockStyle.Top, Font = mono };
            labelFilter.TextChanged += (s, e) => RefreshLabels();
            labelList = new ListBox { Dock = DockStyle.Fill, Font = mono, IntegralHeight = false };
            labelList.DoubleClick += (s, e) => GoToSelectedLabel();
            var labelHint = new Label { Dock = DockStyle.Top, Height = 20, Text = "Type to filter. Double-click a label to open it.", Padding = new Padding(4, 3, 0, 0) };

            basic = new BasicHandlersView(machine);
            basic.BreakpointsChanged += RefreshAll;
            basic.GoToRequested += ShowRom;
            profile = new ProfileView(machine);
            profile.GoToRequested += ShowRom;

            codeTabs = new TabControl { Dock = DockStyle.Fill };
            livePage = Page(codeTabs, "Live (from PC)", liveListing);
            romPage = Page(codeTabs, "ROM listing", romListing);
            labelsPage = new TabPage("Labels");
            labelsPage.Controls.Add(labelList);
            labelsPage.Controls.Add(labelFilter);
            labelsPage.Controls.Add(labelHint);
            labelList.BringToFront();
            codeTabs.TabPages.Add(labelsPage);
            basicPage = Page(codeTabs, "BASIC commands", basic);
            profilePage = Page(codeTabs, "Profile", profile);
            codeTabs.SelectedIndexChanged += (s, e) => RefreshAll();

            // ---- right: registers on top, tabs below
            registers = new RegisterView(machine) { Dock = DockStyle.Fill };

            memory = new MemoryView(machine) { Dock = DockStyle.Fill };
            memory.Edited += RefreshAll;
            var memBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2), WrapContents = false };
            memBar.Controls.Add(new Label { Text = "Address / label", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            memAddr = new TextBox { Width = 110, Font = mono, Text = "4000" };
            memAddr.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { GoMemory(memAddr.Text); e.Handled = e.SuppressKeyPress = true; }
            };
            memBar.Controls.Add(memAddr);
            Btn(memBar, "Show", (s, e) => GoMemory(memAddr.Text));
            Btn(memBar, "PC", (s, e) => ShowMemory(machine.Cpu.PC));
            Btn(memBar, "X", (s, e) => ShowMemory(machine.Cpu.X));
            Btn(memBar, "SP", (s, e) => ShowMemory(machine.Cpu.SP));
            Btn(memBar, "Screen", (s, e) => ShowMemory((ushort)machine.Config.VideoBase));
            Btn(memBar, "Zero page", (s, e) => ShowMemory(0x0080));
            var memPanel = new Panel { Dock = DockStyle.Fill };
            memPanel.Controls.Add(memory);
            memPanel.Controls.Add(memBar);
            memory.BringToFront();

            stack = new StackView(machine);
            stack.GoToRequested += ShowRom;
            hardware = new HardwareView(machine);
            breakpoints = new BreakpointsView(machine);
            breakpoints.Changed += RefreshAll;
            breakpoints.GoToRequested += ShowRom;
            watches = new WatchView(machine);
            watches.Changed += RefreshAll;
            history = new HistoryView(machine);
            history.GoToRequested += ShowRom;

            sideTabs = new TabControl { Dock = DockStyle.Fill };
            autos = new AutosView(machine);
            autos.GoToRequested += (addr, isCode) =>
            {
                if (isCode) ShowRom(addr); else ShowMemory(addr);
            };
            watchList = new WatchListView(machine);
            watchList.GoToRequested += ShowMemory;

            memoryPage = Page(sideTabs, "Memory", memPanel);
            autosPage = Page(sideTabs, "Autos", autos);
            watchListPage = Page(sideTabs, "Watch", watchList);
            stackPage = Page(sideTabs, "Stack / calls", stack);
            hardwarePage = Page(sideTabs, "Hardware", hardware);
            breakPage = Page(sideTabs, "Breakpoints", breakpoints);
            watchPage = Page(sideTabs, "Break on write", watches);
            historyPage = Page(sideTabs, "History", history);
            sideTabs.SelectedIndexChanged += (s, e) => RefreshAll();

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, registers.Height + 6));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.Controls.Add(registers, 0, 0);
            right.Controls.Add(sideTabs, 0, 1);

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(codeTabs, 0, 0);
            main.Controls.Add(right, 1, 0);

            Controls.Add(main);
            Controls.Add(bar);
            Controls.Add(status);
            main.BringToFront();            // the fill control must be docked last

            memory.GoTo((ushort)machine.Config.VideoBase);

            refresh = new Timer { Interval = 250 };
            refresh.Tick += (s, e) => OnTimer();
            refresh.Start();

            KeyDown += (s, e) => HandleKey(e);
            FormClosing += (s, e) => refresh.Stop();
            Shown += (s, e) => RefreshAll();        // listings only build when visible
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

        static TabPage Page(TabControl tabs, string title, Control content)
        {
            var page = new TabPage(title);
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            tabs.TabPages.Add(page);
            return page;
        }

        ListingPanel ActiveListing() => codeTabs.SelectedTab == livePage ? liveListing : romListing;

        void HandleKey(KeyEventArgs e)
        {
            bool handled = true;
            switch (e.KeyCode)
            {
                case Keys.F5: session.Toggle(); break;
                case Keys.F10: session.StepInstruction(); break;
                case Keys.F11:
                    if (e.Shift) session.StepOutInstruction(); else session.StepOverInstruction();
                    break;
                case Keys.F9: ActiveListing().ToggleBreakpointAtSelection(); break;
                default: handled = false; break;
            }
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        // ------------------------------------------------------------ actions
        void RunToCursor()
        {
            ushort? a = ActiveListing().SelectedAddress();
            if (a.HasValue) RunTo(a.Value);
        }

        void RunTo(ushort target)
        {
            session.Pause();
            StopReason r = machine.RunTo(target, 20_000_000);
            session.NotifyChanged();
            if (r != StopReason.Reached)
                status.Text = r == StopReason.Budget ? $"${target:X4} was not reached (gave up after 20 million cycles)" : $"Stopped early: {r}";
        }

        void ShowRom(ushort addr)
        {
            bool inRom = machine.Rom != null && addr >= machine.Rom.Base && addr < machine.Rom.Base + machine.Rom.Data.Length;
            if (!inRom) { ShowLive(addr); return; }
            codeTabs.SelectedTab = romPage;
            romListing.GoTo(addr);
        }

        void ShowLive(ushort addr)
        {
            codeTabs.SelectedTab = livePage;
            liveListing.GoTo(addr);
        }

        void ShowMemory(ushort addr)
        {
            sideTabs.SelectedTab = memoryPage;
            memAddr.Text = addr.ToString("X4");
            memory.GoTo(addr);
        }

        void GoMemory(string text)
        {
            if (machine.TryResolve(text, out ushort a)) { sideTabs.SelectedTab = memoryPage; memory.GoTo(a); }
            else status.Text = $"'{text}' is not an address or a known label";
        }

        void SaveUserLabels()
        {
            using (var dlg = new SaveFileDialog { Filter = "Symbol file|*.sym", FileName = "user.sym", Title = "Save the labels you renamed" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                machine.SaveUserSymbols(dlg.FileName);
                status.Text = $"{machine.UserSymbols.Count} of your labels saved. A file named user.sym in the symbols folder is loaded at start.";
            }
        }

        // ------------------------------------------------------------ refresh
        void OnTimer()
        {
            if (IsDisposed) return;
            if (session.Running) { RefreshRunning(); return; }
            // Paused: redraw only when something actually changed (no flicker, selection is kept).
            int bp = machine.Breakpoints.Count + machine.DisabledBreakpoints.Count;
            if (machine.Cpu.Cycles != lastCycles || machine.Cpu.PC != lastPc || bp != lastBreakCount ||
                machine.SymbolVersion != lastSymbolVersion)
                RefreshAll();
        }

        /// <summary>Full refresh of the visible panes. Called after every step, stop or edit.</summary>
        public void RefreshAll()
        {
            if (IsDisposed || refreshing) return;
            refreshing = true;
            try
            {
                lastCycles = machine.Cpu.Cycles;
                lastPc = machine.Cpu.PC;
                lastBreakCount = machine.Breakpoints.Count + machine.DisabledBreakpoints.Count;
                lastSymbolVersion = machine.SymbolVersion;

                registers.Reload();
                RefreshCodeTab(session.Running);
                RefreshSideTab();
                runBtn.Text = session.Running ? "Pause (F5)" : "Run (F5)";
                string where = machine.DescribeAddress(machine.Cpu.PC);
                string stop = machine.LastStop == StopReason.Watchpoint ? $"write to ${machine.LastWatchAddress:X4}"
                            : machine.LastStop == StopReason.Budget || machine.LastStop == StopReason.Reached ? "none"
                            : machine.LastStop.ToString();
                status.Text = $"{(session.Running ? "RUNNING" : "paused")}   PC={machine.Cpu.PC:X4}" + (where != null ? $" {where}" : "") +
                              $"   last stop: {stop}   cycles: {machine.Cpu.Cycles:N0}" +
                              (machine.Rom != null ? $"   ROM CRC32 {machine.Rom.Crc32():X8}" : "   no ROM");
            }
            finally { refreshing = false; }
        }

        void RefreshRunning()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                registers.Reload();
                RefreshCodeTab(true);
                RefreshSideTab();
                runBtn.Text = "Pause (F5)";
                status.Text = $"RUNNING   PC={machine.Cpu.PC:X4}   cycles: {machine.Cpu.Cycles:N0}";
            }
            finally { refreshing = false; }
        }

        void RefreshCodeTab(bool running)
        {
            TabPage page = codeTabs.SelectedTab;
            if (page == livePage) liveListing.Sync(running);
            else if (page == romPage) romListing.Sync(running);
            else if (page == labelsPage) RefreshLabels();
            else if (page == basicPage) basic.Reload();
            else if (page == profilePage) profile.Reload();
        }

        void RefreshSideTab()
        {
            TabPage page = sideTabs.SelectedTab;
            if (page == memoryPage) memory.Reload();
            else if (page == autosPage) autos.Reload();
            else if (page == watchListPage) watchList.Reload();
            else if (page == stackPage) stack.Reload();
            else if (page == hardwarePage) hardware.Reload();
            else if (page == breakPage) breakpoints.Reload();
            else if (page == watchPage) watches.Reload();
            else if (page == historyPage) history.Reload();
        }

        void RefreshLabels()
        {
            string filter = labelFilter.Text.Trim();
            if (labelsVersion == machine.SymbolVersion && labelsFilterText == filter) return;
            labelsVersion = machine.SymbolVersion;
            labelsFilterText = filter;
            var all = new SortedDictionary<ushort, string>();
            foreach (var kv in machine.AutoSymbols) all[kv.Key] = kv.Value;
            foreach (var kv in machine.Symbols) all[kv.Key] = kv.Value;
            labelList.BeginUpdate();
            labelList.Items.Clear();
            labelAddrs.Clear();
            foreach (var kv in all)
            {
                if (filter.Length > 0 && kv.Value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    kv.Key.ToString("X4").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                labelList.Items.Add($"{kv.Key:X4}  {kv.Value}");
                labelAddrs.Add(kv.Key);
            }
            labelList.EndUpdate();
        }

        void GoToSelectedLabel()
        {
            int i = labelList.SelectedIndex;
            if (i < 0 || i >= labelAddrs.Count) return;
            ushort a = labelAddrs[i];
            bool inRom = machine.Rom != null && a >= machine.Rom.Base && a < machine.Rom.Base + machine.Rom.Data.Length;
            if (inRom) ShowRom(a); else ShowMemory(a);
        }
    }
}
