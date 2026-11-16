using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MC10.Core
{
    /// <summary>
    /// Memory map and clock for the emulated machine. The Tandy MC-10 manual gives the
    /// CPU (6803), screen geometry (32 x 16 text, 64 x 32 blocks) and RAM size (about 4 KB),
    /// but not the address map or device addresses. Those defaults are assumptions and can be
    /// overridden with a JSON file. See docs/ASSUMPTIONS.md.
    /// </summary>
    public sealed class MachineConfig
    {
        public int ClockHz { get; set; } = 894886;        // ~0.89 MHz E-clock (assumed)

        // Firmware evidence (see docs/ASSUMPTIONS.md): variables and vectors at $4200-$42FF,
        // stack near $419A, and a test of $00EA. That suggests 6803 internal RAM at $0040-$00FF
        // plus 4 KB external RAM at $4000-$4FFF.
        public int InternalRamBase { get; set; } = 0x0040;
        public int InternalRamSize { get; set; } = 0x00C0;
        public int RamBase { get; set; } = 0x4000;
        public int RamSize { get; set; } = 4096;          // ~4 KB, consistent with the manual's MEM figure

        public int VideoBase { get; set; } = 0x4000;      // GUESS: text screen RAM, not yet confirmed from firmware
        public int Columns { get; set; } = 32;            // from the manual
        public int Rows { get; set; } = 16;               // 32 x 16 = 512 print positions (manual)

        public int RomBase { get; set; } = 0xE000;
        public int RomSize { get; set; } = 0x2000;        // 8 KB image at the top of memory (reset vector $FFFE)
        public EpromMode RomMode { get; set; } = EpromMode.ReadOnly;

        // Placeholder device addresses. They must lie outside the ROM window (checked by Machine.LoadRom).
        public int KeyboardData { get; set; } = 0x8000;
        public int KeyboardStatus { get; set; } = 0x8001;
        public int SoundRegister { get; set; } = 0x8002;
        public int VideoControl { get; set; } = 0x8003;

        public static MachineConfig Default() => new MachineConfig();

        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static MachineConfig Load(string path)
        {
            return JsonSerializer.Deserialize<MachineConfig>(File.ReadAllText(path), JsonOptions)
                   ?? new MachineConfig();
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
