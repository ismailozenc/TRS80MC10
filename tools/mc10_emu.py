#!/usr/bin/env python3
"""Headless MC-10 emulator used to run the firmware, type BASIC into it and record what executes.

Mirrors the C# core (MC10.Core): MC6803 CPU, internal timer/ports, RAM at $0040-$00FF and
$4000-$4FFF, ROM at $E000-$FFFF, keyboard matrix scanned through port 1 / $BFFF / port 2 bit 1.
Pure Python 3, no dependencies. Slow (about 0.5 M instructions per second) but good enough for tests.
"""
import sys

C, V, Z, N, I, H = 1, 2, 4, 8, 0x10, 0x20
CYCLES_PER_FRAME = 14914          # 894886 Hz / 60


# ---------------------------------------------------------------- opcode table (same as Opcodes.cs)
def build_table():
    T = [None] * 256

    def put(op, name, mode, cyc, sub=None, regb=False):
        assert T[op] is None, hex(op)
        T[op] = (name, mode, cyc, sub, regb)
    inh = [(0x01, "NOP", 2), (0x04, "LSRD", 3), (0x05, "ASLD", 3), (0x06, "TAP", 2), (0x07, "TPA", 2),
           (0x08, "INX", 3), (0x09, "DEX", 3), (0x0A, "CLV", 2), (0x0B, "SEV", 2), (0x0C, "CLC", 2),
           (0x0D, "SEC", 2), (0x0E, "CLI", 2), (0x0F, "SEI", 2), (0x10, "SBA", 2), (0x11, "CBA", 2),
           (0x16, "TAB", 2), (0x17, "TBA", 2), (0x19, "DAA", 2), (0x1B, "ABA", 2), (0x30, "TSX", 3),
           (0x31, "INS", 3), (0x32, "PULA", 4), (0x33, "PULB", 4), (0x34, "DES", 3), (0x35, "TXS", 3),
           (0x36, "PSHA", 3), (0x37, "PSHB", 3), (0x38, "PULX", 5), (0x39, "RTS", 5), (0x3A, "ABX", 3),
           (0x3B, "RTI", 10), (0x3C, "PSHX", 4), (0x3D, "MUL", 10), (0x3E, "WAI", 9), (0x3F, "SWI", 12),
           (0x8F, "XGDX", 3)]
    for op, n, c in inh:
        put(op, n, "inh", c, "inh")
    un = [("NEG", 0x40), ("COM", 0x43), ("LSR", 0x44), ("ROR", 0x46), ("ASR", 0x47), ("ASL", 0x48),
          ("ROL", 0x49), ("DEC", 0x4A), ("INC", 0x4C), ("TST", 0x4D), ("CLR", 0x4F)]
    for n, b in un:
        put(b, n + "A", "inh", 2, "ureg", False)
        put(b + 0x10, n + "B", "inh", 2, "ureg", True)
    unm = [("NEG", 0), ("COM", 3), ("LSR", 4), ("ROR", 6), ("ASR", 7), ("ASL", 8), ("ROL", 9),
           ("DEC", 10), ("INC", 12), ("TST", 13), ("CLR", 15)]
    for n, o in unm:
        put(0x60 + o, n, "idx", 7, "umem")
        put(0x70 + o, n, "ext", 6, "umem")
    put(0x6E, "JMP", "idx", 4, "jmp")
    put(0x7E, "JMP", "ext", 3, "jmp")
    br = "BRA BRN BHI BLS BCC BCS BNE BEQ BVC BVS BPL BMI BGE BLT BGT BLE".split()
    for i, n in enumerate(br):
        put(0x20 + i, n, "rel", 4, "br", i)
    put(0x8D, "BSR", "rel", 8, "bsr")
    alu = [("SUB", 0x80), ("CMP", 0x81), ("SBC", 0x82), ("AND", 0x84), ("BIT", 0x85), ("LD", 0x86),
           ("EOR", 0x88), ("ADC", 0x89), ("OR", 0x8A), ("ADD", 0x8B)]
    for n, b in alu:
        for off, mode, cyc in ((0, "imm", 2), (0x10, "dir", 3), (0x20, "idx", 5), (0x30, "ext", 4)):
            put(b + off, n + "A", mode, cyc, "alu", False)
            put(b + 0x40 + off, n + "B", mode, cyc, "alu", True)
    for b, mode, cyc in ((0x97, "dir", 4), (0xA7, "idx", 6), (0xB7, "ext", 5)):
        put(b, "STAA", mode, cyc, "st8", False)
        put(b + 0x40, "STAB", mode, cyc, "st8", True)
    a16 = [("SUBD", 0x83, 4, 6, 7, 7), ("ADDD", 0xC3, 4, 6, 7, 7), ("CPX", 0x8C, 3, 4, 6, 5),
           ("LDS", 0x8E, 3, 4, 6, 5), ("LDD", 0xCC, 3, 4, 6, 5), ("LDX", 0xCE, 3, 4, 6, 5)]
    for n, b, ci, cd, cx, ce in a16:
        put(b, n, "imm16", ci, "a16")
        put(b + 0x10, n, "dir", cd, "a16")
        put(b + 0x20, n, "idx", cx, "a16")
        put(b + 0x30, n, "ext", ce, "a16")
    for n, d_, x_, e_ in (("STS", 0x9F, 0xAF, 0xBF), ("STD", 0xDD, 0xED, 0xFD), ("STX", 0xDF, 0xEF, 0xFF)):
        put(d_, n, "dir", 5, "st16")
        put(x_, n, "idx", 7, "st16")
        put(e_, n, "ext", 6, "st16")
    put(0x9D, "JSR", "dir", 5, "jsr")
    put(0xAD, "JSR", "idx", 5, "jsr")
    put(0xBD, "JSR", "ext", 6, "jsr")
    for op in range(256):
        if T[op] is None:
            T[op] = ("ILLEGAL", "inh", 2, "ill")
    return T


