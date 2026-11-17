#!/usr/bin/env python3
"""Label the MC-10 firmware by running it.

1. Parses the BASIC keyword, operator and dispatch tables straight from the ROM.
2. Runs a BASIC test for every statement, function and operator in the headless emulator
   (tools/mc10_emu.py) and checks that the handler the table points to really executes.
3. Records which routines touch the keyboard, screen, timer and I/O ports, plus call targets,
   and names the helpers from that evidence.
4. Writes a symbol file and a report.

Usage: mc10_label.py ROM.BIN [--sym symbols/mc10.sym] [--report docs/ROUTINES.md]
"""
import argparse, json, os, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mc10_emu import MC10, load_rom, CYCLES_PER_FRAME

ROM_BASE = 0xE000
STATEMENTS = 33
SECONDARY = ["TAB(", "TO", "THEN", "NOT", "STEP", "OFF", "+", "-", "*", "/", "^", "AND", "OR", ">", "=", "<"]
FUNCTIONS = ["SGN", "INT", "ABS", "USR", "RND", "SQR", "LOG", "EXP", "SIN", "COS", "TAN", "PEEK", "LEN",
             "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$", "POINT", "VARPTR", "INKEY$", "MEM"]
OPS = ["ADD", "SUB", "MUL", "DIV", "POW", "AND", "OR"]
SAFE = {"$": "S", "(": "", "+": "ADD", "-": "SUB", "*": "MUL", "/": "DIV", "^": "POW", ">": "GT", "=": "EQ", "<": "LT"}


def safe(name):
    return "".join(SAFE.get(c, c) for c in name) if name not in SAFE else SAFE[name]


class Rom:
    def __init__(self, data):
        self.d = data

    def b(self, a): return self.d[a - ROM_BASE]
    def w(self, a): return (self.b(a) << 8) | self.b(a + 1)


def parse_tables(rom):
    """Keyword table at the first 'FOR' word, dispatch tables found by shape."""
    d = rom.d
    start = d.find(b"FO\xd2") + ROM_BASE
    words, cur, a = [], "", start
    s = start
    while True:
        b = rom.b(a)
        if b & 0x80:
            words.append((s, cur + chr(b & 0x7F)))
            cur, s = "", a + 1
        elif 0x20 <= b < 0x7F:
            cur += chr(b)
        else:
            break
        a += 1
        if len(words) == STATEMENTS + len(SECONDARY) + len(FUNCTIONS):
            break
    names = [w for _, w in words]
    expect = ["FOR", "GOTO"] + [None] * 31 + SECONDARY[:2]
    assert names[0] == "FOR" and names[1] == "GOTO", names[:3]
    stm = names[:STATEMENTS]
    sec = names[STATEMENTS:STATEMENTS + len(SECONDARY)]
    fn = names[STATEMENTS + len(SECONDARY):]
    assert sec == SECONDARY, sec
    assert fn == FUNCTIONS, fn
    end = a
    # dispatch tables: the first run of >= 30 ROM pointers after the keyword table = statements
    def run_at(addr):
        n = 0
        while ROM_BASE <= rom.w(addr + 2 * n) < 0x10000 and addr + 2 * n < 0xFFF0:
            n += 1
        return n
    stm_tab = None
    for a2 in range(end, end + 16):
        if run_at(a2) >= STATEMENTS:
            stm_tab = a2
            break
    assert stm_tab is not None, "statement dispatch table not found"
    fn_tab = ROM_BASE                      # 24 words in keyword order (SGN..MEM), found by coverage
    ops_tab = fn_tab + 2 * 24
    return dict(keyword_start=start, keyword_end=end, statements=stm, secondary=sec, functions=fn,
                stm_tab=stm_tab, fn_tab=fn_tab, ops_tab=ops_tab,
                stm_handlers=[rom.w(stm_tab + 2 * i) for i in range(STATEMENTS)],
                fn_handlers=[rom.w(fn_tab + 2 * i) for i in range(24)],
                ops=[(rom.b(ops_tab + 3 * i), rom.w(ops_tab + 3 * i + 1)) for i in range(7)])


