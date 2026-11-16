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
        readonly Panel screen;
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
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, (s, e) => Close()));

            var mach = new ToolStripMenuItem("&Machine");
            mach.DropDownItems.Add(Item("&Run / Pause  (F5)", Keys.F5, (s, e) => session.Toggle()));
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
            screen = new Panel
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
                screen.Invalidate();
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
            char ch = e.KeyChar;
            byte code;
            if (ch == '\r') code = 13;                       // ENTER
            else if (ch == '\b') code = 8;                   // backspace
            else if (ch == (char)27) code = 3;               // ESC = BREAK
            else if (ch >= 32 && ch < 127) code = (byte)char.ToUpperInvariant(ch);
            else return;
            session.Machine.Press(code);
            e.Handled = true;
        }

        void HandleKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left)
            {
                session.Machine.Press(8);                    // left arrow acts as backspace
                e.Handled = true;
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
            stateLabel.Text = session.Running ? "running" :
                              session.LastStop == StopReason.None ? "paused" : $"stopped: {session.LastStop}";
            cycleLabel.Text = $"PC={cpu.PC:X4}  cycles={cpu.Cycles:N0}" + (traceWriter != null ? "  TRACE" : "");
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