TABLE = build_table()
LEN = {"inh": 1, "rel": 2, "imm": 2, "imm16": 3, "dir": 2, "idx": 2, "ext": 3}


def nz8(r):
    return (N if r & 0x80 else 0) | (Z if (r & 0xFF) == 0 else 0)


def nz16(r):
    return (N if r & 0x8000 else 0) | (Z if (r & 0xFFFF) == 0 else 0)


class Keyboard:
    """Same matrix and tap behaviour as MatrixKeyboard.cs."""
    HOLD, GAP = 4, 3

    def __init__(self, strobe):
        self.strobe = strobe
        self.cols = [0xFF] * 8
        self.shift = self.ctrl = self.brk = False
        self.queue = []
        self.frames = 0
        self.holding = False

    def read_rows(self):
        s = self.strobe()
        r = 0xFF
        for c in range(8):
            if not (s >> c) & 1:
                r &= self.cols[c]
        return r

    def read_port2(self):
        s = self.strobe()
        low = (not (s >> 7) & 1 and self.shift) or (not s & 1 and self.ctrl) or (not (s >> 2) & 1 and self.brk)
        return 0xFD if low else 0xFF

    def tap_index(self, idx, shift=False, ctrl=False):
        self.queue.append((idx, shift, ctrl, False))

    def tap_break(self):
        self.queue.append((-1, False, False, True))

    def tap_char(self, ch):
        o = ord(ch)
        if ch == '\r': self.tap_index(30)
        elif ch == ' ': self.tap_index(31)
        elif ch == '\b': self.tap_index(1, False, True)
        elif ch == '^': self.tap_index(23, False, True)      # CONTROL+W
        elif 'A' <= ch <= 'Z': self.tap_index(o - 64)
        elif 'a' <= ch <= 'z': self.tap_index(o - 96)
        elif ch == '@': self.tap_index(0)
        elif '0' <= ch <= '7': self.tap_index(32 + o - 48)
        elif ch in '89': self.tap_index(40 + o - 56)
        elif ch in "!\"#$%&'()*+<=>?": self.tap_index(33 + "!\"#$%&'()*+<=>?".index(ch), True)
        elif ch in ":;,-./": self.tap_index(42 + ":;,-./".index(ch))
        else: raise ValueError("no key for %r" % ch)

    def advance(self):
        if self.frames > 0:
            self.frames -= 1
        if self.frames > 0:
            return
        if self.holding:
            self.cols = [0xFF] * 8
            self.shift = self.ctrl = self.brk = False
            self.holding = False
            self.frames = self.GAP
            return
        if not self.queue:
            return
        idx, sh, ct, br = self.queue.pop(0)
        self.shift, self.ctrl, self.brk = sh, ct, br
        if idx >= 0:
            self.cols[idx & 7] &= ~(1 << (idx >> 3)) & 0xFF
        self.holding = True
        self.frames = self.HOLD

    @property
    def busy(self):
        return bool(self.queue) or self.holding