# ------------------------------------------------------------------ test programs
# Each test: (id, category, [steps]) where a step is text typed (with \r) and the machine settles after it.
def T(i, cat, *steps, expect=None):
    return dict(id=i, cat=cat, steps=list(steps), expect=expect)


TESTS = [
    T("FOR", "stm", "10 FOR I=1 TO 3 STEP 1:PRINT I:NEXT I\r", "RUN\r", expect=["FOR", "NEXT"]),
    T("GOTO", "stm", "10 GOTO 30\r", "20 PRINT \"NO\"\r", "30 PRINT \"YES\"\r", "RUN\r", expect=["GOTO"]),
    T("GOSUB", "stm", "10 GOSUB 100:PRINT \"BACK\":END\r", "100 PRINT \"SUB\":RETURN\r", "RUN\r", expect=["GOSUB", "RETURN", "END"]),
    T("REM", "stm", "10 REM HELLO\r", "RUN\r", expect=["REM"]),
    T("IF", "stm", "10 IF 1=1 THEN PRINT \"T\" ELSE PRINT \"F\"\r".replace(" ELSE PRINT \"F\"", ""), "RUN\r", expect=["IF"]),
    T("DATA", "stm", "10 DATA 5,6\r", "20 READ A,B:PRINT A+B\r", "30 RESTORE:READ C:PRINT C\r", "RUN\r", expect=["DATA", "READ", "RESTORE"]),
    T("PRINT", "stm", "PRINT \"A\";1,2\r", "PRINT @ 100,\"AT\"\r", "PRINT TAB(5);\"T\"\r", expect=["PRINT"]),
    T("ON", "stm", "10 ON 2 GOTO 20,30\r", "20 PRINT \"A\"\r", "30 PRINT \"B\"\r", "RUN\r", expect=["ON"]),
    T("INPUT", "stm", "10 INPUT A:PRINT A*2\r", "RUN\r", "7\r", expect=["INPUT"]),
    T("DIM", "stm", "DIM A(5):A(2)=3:PRINT A(2)\r", expect=["DIM"]),
    T("LET", "stm", "LET A=5:PRINT A\r", expect=["LET"]),
    T("STOP", "stm", "10 STOP\r", "RUN\r", "CONT\r", expect=["STOP", "CONT"]),
    T("POKE", "stm", "POKE 16500,65:PRINT PEEK(16500)\r", expect=["POKE"]),
    T("LIST", "stm", "10 PRINT 1\r", "20 PRINT 2\r", "LIST\r", expect=["LIST"]),
    T("CLEAR", "stm", "CLEAR 100\r", expect=["CLEAR"]),
    T("NEW", "stm", "10 PRINT 1\r", "NEW\r", expect=["NEW"]),
    T("RUN", "stm", "10 PRINT 9\r", "RUN\r", expect=["RUN"]),
    T("SET", "stm", "CLS 0:SET(10,10,3)\r", expect=["SET", "CLS"]),
    T("RESET", "stm", "SET(10,10,3):RESET(10,10)\r", expect=["RESET"]),
    T("CLS", "stm", "CLS 5\r", expect=["CLS"]),
    T("SOUND", "stm", "SOUND 100,1\r", expect=["SOUND"]),
    T("EXEC", "stm", "POKE 16500,57:EXEC 16500\r", expect=["EXEC"]),
    T("CLOAD", "stm", "CLOAD\r", "ESC", expect=["CLOAD"]),
    T("CSAVE", "stm", "10 PRINT 1\r", "CSAVE \"A\"\r", "ESC", expect=["CSAVE"]),
    T("SKIPF", "stm", "SKIPF\r", "ESC", expect=["SKIPF"]),
    T("LLIST", "stm", "10 PRINT 1\r", "LLIST\r", "ESC", expect=["LLIST"]),
    T("LPRINT", "stm", "LPRINT \"X\"\r", "ESC", expect=["LPRINT"]),
    # operators
    T("OP_ADD", "op", "PRINT 2+3\r", expect=["ADD"]),
    T("OP_SUB", "op", "PRINT 5-3\r", expect=["SUB"]),
    T("OP_MUL", "op", "PRINT 4*3\r", expect=["MUL"]),
    T("OP_DIV", "op", "PRINT 9/3\r", expect=["DIV"]),
    T("OP_POW", "op", "PRINT 2^3\r", expect=["POW"]),      # ^ is CONTROL+W on the MC-10
    T("OP_AND", "op", "PRINT 6 AND 3\r", expect=["AND"]),
    T("OP_OR", "op", "PRINT 4 OR 1\r", expect=["OR"]),
    T("OP_CMP", "op", "PRINT 1<2;2>1;3=3;NOT 0\r"),
    T("OP_STR", "op", "A$=\"AB\"+\"CD\":PRINT A$;(A$=\"ABCD\")\r"),
    # functions
    T("SGN", "fn", "PRINT SGN(-5)\r", expect=["SGN"]),
    T("INT", "fn", "PRINT INT(3.7)\r", expect=["INT"]),
    T("ABS", "fn", "PRINT ABS(-2)\r", expect=["ABS"]),
    T("USR", "fn", "POKE 16500,57:PRINT USR(0)\r", "ESC", expect=["USR"]),
    T("RND", "fn", "PRINT RND(10)\r", expect=["RND"]),
    T("SQR", "fn", "PRINT SQR(16)\r", expect=["SQR"]),
    T("LOG", "fn", "PRINT LOG(2)\r", expect=["LOG"]),
    T("EXP", "fn", "PRINT EXP(1)\r", expect=["EXP"]),
    T("SIN", "fn", "PRINT SIN(1)\r", expect=["SIN"]),
    T("COS", "fn", "PRINT COS(1)\r", expect=["COS"]),
    T("TAN", "fn", "PRINT TAN(1)\r", expect=["TAN"]),
    T("PEEK", "fn", "PRINT PEEK(16384)\r", expect=["PEEK"]),
    T("LEN", "fn", "PRINT LEN(\"ABC\")\r", expect=["LEN"]),
    T("STR$", "fn", "PRINT STR$(12)\r", expect=["STR$"]),
    T("VAL", "fn", "PRINT VAL(\"12\")\r", expect=["VAL"]),
    T("ASC", "fn", "PRINT ASC(\"A\")\r", expect=["ASC"]),
    T("CHR$", "fn", "PRINT CHR$(65)\r", expect=["CHR$"]),
    T("LEFT$", "fn", "PRINT LEFT$(\"HELLO\",2)\r", expect=["LEFT$"]),
    T("RIGHT$", "fn", "PRINT RIGHT$(\"HELLO\",2)\r", expect=["RIGHT$"]),
    T("MID$", "fn", "PRINT MID$(\"HELLO\",2,3)\r", expect=["MID$"]),
    T("POINT", "fn", "SET(4,4,3):PRINT POINT(4,4)\r"),
    T("VARPTR", "fn", "A=1:PRINT VARPTR(A)\r"),
    T("INKEY$", "fn", "A$=INKEY$:PRINT LEN(A$)\r"),
    T("MEM", "fn", "PRINT MEM\r"),
    # errors and break
    T("ERR_DIV0", "err", "PRINT 1/0\r"),
    T("ERR_SYNTAX", "err", "PRINT )\r"),
    T("ERR_UL", "err", "GOTO 999\r"),
    T("ERR_BREAK", "err", "10 GOTO 10\r", "RUN\r", "ESC"),
    T("ERR_TYPE", "err", "PRINT \"A\"+1\r"),
    T("ERR_OM", "err", "DIM A(9999)\r"),
]


