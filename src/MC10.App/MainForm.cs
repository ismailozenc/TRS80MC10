using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    public sealed class MainForm : Form
    {
        const int CellPx = 16;                       // 32 x 16 cells -> 512 x 256 pixels
        const int Scale = 2;                         // on-screen zoom

        readonly EmulatorSession session;
        readonly ScreenPanel screen;
        readonly StatusStrip status;
        readonly ToolStripStatusLabel stateLabel, cycleLabel, romLabel;
        readonly Timer frameTimer;
        readonly ToolStripMenuItem traceMenu;
        DebuggerForm debugger;
        StreamWriter traceWriter;

        public MainForm(MachineConfig config, string romPath, string symbolsPath)
        {
            Text = "TRS-80 MC-10 Emulator";
            KeyPreview = true;
            BackColor = Color.Black;
            // Screen + menu bar (~24 px) + status bar (~22 px)
            ClientSize = new Size(CellPx * Video.Columns * Scale, CellPx * Video.Rows * Scale + 50);

            var machine = new Machine(config);
            session = new EmulatorSession(machine);
            session.StateChanged += () => { UpdateStatus(); debugger?.RefreshAll(); };
            session.Stopped += r => ShowDebugger();

            // ---- menu
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&Open ROM...", Keys.None, (s, e) => OpenRom()));
            file.DropDownItems.Add(Item("Load &symbols...", Keys.None, (s, e) => OpenSymbols()));
            file.DropDownItems.Add(Item("Save &screenshot...", Keys.None, (s, e) => SaveScreenshot()));
            file.DropDownItems.Add(Item("Export &disassembly...", Keys.None, (s, e) => ExportDisassembly()));
            file.DropDownItems.Add(Item("&Export symbols...", Keys.None, (s, e) => ExportSymbols()));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, (s, e) => Close()));

            var mach = new ToolStripMenuItem("&Machine");
            mach.DropDownItems.Add(Item("&Run / Pause  (F5)", Keys.F5, (s, e) => session.Toggle()));
            mach.DropDownItems.Add(Item("&Pause  (F6)", Keys.F6, (s, e) => session.Pause()));
            mach.DropDownItems.Add(Item("&Step instruction  (F10)", Keys.F10, (s, e) => session.StepInstruction()));
            mach.DropDownItems.Add(Item("Step &over  (F11)", Keys.F11, (s, e) => session.StepOverInstruction()));
            mach.DropDownItems.Add(Item("Step o&ut  (Shift+F11)", Keys.Shift | Keys.F11, (s, e) => session.StepOutInstruction()));
            mach.DropDownItems.Add(new ToolStripSeparator());
            mach.DropDownItems.Add(Item("&Reset  (Ctrl+R)", Keys.Control | Keys.R, (s, e) => session.HardReset()));
            mach.DropDownItems.Add(Item("Open &debugger  (F12)", Keys.F12, (s, e) => ShowDebugger()));
            mach.DropDownItems.Add(new ToolStripSeparator());
            traceMenu = Item("&Trace to file...", Keys.None, (s, e) => ToggleTrace());
            mach.DropDownItems.Add(traceMenu);
            var speed = new ToolStripMenuItem("&Speed");
            foreach (double f in new[] { 0.5, 1.0, 2.0, 4.0, 8.0 })
            {
                double factor = f;
                speed.DropDownItems.Add(Item($"{f}x", Keys.None, (s, e) => session.Speed = factor));
            }
            mach.DropDownItems.Add(speed);

            menu.Items.Add(file);
            menu.Items.Add(mach);
            MainMenuStrip = menu;
            Controls.Add(menu);

            // ---- screen
            screen = new ScreenPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
            };
            screen.Paint += (s, e) => PaintScreen(e.Graphics, machine, Scale);
            Controls.Add(screen);
            screen.BringToFront();

            // ---- status bar
            status = new StatusStrip();
            stateLabel = new ToolStripStatusLabel { Text = "stopped" };
            cycleLabel = new ToolStripStatusLabel { Text = "" };
            romLabel = new ToolStripStatusLabel { Text = "no ROM loaded", Spring = true, TextAlign = ContentAlignment.MiddleRight };
            status.Items.AddRange(new ToolStripItem[] { stateLabel, cycleLabel, romLabel });
            Controls.Add(status);

            frameTimer = new Timer { Interval = 16 };
            frameTimer.Tick += (s, e) =>
            {
                session.Frame();
                if (ScreenChanged(machine)) screen.Invalidate();
                if (session.Running) UpdateStatus();
            };
            frameTimer.Start();

            if (!string.IsNullOrEmpty(romPath)) LoadRomFile(romPath);
            if (!string.IsNullOrEmpty(symbolsPath)) machine.LoadSymbols(symbolsPath);

            KeyPress += (s, e) => HandleKeyPress(e);
            KeyDown += (s, e) => HandleKeyDown(e);
            FormClosing += (s, e) => { frameTimer.Stop(); StopTrace(); debugger?.Close(); };

            UpdateStatus();
        }

        // Repaint only when video RAM or the colour register changed (avoids flicker and wasted work).
        readonly byte[] lastScreen = new byte[Video.Columns * Video.Rows + 1];
        bool ScreenChanged(Machine m)
        {
            bool changed = false;
            int n = Video.Columns * Video.Rows;
            for (int i = 0; i < n; i++)
            {
                byte v = m.Bus.Mem[m.Config.VideoBase + i];
                if (lastScreen[i] != v) { lastScreen[i] = v; changed = true; }
            }
            if (lastScreen[n] != m.VideoControl.Value) { lastScreen[n] = m.VideoControl.Value; changed = true; }
            return changed;
        }

        // ------------------------------------------------------------ painting
        static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick);
            if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
            return item;
        }

        /// <summary>Draws the 32 x 16 screen at the given zoom. Shared by the window and screenshots.</summary>
        public static void PaintScreen(Graphics g, Machine m, int scale)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            float cw = CellPx * scale, ch = CellPx * scale;
            using (var glyphFont = new Font("Consolas", CellPx * scale * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                for (int r = 0; r < Video.Rows; r++)
                for (int c = 0; c < Video.Columns; c++)
                {
                    Cell cell = m.Video.GetCell(r, c);
                    float x = c * cw, y = r * ch;
                    Color back = Palette.Get(cell.Background);
                    Color front = Palette.Get(cell.Foreground);

                    if (cell.IsSemigraphic)
                    {
                        using (var bg = new SolidBrush(back)) g.FillRectangle(bg, x, y, cw, ch);
                        using (var fg = new SolidBrush(front))
                        {
                            float hw = cw / 2, hh = ch / 2;
                            for (int bit = 0; bit < 4; bit++)
                            {
                                if (((cell.Code >> bit) & 1) == 0) continue;
                                g.FillRectangle(fg, x + (bit % 2) * hw, y + (bit / 2) * hh, hw, hh);
                            }
                        }
                        continue;
                    }

                    Video.Glyph(cell.Code, out char glyph, out bool inverse);
                    if (inverse) { var t = back; back = front; front = t; }   // reverse video
                    using (var bg = new SolidBrush(back)) g.FillRectangle(bg, x, y, cw, ch);
                    if (glyph != ' ')
                    {
                        using (var fg = new SolidBrush(front))
                            g.DrawString(glyph.ToString(), glyphFont, fg, x + 1, y);
                    }
                }
            }
        }

        public static Bitmap RenderBitmap(Machine m)
        {
            var bmp = new Bitmap(Video.Columns * CellPx, Video.Rows * CellPx, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                PaintScreen(g, m, 1);
            return bmp;
        }

        // ------------------------------------------------------------ input
        void HandleKeyPress(KeyPressEventArgs e)
        {
            if (session.Machine.Keyboard.TapChar(e.KeyChar)) e.Handled = true;
        }

        void HandleKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:                            // BREAK
                    session.Machine.Keyboard.TapBreak();
                    e.Handled = e.SuppressKeyPress = true;
                    break;
                case Keys.Left:                              // arrow acts as backspace (CONTROL+A)
                case Keys.Back:
                    session.Machine.Keyboard.TapChar('\b');
                    e.Handled = e.SuppressKeyPress = true;
                    break;
                case Keys.Space:                             // handled here so it never depends on KeyPress
                    if (!e.Control && !e.Alt)
                    {
                        session.Machine.Keyboard.TapChar(' ');
                        e.Handled = e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.Return:
                    session.Machine.Keyboard.TapChar('\r');
                    e.Handled = e.SuppressKeyPress = true;
                    break;
            }
        }

        // ------------------------------------------------------------ actions
        void OpenRom()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "ROM images|*.rom;*.bin;*.hex;*.ihx;*.s19;*.srec|All files|*.*",
                Title = "Open MC-10 ROM image",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) LoadRomFile(dlg.FileName);
        }

        void LoadRomFile(string path)
        {
            try
            {
                session.Machine.LoadRomFile(path);
                session.HardReset();
                romLabel.Text = $"ROM: {Path.GetFileName(path)}  CRC32 {session.Machine.Rom.Crc32():X8}";
                session.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not load ROM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OpenSymbols()
        {
            using var dlg = new OpenFileDialog { Filter = "Symbol files|*.sym;*.txt;*.map|All files|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                session.Machine.LoadSymbols(dlg.FileName);
                debugger?.RefreshAll();
            }
        }

        void ExportDisassembly()
        {
            if (session.Machine.Analysis == null)
            {
                MessageBox.Show(this, "Load a ROM first.", "Export disassembly");
                return;
            }
            using var dlg = new SaveFileDialog { Filter = "Assembly listing|*.asm;*.txt", FileName = "mc10-rom.asm" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, Listing.ToText(session.Machine.BuildListing()));
        }

        void ExportSymbols()
        {
            using var dlg = new SaveFileDialog { Filter = "Symbol file|*.sym;*.txt", FileName = "mc10.sym" };
            if (dlg.ShowDialog(this) == DialogResult.OK) session.Machine.SaveSymbols(dlg.FileName);
        }

        void SaveScreenshot()
        {
            using var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "mc10.png" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            using var bmp = RenderBitmap(session.Machine);
            bmp.Save(dlg.FileName, ImageFormat.Png);
        }

        void ToggleTrace()
        {
            if (traceWriter != null)
            {
                StopTrace();
                return;
            }
            using var dlg = new SaveFileDialog { Filter = "Trace log|*.log;*.txt", FileName = "mc10-trace.log" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            traceWriter = new StreamWriter(dlg.FileName, false) { AutoFlush = false };
            session.Machine.Trace = traceWriter;
            traceMenu.Checked = true;
            UpdateStatus();
        }

        void StopTrace()
        {
            if (traceWriter == null) return;
            session.Machine.Trace = null;
            traceWriter.Flush();
            traceWriter.Dispose();
            traceWriter = null;
            traceMenu.Checked = false;
            UpdateStatus();
        }

        void ShowDebugger()
        {
            if (debugger == null || debugger.IsDisposed)
                debugger = new DebuggerForm(session, this);
            debugger.Show();
            debugger.BringToFront();
        }

        void UpdateStatus()
        {
            var cpu = session.Machine.Cpu;
            string st = session.Running ? "running" :
                              session.LastStop == StopReason.None ? "paused" : $"stopped: {session.LastStop}";
            if (stateLabel.Text != st) stateLabel.Text = st;
            string cy = $"PC={cpu.PC:X4}  cycles={cpu.Cycles:N0}" + (traceWriter != null ? "  TRACE" : "");
            if (cycleLabel.Text != cy) cycleLabel.Text = cy;
        }

    }

    /// <summary>Panel with double buffering so repaints do not flicker.</summary>
    internal sealed class ScreenPanel : Panel
    {
        public ScreenPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }
    }

    /// <summary>Nine MC-10 colours, indexed as in the manual's CLS colour codes.</summary>
    internal static class Palette
    {
        static readonly Color[] colors =
        {
            Color.FromArgb(0, 0, 0),         // 0 black
            Color.FromArgb(51, 255, 51),     // 1 green
            Color.FromArgb(255, 255, 51),    // 2 yellow
            Color.FromArgb(51, 51, 255),     // 3 blue
            Color.FromArgb(255, 51, 51),     // 4 red
            Color.FromArgb(255, 230, 190),   // 5 buff
            Color.FromArgb(51, 255, 255),    // 6 cyan
            Color.FromArgb(255, 51, 255),    // 7 magenta
            Color.FromArgb(255, 153, 51),    // 8 orange
        };

        public static Color Get(int index) => index >= 0 && index < colors.Length ? colors[index] : colors[0];
    }
}
