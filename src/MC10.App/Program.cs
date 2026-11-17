using System;
using System.IO;
using System.Windows.Forms;
using MC10.Core;

namespace MC10.App
{
    internal static class Program
    {
        /// <summary>
        /// Usage: MC10.App [--rom FILE] [--symbols FILE] [--config FILE.json]
        /// ROM files may be raw binary (placed at the configured ROM base) or Intel HEX / S-record.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            string rom = null, symbols = null, configPath = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--rom": rom = args[++i]; break;
                    case "--symbols": symbols = args[++i]; break;
                    case "--config": configPath = args[++i]; break;
                }
            }

            // No --rom given (e.g. Visual Studio F5): look for a ROM in the repo's roms folder.
            rom ??= FindDefaultRom();

            MachineConfig config = configPath != null ? MachineConfig.Load(configPath) : MachineConfig.Default();

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(config, rom, symbols));
        }

        /// <summary>
        /// Labels and run data produced by tools/mc10_label.py (symbols/mc10.sym, .targets, .cov), searched
        /// from the build output folder upwards. Returned in load order (symbols first).
        /// </summary>
        internal static System.Collections.Generic.List<string> FindDefaultRunData()
        {
            var result = new System.Collections.Generic.List<string>();
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                string folder = Path.Combine(dir.FullName, "symbols");
                if (!Directory.Exists(folder)) continue;
                foreach (string name in new[] { "mc10.sym", "mc10.targets", "mc10.cov", "user.sym" })
                {
                    string f = Path.Combine(folder, name);
                    if (File.Exists(f)) result.Add(f);
                }
                if (result.Count > 0) break;
            }
            return result;
        }

        /// <summary>Folder with sample BASIC programs: "samples" next to the exe, or basic/samples in the repo.</summary>
        internal static string FindSamplesFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                foreach (string rel in new[] { "samples", Path.Combine("basic", "samples") })
                {
                    string folder = Path.Combine(dir.FullName, rel);
                    if (Directory.Exists(folder) && Directory.GetFiles(folder, "*.bas").Length > 0) return folder;
                }
            }
            return null;
        }

        static readonly string[] RomExtensions = { ".bin", ".rom", ".hex", ".ihx", ".s19" };

        /// <summary>
        /// Finds a firmware image without command-line arguments (Visual Studio F5). Searches, from the
        /// build output folder upwards: the folder itself and a <c>roms</c> subfolder. The folder itself is
        /// only searched when it is the output folder or a project folder (contains a .csproj), so stray
        /// files in higher-level folders are never picked up. MC10.App.csproj also copies *.bin / *.rom
        /// from the project folder to the output folder.
        /// </summary>
        static string FindDefaultRom()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                bool isOutput = depth == 0;
                bool isProject = dir.GetFiles("*.csproj").Length > 0;
                if (isOutput || isProject)
                {
                    string hit = FirstRom(dir.FullName);
                    if (hit != null) return hit;
                }
                string roms = Path.Combine(dir.FullName, "roms");
                if (Directory.Exists(roms))
                {
                    string hit = FirstRom(roms);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        static string FirstRom(string folder)
        {
            string[] files = Directory.GetFiles(folder);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
                if (Array.IndexOf(RomExtensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                    return f;
            return null;
        }
    }
}
