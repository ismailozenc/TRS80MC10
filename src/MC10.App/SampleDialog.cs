using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    /// <summary>
    /// Browser for the sample BASIC programs. Shows what each one demonstrates and what to watch in the
    /// debugger, types it into the MC-10 (optionally NEW first and RUN after) and can set the suggested breakpoints.
    /// </summary>
    internal sealed class SampleDialog : Form
    {
        readonly EmulatorSession session;
        readonly ListBox list;
        readonly TextBox details;
        readonly CheckBox newFirst;
        readonly Label status;
        readonly List<SampleProgram> programs = new List<SampleProgram>();

        public SampleDialog(EmulatorSession session, string folder)
        {
            this.session = session;
            Text = "Sample BASIC programs";
            ClientSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            list = new ListBox { Dock = DockStyle.Left, Width = 250, IntegralHeight = false };
            details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point),
                BackColor = Color.White,
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
            Btn(bar, "Type it in", (s, e) => TypeSelected(false));
            Btn(bar, "Type it in and RUN", (s, e) => TypeSelected(true));
            Btn(bar, "Set suggested breakpoints", (s, e) => SetBreakpoints());
            newFirst = new CheckBox { Text = "NEW first (erase the program in memory)", Checked = true, AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
            bar.Controls.Add(newFirst);
            status = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 3, 0, 0) };

            Controls.Add(details);
            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(status);
            details.BringToFront();

            if (folder != null)
            {
                foreach (string f in Directory.GetFiles(folder, "*.bas").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try { programs.Add(SampleProgram.Load(f)); } catch { }
                }
            }
            foreach (SampleProgram p in programs) list.Items.Add(p.Title);
            list.SelectedIndexChanged += (s, e) => ShowSelected();
            if (programs.Count > 0) list.SelectedIndex = 0;
            else details.Text = "No sample programs were found. They live in basic/samples in the repository " +
                                "and are copied to a 'samples' folder next to the program when it is built.";
            status.Text = folder != null ? "Folder: " + folder : "";
        }

        static void Btn(Control parent, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += click;
            parent.Controls.Add(b);
        }

        SampleProgram Selected => list.SelectedIndex >= 0 && list.SelectedIndex < programs.Count ? programs[list.SelectedIndex] : null;

        void ShowSelected()
        {
            SampleProgram p = Selected;
            details.Text = p == null ? "" : p.Describe().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        void TypeSelected(bool run)
        {
            SampleProgram p = Selected;
            if (p == null) return;
            var machine = session.Machine;
            machine.Keyboard.Clear();
            int skipped = machine.TypeText((newFirst.Checked ? "NEW\r" : "") + p.Code + (run ? "RUN\r" : ""));
            if (!session.Running) session.Start();
            status.Text = $"Typing {p.Code.Length} characters into the MC-10 (fast)..." +
                          (skipped > 0 ? $"  {skipped} character(s) have no MC-10 key and were skipped." : "");
        }

        void SetBreakpoints()
        {
            SampleProgram p = Selected;
            if (p == null) return;
            var machine = session.Machine;
            var missing = new List<string>();
            int set = 0;
            foreach (string name in p.Breaks)
            {
                if (machine.TryResolve(name, out ushort a))
                {
                    if (!machine.HasBreakpoint(a)) machine.Breakpoints.Add(a);
                    set++;
                }
                else missing.Add(name);
            }
            session.NotifyChanged();
            status.Text = p.Breaks.Count == 0 ? "This sample suggests no breakpoints."
                : $"{set} breakpoint(s) set." + (missing.Count > 0 ? "  Unknown labels: " + string.Join(", ", missing) : "") +
                  "  Open the debugger with F12.";
        }
    }
}
