using System;
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

            MachineConfig config = configPath != null ? MachineConfig.Load(configPath) : MachineConfig.Default();

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(config, rom, symbols));
        }
    }
}
