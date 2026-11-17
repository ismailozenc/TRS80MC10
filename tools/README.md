# Tools: finding out what the firmware does by running it

These are Python 3 scripts with no dependencies. They are not needed to build or run the C# emulator.

* `mc10_emu.py` – headless MC-10 (6803 CPU, timer, ports, keyboard matrix, RAM/ROM map) mirroring `MC10.Core`.
  `python tools/mc10_emu.py path/to/MC10.BIN` boots the firmware and prints the screen.
* `mc10_label.py` – boots the ROM, types ~66 BASIC tests (every statement, function and operator plus error
  cases), records executed addresses, call targets and I/O accesses, checks that each BASIC handler in the
  dispatch tables really ran, and writes:
  * `symbols/mc10.sym`     – labels (`stm_PRINT $E71F`, `fn_SIN $F68C`, `op_ADD $EF80`, table labels, I/O routines)
  * `symbols/mc10.cov`     – executed instruction ranges
  * `symbols/mc10.targets` – run-time call/jump targets (analyzer entry points)
  * `docs/ROUTINES.md`     – verification table

  `python tools/mc10_label.py path/to/MC10.BIN` (about 90 s). The ROM itself is never written out.

The WinForms app loads `symbols/mc10.*` automatically (they are copied next to the executable) and shades
executed lines green in the ROM listing. **Analysis > Type BASIC file** feeds `basic/*.bas` into the keyboard,
and **Analysis > Re-analyse ROM using run data** folds the observed call targets back into the disassembly.

Older exploration scripts (usage: `python tools/<script> MC10.BIN`):

* `mc10_dis.py` – quick standalone 6803 disassembler of the ROM.
* `mc10_mini_keyboard.py` – small executor used to verify the keyboard scan routine at `$F883`.
