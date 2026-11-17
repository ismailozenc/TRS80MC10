using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MC10.App
{
    /// <summary>Small modal "enter one line of text" dialog (WinForms has no built-in input box).</summary>
    internal sealed class PromptDialog : Form
    {
        readonly TextBox box;

        PromptDialog(string title, string message, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 110);

            var label = new Label { Text = message, Left = 12, Top = 12, Width = 396, Height = 32, AutoSize = false };
            box = new TextBox { Left = 12, Top = 48, Width = 396, Text = initial ?? "" };
            var ok = new Button { Text = "OK", Left = 252, Top = 76, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 333, Top = 76, Width = 75, DialogResult = DialogResult.Cancel };
            Controls.Add(label);
            Controls.Add(box);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => { box.SelectAll(); box.Focus(); };
        }

        /// <summary>Returns the entered text, or null if the user cancelled.</summary>
        public static string Ask(IWin32Window owner, string title, string message, string initial = "")
        {
            using (var dlg = new PromptDialog(title, message, initial))
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.box.Text : null;
        }
    }

    /// <summary>Simple list of text lines; double-click or Enter reports the chosen index.</summary>
    internal sealed class ListDialog : Form
    {
        readonly ListBox list;

        public ListDialog(string title, IList<string> items, Action<int> activate)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 320);
            ShowInTaskbar = false;
            list = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point),
                IntegralHeight = false,
                HorizontalScrollbar = true,
            };
            foreach (string s in items) list.Items.Add(s);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.DoubleClick += (s, e) =>
            {
                if (list.SelectedIndex >= 0) activate?.Invoke(list.SelectedIndex);
            };
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0) { activate?.Invoke(list.SelectedIndex); e.Handled = true; }
                if (e.KeyCode == Keys.Escape) Close();
            };
            Controls.Add(list);
        }
    }

    /// <summary>List view that double-buffers, so refreshing it while the machine runs does not flicker.</summary>
    internal sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            GridLines = true;
            HideSelection = false;
            Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        }

        public void AddColumn(string title, int width) => Columns.Add(title, width);
    }

    /// <summary>Colours shared by the debugger views.</summary>
    internal static class Theme
    {
        public static readonly Color Address = Color.FromArgb(120, 120, 120);
        public static readonly Color HexBytes = Color.FromArgb(150, 150, 150);
        public static readonly Color Branch = Color.FromArgb(0, 90, 200);
        public static readonly Color Call = Color.FromArgb(160, 0, 160);
        public static readonly Color Return = Color.FromArgb(200, 0, 0);
        public static readonly Color Load = Color.FromArgb(0, 125, 95);
        public static readonly Color Store = Color.FromArgb(205, 105, 0);
        public static readonly Color Stack = Color.FromArgb(90, 90, 160);
        public static readonly Color Compare = Color.FromArgb(125, 85, 0);
        public static readonly Color Alu = Color.FromArgb(25, 25, 25);
        public static readonly Color Special = Color.FromArgb(150, 0, 0);
        public static readonly Color Name = Color.FromArgb(0, 70, 150);
        public static readonly Color Number = Color.FromArgb(165, 70, 0);
        public static readonly Color Register = Color.FromArgb(110, 110, 110);
        public static readonly Color Label = Color.FromArgb(0, 0, 160);
        public static readonly Color Comment = Color.FromArgb(0, 128, 0);
        public static readonly Color Data = Color.FromArgb(120, 120, 120);

        public static readonly Color PcBack = Color.FromArgb(255, 240, 150);
        public static readonly Color SelectedBack = Color.FromArgb(205, 224, 255);
        public static readonly Color CoveredBack = Color.FromArgb(230, 246, 230);
        public static readonly Color BannerBack = Color.FromArgb(240, 243, 250);
        public static readonly Color Changed = Color.FromArgb(220, 0, 0);
        public static readonly Color WatchBack = Color.FromArgb(255, 220, 170);
    }
}
