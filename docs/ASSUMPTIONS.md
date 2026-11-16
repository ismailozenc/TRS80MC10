# What the manual confirms, and what is assumed

Source: *TRS-80 MC-10 Operation and Language Reference Manual* (scanned PDF, 152 pages, no text layer).

## Confirmed by the manual

| Item | Where (PDF page) |
|---|---|
| CPU: Motorola 6803 | Appendix E, Specifications (p. 141) |
| Text screen: 32 columns, 512 print positions (32 x 16) | `PRINT @` section (p. 55) |
| Block-graphics grid: 64 x 32 blocks | Graphics section (p. 107) |
| Nine colours: black, green, yellow, blue, red, buff, cyan, magenta, orange; CLS codes 0-8 | CLS / Table 3 (p. 66) |
| Default screen colour is green; `CLS` with no argument restores it | CLS section (p. 66) |
| `MEM` shows 3142 free bytes after reset (consistent with about 4 KB RAM) | p. 27 |
| `SOUND pitch,duration`, pitch 1-255 | Sound section (p. 32) |
| `INKEY$` polls the keyboard and empties the key latch | INKEY$ section |
| Serial printer: 600 baud, 7 data, 2 stop, no parity | Appendix E (p. 126) |
| Cassette: about 1500 baud | Cassette section |

## Not in the manual (assumed, configurable)

The manual is a BASIC user reference. It does not describe the hardware memory map, the ROM contents,
the keyboard matrix or the video-chip wiring. The emulator therefore uses these defaults, all in
`MachineConfig`:

| Item | Default | Status |
|---|---|---|
| Clock | 894886 Hz | Assumed (about 0.89 MHz) |
| Internal RAM | `$0040`-`$00FF` (128 bytes) | Standard 6803 location. Firmware writes `$00BD` and tests `$00EA`. |
| External RAM | `$4000`-`$4FFF`, 4 KB | Inferred from firmware: variables/vectors at `$4200`-`$42FF`, stack near `$419A`, `LDX $4221`. Internal 6803 registers overlay `$0000`-`$001F`. |
| Text screen RAM | `$4000`, 512 bytes | **Guess.** Not yet located in the firmware. |
| ROM window | `$E000`-`$FFFF`, 8 KB (matches the uploaded 8 KB image, whose reset vector `$FFFE` = `$F72E`) | Assumed from the image size and vectors, not from the manual. The 6803 vectors at `$FFF0`-`$FFFF` are architectural. A 16 KB layout is still possible via config. |
| Keyboard | 8 columns strobed via port 1 (active low), 6 rows read from `$BFFF` bits 0-5 (active low); key index = row*8+col (0-26 `@A-Z`, 30 ENTER, 31 SPACE, 32-39 `0-7`, 40-47 `8 9 : ; , - . /`) | **Derived from the firmware scan routine** (`$F8A1`-`$F8F3`) and its translation tables. Shift/Ctrl/Break on port 2 bit 1 with column 7/2/0 strobed is an inference; keys 27-29 (unidentified) are not mapped. |
| Sound register | `$8002` | **Placeholder.** The frequency mapping is not implemented; the events are recorded for inspection. |
| Video control register | `$8003` (bits 0-3 background, 4-7 text colour) | **Placeholder.** |
| Semigraphics | Codes `$80`-`$FF`: bits 0-3 select the four 2x2 blocks | Assumed layout. The character-generator ROM is not emulated; printable codes use the system monospace font. |

Replace the placeholders by editing the JSON config (`MachineConfig.Save` writes the defaults) once the
real addresses are confirmed from the firmware or a schematic.

## Disassembly

The listing comes from static analysis (`CodeAnalyzer`), not from running the firmware. It follows branches, jumps and
calls from the eight interrupt vectors, and treats runs of 3+ valid code pointers in leftover bytes as dispatch tables
(entries named `fn_XXXX`). Code reached only through `JMP n,X` or RAM vectors can show up as data. Names are
automatic; load your own with a symbol file (`NAME $ADDR`) and they win over the automatic ones.

## CPU timing

Instruction cycle counts follow the MC6800/6803 data-sheet family. Some are grouped in the data sheet, so
they are approximate. The free-running counter runs at E/4, and timer flags (ICF/OCF/TOF) raise interrupts
when their enables are set. Serial I/O (SCI) is not implemented; the transmitter always reports empty.

## Unverified

- No firmware dump is included. The repository was empty. A dump must be supplied by the user.
- The C# code has not been compiled or run in this environment (no .NET SDK is installed here).
  See README for build steps.