class MC10:
    def __init__(self, rom, rom_base=0xE000):
        self.mem = bytearray(65536)
        self.writable = bytearray(65536)
        for a in list(range(0x40, 0x100)) + list(range(0x4000, 0x5000)):
            self.writable[a] = 1
        self.rom_base = rom_base
        self.mem[rom_base:rom_base + len(rom)] = rom
        self.io = bytearray(32)
        self.kbd = Keyboard(lambda: self.io[2])
        self.a = self.b = 0
        self.x = self.sp = self.pc = 0
        self.cc = I
        self.halted = False
        self.cycles = 0
        self.instructions = 0
        self.frc = 0
        self.frc_div = 0
        self.ocr = 0xFFFF
        self.icr = 0
        self.nmi = False
        self.irq = False
        self.illegal = 0
        # profiling
        self.cov = set()              # addresses of executed instructions
        self.calls = {}               # target -> set(callers)
        self.indirect = set()         # (from, target) for JMP/JSR n,X
        self.record = True
        self.hw = {}                  # kind -> set(pc of the accessing instruction)
        self.cur = 0
        self.pc = self.rd16(0xFFFE)
        self.next_frame = CYCLES_PER_FRAME

    # ---- bus
    def rd(self, a):
        a &= 0xFFFF
        if a < 0x20:
            if self.record: self.hw.setdefault("r%02X" % a, set()).add(self.cur)
            return self.io_read(a)
        if a == 0xBFFF:
            if self.record: self.hw.setdefault("kbd_rows", set()).add(self.cur)
            return self.kbd.read_rows() | 0xC0
        if a >= self.rom_base or self.writable[a]:
            return self.mem[a]
        return 0xFF

    def wr(self, a, v):
        a &= 0xFFFF
        if a < 0x20:
            if self.record: self.hw.setdefault("w%02X" % a, set()).add(self.cur)
            self.io_write(a, v & 0xFF)
        elif self.writable[a]:
            if 0x4000 <= a < 0x4200 and self.record: self.hw.setdefault("vram", set()).add(self.cur)
            self.mem[a] = v & 0xFF
        elif a == 0xBFFF and self.record:
            self.hw.setdefault("vdg_latch_w", set()).add(self.cur)

    def rd16(self, a):
        return (self.rd(a) << 8) | self.rd(a + 1)

    def wr16(self, a, v):
        self.wr(a, v >> 8)
        self.wr(a + 1, v)

    def io_read(self, r):
        if r in (2, 3):
            ddr = self.io[r - 2]
            ext = self.kbd.read_port2() if r == 3 else 0xFF
            return (self.io[r] & ddr) | (ext & ~ddr & 0xFF)
        if r == 9: return self.frc >> 8
        if r == 0x0A:
            self.io[8] &= ~0x20 & 0xFF
            return self.frc & 0xFF
        if r == 0x0B: return self.ocr >> 8
        if r == 0x0C:
            self.io[8] &= ~0x40 & 0xFF
            return self.ocr & 0xFF
        if r == 0x0D: return self.icr >> 8
        if r == 0x0E:
            self.io[8] &= ~0x80 & 0xFF
            return self.icr & 0xFF
        if r == 0x11: return 0x20 | self.io[0x11]
        return self.io[r]

    def io_write(self, r, v):
        if r == 8: self.io[8] = (self.io[8] & 0xE0) | (v & 0x1F)
        elif r in (9, 0x0A, 0x0D, 0x0E, 0x12): pass
        elif r == 0x0B: self.ocr = (v << 8) | (self.ocr & 0xFF)
        elif r == 0x0C: self.ocr = (self.ocr & 0xFF00) | v
        else: self.io[r] = v

    # ---- stack helpers
    def f8(self):
        v = self.rd(self.pc)
        self.pc = (self.pc + 1) & 0xFFFF
        return v

    def f16(self):
        h = self.f8()
        return (h << 8) | self.f8()

    def push(self, v):
        self.wr(self.sp, v)
        self.sp = (self.sp - 1) & 0xFFFF

    def pull(self):
        self.sp = (self.sp + 1) & 0xFFFF
        return self.rd(self.sp)

    def push16(self, v):
        self.push(v & 0xFF)
        self.push(v >> 8)

    def pull16(self):
        h = self.pull()
        return (h << 8) | self.pull()

    def stack_all(self):
        self.push(self.pc & 0xFF); self.push(self.pc >> 8)
        self.push(self.x & 0xFF); self.push(self.x >> 8)
        self.push(self.a); self.push(self.b); self.push(self.cc | 0xC0)

    def unstack_all(self):
        self.cc = self.pull() & 0x3F
        self.b = self.pull(); self.a = self.pull()
        h = self.pull(); self.x = (h << 8) | self.pull()
        self.pc = self.pull16()

    # ---- timer and interrupts
    def tick(self, n):
        self.frc_div += n
        while self.frc_div >= 4:
            self.frc_div -= 4
            self.frc = (self.frc + 1) & 0xFFFF
            if self.frc == 0: self.io[8] |= 0x20
            if self.frc == self.ocr: self.io[8] |= 0x40

    def pending(self):
        if self.cc & I: return None
        if self.irq: return 0xFFF8
        t = self.io[8]
        if t & 0x80 and t & 0x10: return 0xFFF6
        if t & 0x40 and t & 0x08: return 0xFFF4
        if t & 0x20 and t & 0x04: return 0xFFF2
        return None

    # ---- execution
    def step(self):
        if self.nmi:
            self.nmi = False
            if not self.halted: self.stack_all()
            self.cc |= I; self.halted = False; self.pc = self.rd16(0xFFFC); cyc = 12
        else:
            vec = self.pending()
            if vec is not None:
                if not self.halted: self.stack_all()
                self.cc |= I; self.halted = False; self.pc = self.rd16(vec); cyc = 12
            elif self.halted:
                cyc = 4
            else:
                start = self.pc
                self.cur = start
                op = self.f8()
                name, mode, cyc, sub, *rest = TABLE[op]
                if self.record: self.cov.add(start)
                self.instructions += 1
                self.exec(start, op, name, mode, sub, rest)
        self.cycles += cyc
        self.tick(cyc)
        if self.cycles >= self.next_frame:
            self.next_frame += CYCLES_PER_FRAME
            self.kbd.advance()
        return cyc

    def ea(self, mode):
        if mode == "dir": return self.f8()
        if mode == "idx": return (self.x + self.f8()) & 0xFFFF
        return self.f16()

    def setcc(self, mask, bits):
        self.cc = (self.cc & ~mask) | (bits & mask)

    def exec(self, start, op, name, mode, sub, rest):
        if sub == "alu":
            regb = rest[0]
            m = self.f8() if mode == "imm" else self.rd(self.ea(mode))
            a = self.b if regb else self.a
            cin = 1 if self.cc & C else 0
            n = name[:-1]
            store = True
            mask = N | Z | V
            if n == "LD": r = m; bits = nz8(m)
            elif n in ("ADD", "ADC"):
                ci = cin if n == "ADC" else 0
                s = a + m + ci
                bits = (H if (a & 15) + (m & 15) + ci > 15 else 0) | (C if s > 255 else 0) | \
                       (V if (a ^ s) & (m ^ s) & 0x80 else 0) | nz8(s)
                r = s & 0xFF; mask = H | C | V | N | Z
            elif n in ("SUB", "SBC", "CMP"):
                ci = cin if n == "SBC" else 0
                s = a - m - ci
                bits = (C if s < 0 else 0) | (V if (a ^ m) & (a ^ s) & 0x80 else 0) | nz8(s)
                r = s & 0xFF; mask = C | V | N | Z; store = n != "CMP"
            elif n == "AND": r = a & m; bits = nz8(r)
            elif n == "BIT": r = a & m; bits = nz8(r); store = False
            elif n == "EOR": r = a ^ m; bits = nz8(r)
            elif n == "OR": r = a | m; bits = nz8(r)
            else: raise AssertionError(name)
            if store:
                if regb: self.b = r
                else: self.a = r
            self.setcc(mask, bits)
        elif sub == "st8":
            ea = self.ea(mode)
            v = self.b if rest[0] else self.a
            self.wr(ea, v)
            self.setcc(N | Z | V, nz8(v))
        elif sub == "a16":
            m = self.f16() if mode == "imm16" else self.rd16(self.ea(mode))
            if name == "LDD": self.a, self.b = m >> 8, m & 0xFF; self.setcc(N | Z | V, nz16(m))
            elif name == "LDX": self.x = m; self.setcc(N | Z | V, nz16(m))
            elif name == "LDS": self.sp = m; self.setcc(N | Z | V, nz16(m))
            else:
                d = (self.a << 8) | self.b
                if name == "ADDD":
                    s = d + m
                    bits = (C if s > 0xFFFF else 0) | (V if (d ^ s) & (m ^ s) & 0x8000 else 0) | nz16(s)
                    self.a, self.b = (s >> 8) & 0xFF, s & 0xFF
                else:
                    ref = self.x if name == "CPX" else d
                    s = ref - m
                    bits = (C if s < 0 else 0) | (V if (ref ^ m) & (ref ^ s) & 0x8000 else 0) | nz16(s)
                    if name == "SUBD": self.a, self.b = (s >> 8) & 0xFF, s & 0xFF
                self.setcc(C | V | N | Z, bits)
        elif sub == "st16":
            ea = self.ea(mode)
            v = {"STD": (self.a << 8) | self.b, "STX": self.x, "STS": self.sp}[name]
            self.wr16(ea, v)
            self.setcc(N | Z | V, nz16(v))
        elif sub == "jsr":
            ea = self.ea(mode)
            self.push16(self.pc)
            if self.record:
                self.calls.setdefault(ea, set()).add(start)
                if mode != "ext": self.indirect.add((start, ea))
            self.pc = ea
        elif sub == "jmp":
            ea = self.ea(mode)
            if self.record and mode != "ext": self.indirect.add((start, ea))
            self.pc = ea
        elif sub == "bsr":
            off = self.f8(); off = off - 256 if off > 127 else off
            self.push16(self.pc)
            self.pc = (self.pc + off) & 0xFFFF
            if self.record: self.calls.setdefault(self.pc, set()).add(start)
        elif sub == "br":
            off = self.f8(); off = off - 256 if off > 127 else off
            cc = self.cc
            n, v, z, c = bool(cc & N), bool(cc & V), bool(cc & Z), bool(cc & C)
            k = rest[0]
            take = (True, False, not (c or z), c or z, not c, c, not z, z, not v, v, not n, n,
                    n == v, n != v, not (z or n != v), z or n != v)[k]
            if take: self.pc = (self.pc + off) & 0xFFFF
        elif sub == "ureg":
            regb = rest[0]
            v = self.b if regb else self.a
            r, bits, store = self.unary(name[:-1], v)
            if store:
                if regb: self.b = r
                else: self.a = r
            self.setcc(C | V | N | Z, bits)
        elif sub == "umem":
            ea = self.ea(mode)
            r, bits, store = self.unary(name, self.rd(ea))
            if store: self.wr(ea, r)
            self.setcc(C | V | N | Z, bits)
        elif sub == "inh":
            self.inherent(name)
        else:
            self.illegal += 1

    def unary(self, n, v):
        cin = 1 if self.cc & C else 0
        if n == "NEG":
            r = (-v) & 0xFF; return r, (C if v else 0) | (V if v == 0x80 else 0) | nz8(r), True
        if n == "COM":
            r = (~v) & 0xFF; return r, C | nz8(r), True
        if n == "CLR": return 0, Z, True
        if n == "TST": return v, nz8(v), False
        if n == "INC":
            r = (v + 1) & 0xFF; return r, (V if v == 0x7F else 0) | nz8(r) | (C if cin else 0), True
        if n == "DEC":
            r = (v - 1) & 0xFF; return r, (V if v == 0x80 else 0) | nz8(r) | (C if cin else 0), True
        if n == "LSR":
            co = v & 1; r = v >> 1; return r, ((C | V) if co else 0) | nz8(r), True
        if n == "ASR": co = v & 1; r = (v >> 1) | (v & 0x80)
        elif n == "ASL": co = v >> 7; r = (v << 1) & 0xFF
        elif n == "ROL": co = v >> 7; r = ((v << 1) | cin) & 0xFF
        elif n == "ROR": co = v & 1; r = (v >> 1) | (cin << 7)
        else: raise AssertionError(n)
        nf = r >> 7
        return r, (C if co else 0) | nz8(r) | (V if nf != co else 0), True

    def inherent(self, n):
        a, b = self.a, self.b
        if n == "NOP": pass
        elif n == "TAP": self.cc = a & 0x3F
        elif n == "TPA": self.a = self.cc | 0xC0
        elif n == "INX": self.x = (self.x + 1) & 0xFFFF; self.setcc(Z, Z if self.x == 0 else 0)
        elif n == "DEX": self.x = (self.x - 1) & 0xFFFF; self.setcc(Z, Z if self.x == 0 else 0)
        elif n == "CLV": self.setcc(V, 0)
        elif n == "SEV": self.setcc(V, V)
        elif n == "CLC": self.setcc(C, 0)
        elif n == "SEC": self.setcc(C, C)
        elif n == "CLI": self.setcc(I, 0)
        elif n == "SEI": self.setcc(I, I)
        elif n in ("SBA", "CBA"):
            s = a - b
            bits = (C if s < 0 else 0) | (V if (a ^ b) & (a ^ s) & 0x80 else 0) | nz8(s)
            if n == "SBA": self.a = s & 0xFF
            self.setcc(C | V | N | Z, bits)
        elif n == "TAB": self.b = a; self.setcc(N | Z | V, nz8(a))
        elif n == "TBA": self.a = b; self.setcc(N | Z | V, nz8(b))
        elif n == "ABA":
            s = a + b
            bits = (H if (a & 15) + (b & 15) > 15 else 0) | (C if s > 255 else 0) | \
                   (V if (a ^ s) & (b ^ s) & 0x80 else 0) | nz8(s)
            self.a = s & 0xFF
            self.setcc(H | C | V | N | Z, bits)
        elif n == "DAA":
            corr = 0; cout = bool(self.cc & C)
            if self.cc & H or (a & 15) > 9: corr |= 6
            if cout or a > 0x99: corr |= 0x60; cout = True
            self.a = (a + corr) & 0xFF
            self.setcc(C | N | Z, (C if cout else 0) | nz8(self.a))
        elif n == "TSX": self.x = (self.sp + 1) & 0xFFFF
        elif n == "TXS": self.sp = (self.x - 1) & 0xFFFF
        elif n == "INS": self.sp = (self.sp + 1) & 0xFFFF
        elif n == "DES": self.sp = (self.sp - 1) & 0xFFFF
        elif n == "PULA": self.a = self.pull()
        elif n == "PULB": self.b = self.pull()
        elif n == "PSHA": self.push(a)
        elif n == "PSHB": self.push(b)
        elif n == "PULX": h = self.pull(); self.x = (h << 8) | self.pull()
        elif n == "PSHX": self.push16(self.x)
        elif n == "RTS": self.pc = self.pull16()
        elif n == "ABX": self.x = (self.x + b) & 0xFFFF
        elif n == "RTI": self.unstack_all()
        elif n == "MUL":
            r = a * b; self.a, self.b = (r >> 8) & 0xFF, r & 0xFF
            self.setcc(C | Z, (C if (r >> 7) & 1 else 0) | (Z if r & 0xFFFF == 0 else 0))
        elif n == "WAI": self.stack_all(); self.halted = True
        elif n == "SWI":
            self.stack_all(); self.cc |= I; self.pc = self.rd16(0xFFFA)
        elif n == "XGDX":
            d = (a << 8) | b; self.a, self.b = self.x >> 8, self.x & 0xFF; self.x = d
        elif n == "LSRD":
            d = (a << 8) | b; co = d & 1; r = d >> 1
            self.a, self.b = r >> 8, r & 0xFF
            self.setcc(C | V | N | Z, ((C | V) if co else 0) | nz16(r))
        elif n == "ASLD":
            d = (a << 8) | b; co = d >> 15; r = (d << 1) & 0xFFFF
            self.a, self.b = r >> 8, r & 0xFF
            nf = r >> 15
            self.setcc(C | V | N | Z, (C if co else 0) | nz16(r) | (V if nf != co else 0))
        else:
            raise AssertionError(n)

    def run(self, cycles):
        end = self.cycles + cycles
        while self.cycles < end:
            self.step()

    # ---- screen
    @staticmethod
    def glyph(code):
        """Same mapping as Video.Glyph: returns (char, inverse)."""
        if code >= 0x80: return None, False
        if code < 0x20: return chr(code + 0x40), True
        if code < 0x40: return chr(code), True
        if code < 0x60: return chr(code), False
        return chr(code - 0x40), False

    def screen_rows(self):
        rows = []
        for r in range(16):
            s = ""
            for c in range(32):
                code = self.mem[0x4000 + r * 32 + c]
                ch, inv = self.glyph(code)
                if ch is None: s += "#"
                elif inv: s += ch.lower() if ch.isalpha() else ch   # reverse video letters = lowercase
                else: s += ch
            rows.append(s.rstrip())
        return rows

    def screen_text(self):
        rows = self.screen_rows()
        while rows and not rows[-1]: rows.pop()
        return "\n".join(rows)

    # ---- typing
    def type_text(self, text, max_frames=4000):
        for ch in text:
            self.kbd.tap_char(ch)
        frames = 0
        while self.kbd.busy and frames < max_frames:
            self.run(CYCLES_PER_FRAME)
            frames += 1

    def run_frames(self, n):
        self.run(CYCLES_PER_FRAME * n)


def load_rom(path):
    d = open(path, "rb").read()
    if len(d) != 0x2000:
        raise SystemExit("expected an 8 KB ROM image, got %d bytes" % len(d))
    return d


if __name__ == "__main__":
    m = MC10(load_rom(sys.argv[1]))
    m.run_frames(120)
    print(m.screen_text())
