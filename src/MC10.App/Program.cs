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

        static readonly string[] RomExtensions = { ".bin", ".rom", ".hex", ".ihx", ".s19" };

        /// <summary>
        /// Walks up from the build output folder looking for a <c>roms</c> directory and returns
        /// the first firmware image in it, or null. Keeps the ROM out of source control.
        /// </summary>
        static string FindDefaultRom()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                string roms = Path.Combine(dir.FullName, "roms");
                if (!Directory.Exists(roms)) continue;
                foreach (string f in Directory.GetFiles(roms))
                {
                    if (Array.IndexOf(RomExtensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                        return f;
                }
            }
            return null;
        }
    }
}
