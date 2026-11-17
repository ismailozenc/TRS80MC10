#!/usr/bin/env python3
"""Type every program in basic/samples into the headless emulator, RUN it and record the screen.

Usage: run_samples.py ROM.BIN [--out docs/SAMPLES.md]
Lines starting with ';;' in a sample are notes and are not typed (same rule as the WinForms app).
"""
import argparse, glob, os, sys
from concurrent.futures import ProcessPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mc10_label as L
from mc10_emu import load_rom

# Keys to press after RUN for the interactive samples.
ANSWERS = {"08_input.bas": ["10\r", "5\r", "15\r", "1\r", "2\r", "3\r", "4\r", "6\r", "7\r", "8\r", "9\r",
                            "11\r", "12\r", "13\r", "14\r", "16\r", "17\r", "18\r", "19\r", "20\r"],
           "09_inkey.bas": ["A", "7", "Q"]}


def parse(path):
    notes, code = [], []
    for line in open(path).read().splitlines():
        if line.startswith(";;"): notes.append(line[2:].strip())
        elif line.strip(): code.append(line.strip())
    return notes, code


def type_text(m, text, settle=400):
    for ch in text: m.kbd.tap_char(ch)
    while m.kbd.busy: m.run_frames(1)
    L.settle(m, settle)


def at_prompt(m):
    rows = [r for r in m.screen_rows() if r.strip() and r.strip() != "#"]
    return bool(rows) and rows[-1].strip() == "OK"


def wait_prompt(m, max_frames):
    for _ in range(max_frames // 5):
        m.run_frames(5)
        if at_prompt(m): return True
    return False


def run_one(args):
    rom_path, path = args
    notes, code = parse(path)
    m = L.boot(load_rom(rom_path))
    for line in code: type_text(m, line + "\r", 100)
    name = os.path.basename(path)
    type_text(m, "RUN\r", 60)
    if name in ANSWERS:
        for ans in ANSWERS[name]:
            if at_prompt(m): break
            type_text(m, ans, 300)
    wait_prompt(m, 1500 if "GOTO 90" in code[-1] else 6000)
    return os.path.basename(path), notes, code, m.screen_text(), sorted(a for a in m.cov if a >= 0xE000)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rom")
    ap.add_argument("--out", default="docs/SAMPLES.md")
    args = ap.parse_args()
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    files = sorted(glob.glob(os.path.join(root, "basic", "samples", "*.bas")))
    with ProcessPoolExecutor() as ex:
        results = list(ex.map(run_one, [(args.rom, f) for f in files]))
    with open(args.out, "w") as f:
        f.write("# Sample BASIC programs\n\nIn the emulator: **Programs > Sample programs (F4)**. "
                "Each screen below is what the headless emulator (`tools/run_samples.py`) showed after RUN.\n")
        for name, notes, code, screen, cov in results:
            f.write("\n## %s\n\n" % name)
            for n in notes: f.write("- %s\n" % n)
            f.write("\n```basic\n%s\n```\n\nScreen after RUN (`#` = colour block or cursor, lowercase = reverse video):\n\n```\n%s\n```\n" % ("\n".join(code), screen))
            print("=====", name); print(screen)
    return results


if __name__ == "__main__":
    main()
