# TRS-80 MC-10 emulator (C# / WinForms)

A Motorola 6803 based MC-10 emulator for Windows, with:

- **CPU**: full MC6800 instruction set plus the 6801/6803 additions (MUL, ABX, ASLD/LSRD, PSHX/PULX, XGDX, WAI), internal timer and ports, and the NMI / IRQ / ICI / OCI / TOI / SWI / RESET interrupt model.
- **EPROM**: read-only ROM or byte-programmable (EEPROM-style) chip, with CRC-32 and checksum. Loads raw `.bin`/`.rom`, Intel HEX (`.hex`), and Motorola S-record (`.s19`).
- **RAM and memory map**: configurable through a JSON file.
- **Screen**: 32 x 16 text with the nine MC-10 colours and 2x2 block graphics.
- **Keyboard**: keystrokes go into a queue that firmware reads, with BREAK on Esc.
- **Sound**: each write is recorded (cycle and pitch) for inspection.
- **Debugger**: disassembly with breakpoints, single-step and step-over, run-to-cursor, registers and flags, hex memory with poke, write-watchpoints, instruction history, and a **trace log to file**.
- **Tests**: xUnit tests for flag rules, stack and interrupt sequencing, watchpoints, breakpoints, EPROM policy and image loaders.

See `docs/ASSUMPTIONS.md` for what the manual confirms and what is assumed.

## You need to supply the firmware

This repository contains **no ROM**. The MC-10 ROM is copyrighted Tandy/Radio Shack firmware, and it was not in the empty repository. Dump your own ROM and load it with `--rom` or **File > Open ROM**.

## Build and run (Windows)

Requires the .NET 8 SDK and Windows.

```powershell
cd trs80mc10
dotnet build src\MC10.App -c Release
dotnet run --project src\MC10.App -- --rom C:\path\to\mc10.rom
dotnet test tests\MC10.Tests
```

Optional arguments: `--symbols FILE` (lines of `NAME ADDRESS`, `;` comments allowed) and `--config FILE.json`.

To create a config file with the defaults, call `MachineConfig.Default().Save("mc10.json")`, or edit the file the app writes.

## Keys and menus

| Action | Key |
|---|---|
| Run / pause | F5 |
| Reset | Ctrl+R |
| Debugger window | F12 |
| Debugger: step / step over / run-pause | F10 / F11 / F5 |
| Debugger: toggle breakpoint on the selected line | Ctrl+B or double-click |

Typed letters are sent in uppercase, matching the MC-10's default all-caps keyboard. Enter, Backspace, and the Left arrow (as backspace) are supported. Esc sends BREAK.

## Architecture

```
src/MC10.Core          (no UI, unit-testable)
  Opcodes.cs           one 256-entry table, shared by CPU, disassembler and debugger
  Cpu6803.cs           CPU core, internal I/O, timer, interrupts
  Bus.cs               64 KB map: RAM / ROM / device / internal I/O / unmapped
  Eprom.cs             ROM chip model and HEX / S-record / BIN loaders
  Devices.cs           keyboard, sound, video control
  Video.cs             screen model (text and block graphics)
  Disassembler.cs      MC6803 disassembler
  Machine.cs           wiring, run loop, breakpoints, watchpoints, trace, history
  MachineConfig.cs     memory map and clock (JSON)
src/MC10.App           WinForms front end
  MainForm.cs          screen, keyboard, menus, trace-to-file, screenshot
  DebuggerForm.cs      disassembly, registers, memory, watchpoints, history
  EmulatorSession.cs   shared run state (60 Hz frame pump)
tests/MC10.Tests       xUnit tests
```

## Trace log format

One line per instruction, written before it executes:

```
C000  86 10     LDAA  #$10       A=00 B=00 X=0000 SP=0FFF CC=C0  cyc=12345
```

Trace files grow fast at full speed. Use **Machine > Speed** to slow the machine, or trace only a short run.

## Limitations and next steps

- **Not compiled here.** This code was written on a machine with no .NET SDK. Expect a small number of compile fixes on the first build. The tests are written but have not been run.
- The memory map, keyboard, sound, and video-control addresses are assumptions (see `docs/ASSUMPTIONS.md`). Confirm them against the firmware or a schematic.
- Not yet implemented: cassette (CSAVE/CLOAD) tape interface, RS-232 serial, sound frequency mapping and WAV export, a character-generator ROM for exact glyphs, save/load of machine state, and a built-in assembler. These are good next additions.
- Timing is instruction-level. Cycle counts are approximate and do not model bus timing or video-raster timing.

## Running an 8 KB firmware image

`config/mc10-8k-rom.json` maps an 8 KB ROM at `$E000`–`$FFFF`, which is where the image's reset vector
(`$FFFE`) points. Keep your dump outside the repo (ROM files are git-ignored) and run:

```powershell
dotnet run --project src\MC10.App -- --config config\mc10-8k-rom.json --rom C:\path\to\your-mc10.bin
```

The keyboard, sound and video-control addresses in that file are still the placeholders listed in `docs/ASSUMPTIONS.md`.