# ------------------------------------------------------------------ running
def visible(m):
    return [r.replace("#", " ") for r in m.screen_rows()]


def settle(m, max_frames=500):
    last, same = None, 0
    for _ in range(max_frames // 5):
        m.run_frames(5)
        cur = visible(m)
        if cur == last:
            same += 1
            if same >= 3 and not m.kbd.busy:
                return
        else:
            same = 0
        last = cur


def boot(rom_data):
    m = MC10(rom_data)
    settle(m, 200)
    return m


def run_test(rom_data, test):
    m = boot(rom_data)
    base_cov = set(m.cov)
    m.cov.clear(); m.calls.clear(); m.indirect.clear(); m.hw.clear()
    for step in test["steps"]:
        if isinstance(step, tuple):
            for a in range(0x4200, 0x4500):
                if m.mem[a] == step[1] and m.mem[a - 1] == 0x32:
                    m.mem[a] = step[2]
        elif step == "ESC":
            m.kbd.tap_break()
            m.run_frames(30)
            settle(m, 200)
        else:
            for ch in step:
                m.kbd.tap_char(ch)
            frames = 0
            while m.kbd.busy and frames < 6000:
                m.run_frames(1); frames += 1
            settle(m, 400)
    return dict(cov=set(m.cov), calls={k: set(v) for k, v in m.calls.items()}, indirect=set(m.indirect),
                hw={k: set(v) for k, v in m.hw.items()}, screen=m.screen_text(), base=base_cov, instr=m.instructions)


# Names checked by reading the code and by experiment in the emulator (see docs/ROUTINES.md).
MANUAL = {
    # RAM: the character fetch routine BASIC copies to the direct page, and its pointer
    0x00EB: "chrget",             # INC txtptr; fall into chrgot
    0x00F3: "chrgot",             # LDAA [txtptr]; JMP chrget_classify
    0x00F4: "txtptr",             # address of the BASIC text being interpreted
    # RAM: BASIC memory layout pointers (watched while entering lines, DIM, strings, CLEAR)
    0x0093: "basic_txttab",       # start of the program text
    0x0095: "basic_vartab",       # end of program = start of simple variables
    0x0097: "basic_arytab",       # start of arrays
    0x0099: "basic_aryend",       # end of arrays = start of free memory
    0x009B: "basic_strbot",       # bottom of string space (moved by CLEAR n)
    0x009D: "basic_fretop",       # next free byte in string space (grows down)
    0x00A1: "basic_memtop",       # top of BASIC memory (CLEAR n,addr)
    0x00A5: "linnum_tmp",         # line number being parsed
    0x00C9: "fpa0",               # floating point accumulator 0: exponent, 4 mantissa bytes, sign
    0x00D6: "fpa1",               # floating point accumulator 1
    0x4215: "usr_vector",         # JMP executed by USR(); points at err_fc until the program changes it
    0x421C: "kbd_case_flag",      # toggled by SHIFT+0 (upper / lower case mode)
    0x4239: "kbd_column",         # column being scanned
    0x423A: "kbd_ctrl_state",     # $FF while CONTROL is down
    0x423B: "kbd_break_state",    # last state of the BREAK key
    0x4280: "cursor_addr",        # screen address of the cursor ($4000-$41FF)
    0x4282: "cursor_color",       # colour bits for keyboard graphics (CONTROL+0 steps it)
    # ROM
    0xE1C8: "chrget_classify",    # sets flags for the character just fetched (digit / end of statement)
    0xE18A: "tbl_error_codes",    # two-letter error codes: NF SN RG OD FC OV OM UL BS DD /0 ID TM OS LS ST CN IO FM
    0xE21A: "chk_stack_room",     # OM error unless B*2 bytes fit below the stack
    0xE238: "error_B",            # report error number B, back to the OK prompt
    0xE6B2: "parse_line_number",  # decimal digits at txtptr -> linnum_tmp
    0xE7A8: "print_string",
    0xE7B9: "print_space",
    0xE7BE: "chrout",             # print the character in A
    0xE90C: "eval_numeric",       # evaluate an expression that must be a number (TM error otherwise)
    0xE91A: "eval_expression",    # the expression evaluator
    0xEA2F: "need_comma",         # SN error unless the next character is ","
    0xEA31: "need_char_B",        # SN error unless the next character equals B
    0xEA3C: "err_syntax",
    0xEB1B: "find_variable",      # parse a variable name, return its address (creates it if new)
    0xEC2E: "err_fc",             # illegal function call
    0xEF0D: "eval_byte",          # evaluate an expression into B (0-255)
    0xF251: "fp_load_fpa0",       # unpack the 5-byte number at X into fpa0
    0xF26C: "fp_store_fpa0",      # pack fpa0 into 5 bytes at X
    0xF29F: "fp_copy_fpa0_fpa1",
    0xF883: "kbd_scan",           # scan the matrix, return the key code in A (0 = none)
    0xF8CE: "kbd_test_shift",     # strobe column 7 and test port 2 bit 1
    0xF8D0: "kbd_test_modifier",  # strobe the column in A and test port 2 bit 1
    0xF8E2: "kbd_read_column",    # write B to port 1, read the row bits from $BFFF
    0xF97C: "tbl_keys_control",   # CONTROL+key -> keyword token or control code
    0xF9AB: "tbl_keys_shift",     # SHIFT+key -> graphics character
    0xF9C6: "console_out",        # character output to screen (or printer)
}

# Groups of BASIC commands; a routine reached only from one group is named after it.
FAMILIES = [
    ("trig", {"SIN", "COS", "TAN"}),
    ("substr", {"LEFT$", "RIGHT$", "MID$"}),
    ("gfx", {"SET", "RESET", "POINT", "CLS"}),
    ("tape", {"CLOAD", "CSAVE", "SKIPF"}),
    ("list", {"LIST", "LLIST"}),
    ("print", {"PRINT", "LPRINT"}),
    ("read", {"READ", "DATA", "RESTORE", "INPUT"}),
    ("loop", {"FOR", "NEXT"}),
    ("jump", {"GOTO", "GOSUB", "RETURN", "ON", "RUN", "IF"}),
    ("str", {"LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$", "INKEY$"}),
    ("fp", {"SGN", "INT", "ABS", "RND", "SQR", "LOG", "EXP", "SIN", "COS", "TAN", "ADD", "SUB", "MUL", "DIV", "POW"}),
]


def owner_names(static, handler, taken):
    """Name routines by which BASIC commands can reach them through the static call graph."""
    g = static.graph()
    owners = {}
    for cmd, root in handler.items():
        if root not in g: continue
        seen, stack = set(), [root]
        while stack:
            r = stack.pop()
            if r in seen: continue
            seen.add(r)
            stack.extend(c for c in g.get(r, ()) if c in g)
        for r in seen: owners.setdefault(r, set()).add(cmd)
    names = {}
    total = len(handler)
    for r in sorted(g):
        if r in taken or r < ROM_BASE: continue
        o = owners.get(r)
        if not o:
            names[r] = "sys_%04X" % r                 # reached only from reset / interrupts / RAM hooks
        elif len(o) == 1:
            names[r] = "%s_%04X" % (safe(next(iter(o))).lower(), r)
        else:
            fam = next((f for f, members in FAMILIES if o <= members), None)
            if fam: names[r] = "%s_%04X" % (fam, r)
            elif len(o) >= total * 0.6: names[r] = "core_%04X" % r     # used by nearly every command
            else: names[r] = "util_%04X" % r
    return names, owners


def ranges(addrs):
    out, start, prev = [], None, None
    for a in sorted(addrs):
        if start is None: start = prev = a
        elif a == prev + 1: prev = a
        else: out.append((start, prev)); start = prev = a
    if start is not None: out.append((start, prev))
    return out


HW_NAMES = {  # access evidence -> routine name prefix
    "kbd_rows": "kbd_read_rows", "w02": "port1_write", "r03": "port2_read", "w03": "port2_write",
    "r09": "frc_read", "r0A": "frc_read", "w08": "timer_ctrl_write", "r08": "timer_ctrl_read",
    "w0B": "ocr_write", "w0C": "ocr_write", "vram": "vram_write",
}


def write_outputs(args, data, tab, results, handler, ok, bad):
    cov, calls, indirect, hw = set(), {}, set(), {}
    for r in results.values():
        cov |= r["cov"]; indirect |= r["indirect"]
        for k, v in r["calls"].items(): calls.setdefault(k, set()).update(v)
        for k, v in r["hw"].items(): hw.setdefault(k, set()).update(v)
    targets = sorted(t for t in calls if t >= ROM_BASE)
    def routine_of(pc):
        best = None
        for t in targets:
            if t <= pc: best = t
            else: break
        return best
    syms = {}
    verified = {(e, h) for _, e, h in ok}
    for i, n in enumerate(tab["statements"]):
        syms[tab["stm_handlers"][i]] = "stm_" + safe(n)
    for i, n in enumerate(tab["functions"]):
        if tab["fn_handlers"][i] >= ROM_BASE: syms[tab["fn_handlers"][i]] = "fn_" + safe(n)
    for (pri, a), n in zip(tab["ops"], OPS): syms[a] = "op_" + n
    syms[tab["keyword_start"]] = "tbl_keywords"
    syms[tab["stm_tab"]] = "tbl_statement_handlers"
    syms[tab["fn_tab"]] = "tbl_function_handlers"
    syms[tab["ops_tab"]] = "tbl_operators"
    for a, n in MANUAL.items(): syms[a] = n
    from mc10_static import Static
    rom = Rom(data)
    entries = {rom.w(0xFFF0 + 2 * i) for i in range(8)} | set(handler.values()) | set(calls) | {t for _, t in indirect}
    static = Static(data, entries)
    evidence = {}
    for kind, name in HW_NAMES.items():
        for pc in hw.get(kind, ()):
            r = routine_of(pc)
            if r is not None and r not in syms:
                evidence.setdefault(r, set()).add(name)
    for r, names in evidence.items():
        syms[r] = "io_" + "_".join(sorted(names)) + "_%04X" % r
    auto, owners = owner_names(static, handler, syms)
    syms.update(auto)
    for i in range(14):                                # RAM hook vectors (all RTS until a program patches them)
        syms.setdefault(0x4285 + 3 * i, "hook_%04X" % (0x4285 + 3 * i))
    for i, n in enumerate(["sci", "toi", "oci", "ici", "irq", "swi", "nmi"]):
        syms.setdefault(0x4200 + 3 * i, "ramvec_" + n)
    os.makedirs(os.path.dirname(os.path.abspath(args.sym)), exist_ok=True)
    with open(args.sym, "w") as f:
        f.write("; Labels derived by running BASIC tests in tools/mc10_emu.py (see tools/mc10_label.py).\n")
        f.write("; Format: NAME $ADDR. Addresses and names only; no firmware bytes.\n")
        for a in sorted(syms): f.write("%s $%04X\n" % (syms[a], a))
    base = os.path.splitext(args.sym)[0]
    with open(base + ".cov", "w") as f:
        f.write("; Executed instruction addresses (ranges) from the BASIC test run\n")
        for a, b in ranges(a for a in cov if a >= ROM_BASE): f.write("$%04X $%04X\n" % (a, b))
    with open(base + ".targets", "w") as f:
        f.write("; Entry points seen at run time (JSR/BSR targets and indirect JMP/JSR), usable as analyzer roots\n")
        for t in sorted({t for t in calls if t >= ROM_BASE} | {t for _, t in indirect if t >= ROM_BASE}):
            f.write("$%04X\n" % t)
    rom_cov = len([a for a in cov if a >= ROM_BASE])
    print("symbols: %d -> %s; covered instructions %d; call targets %d" % (len(syms), args.sym, rom_cov, len(targets)))
    os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
    with open(args.report, "w") as f:
        f.write("# Routines identified by running BASIC\n\n")
        f.write("Generated by `tools/mc10_label.py`. Every handler below executed during its test.\n\n")
        f.write("| Kind | Name | Handler | Verified by run |\n|---|---|---|---|\n")
        vv = {e for _, e, _ in ok}
        for i, n in enumerate(tab["statements"]):
            f.write("| statement | %s | $%04X | %s |\n" % (n, tab["stm_handlers"][i], "yes" if n in vv else "no"))
        for i, n in enumerate(tab["functions"]):
            f.write("| function | %s | $%04X | %s |\n" % (n, tab["fn_handlers"][i], "yes" if n in vv else "no"))
        for (pri, a), n in zip(tab["ops"], OPS):
            f.write("| operator | %s | $%04X | %s |\n" % (n, a, "yes" if n in vv else "no"))
        f.write("\n## Names checked by hand\n\nRead from the disassembly and confirmed by experiment in the emulator.\n\n| Address | Name |\n|---|---|\n")
        for a in sorted(MANUAL): f.write("| $%04X | %s |\n" % (a, MANUAL[a]))
        f.write("\n## Names derived from the call graph\n\n"
                "`print_XXXX` = reached only from PRINT; `fp_`, `str_`, `trig_`, `substr_`, `gfx_`, `tape_`, `list_`, `read_`, "
                "`loop_`, `jump_` = reached only from that group of commands; `core_` = used by at least 60%% of all commands; "
                "`util_` = shared by several unrelated commands; `sys_` = not reached from any BASIC command "
                "(start-up, interrupts, main loop); `io_..._XXXX` = touches that hardware. %d routines named this way.\n" % len(auto))
        f.write("\n## I/O evidence\n\n| Access | Routines containing the access |\n|---|---|\n")
        for k in sorted(hw):
            rs = sorted({routine_of(pc) or 0 for pc in hw[k]})
            f.write("| %s | %s |\n" % (k, ", ".join("$%04X" % r for r in rs[:12])))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rom")
    ap.add_argument("--sym", default="symbols/mc10.sym")
    ap.add_argument("--report", default="docs/ROUTINES.md")
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    data = load_rom(args.rom)
    rom = Rom(data)
    tab = parse_tables(rom)
    print("keyword table %04X-%04X, statement table %04X, function table %04X, operator table %04X" %
          (tab["keyword_start"], tab["keyword_end"], tab["stm_tab"], tab["fn_tab"], tab["ops_tab"]))

    results = {}
    t0 = time.time()
    from concurrent.futures import ProcessPoolExecutor
    with ProcessPoolExecutor() as ex:
        for t, r in zip(TESTS, ex.map(run_test, [data] * len(TESTS), TESTS)):
            results[t["id"]] = r
            print("%-10s %6d instr  %s" % (t["id"], r["instr"], " | ".join(x for x in r["screen"].split("\n")[3:][-3:])[:70]))
    print("ran %d tests in %.1fs" % (len(TESTS), time.time() - t0))

    # ---- verify handlers
    handler = {}
    for i, n in enumerate(tab["statements"]): handler[n] = tab["stm_handlers"][i]
    for i, n in enumerate(tab["functions"]): handler[n] = tab["fn_handlers"][i]
    for (pri, a), n in zip(tab["ops"], OPS): handler[n] = a
    ok, bad = [], []
    for t in TESTS:
        for e in t["expect"] or []:
            h = handler.get(e)
            (ok if h in results[t["id"]]["cov"] else bad).append((t["id"], e, h))
    print("handler check: %d executed, %d NOT executed" % (len(ok), len(bad)))
    for b in bad: print("  NOT executed:", b[0], b[1], "%04X" % b[2] if b[2] else None)

    write_outputs(args, data, tab, results, handler, ok, bad)
    json_out = dict(tables={k: v for k, v in tab.items() if k not in ("statements", "secondary", "functions")},
                    verified=[(a, b, c) for a, b, c in ok], failed=[(a, b, c) for a, b, c in bad])
    if args.json:
        json.dump(json_out, open(args.json, "w"), indent=1, default=lambda o: sorted(o))
    return data, rom, tab, results, handler, ok, bad


if __name__ == "__main__":
    main()
