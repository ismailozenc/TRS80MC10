#!/usr/bin/env python3
"""Static helpers for the labelling tool: recursive-descent decode of the ROM and its call graph."""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mc10_emu import TABLE, LEN

ROM_BASE = 0xE000


class Static:
    def __init__(self, rom, entries):
        self.rom = rom
        self.code = {}            # addr -> (name, mode, length)
        self.calls = {}           # callee -> set(call sites)
        self.jumps = {}           # jump/branch target -> set(sites)
        self.entries = set()
        self.ramrefs = {}         # ram addr -> set(sites)
        work = [e for e in entries if self.inrom(e)]
        self.entries.update(work)
        while work:
            self.flow(work.pop(), work)

    def inrom(self, a): return ROM_BASE <= a <= 0xFFFF
    def b(self, a): return self.rom[a - ROM_BASE]
    def w(self, a): return (self.b(a) << 8) | self.b(a + 1)

    def flow(self, a, work):
        while self.inrom(a) and a not in self.code:
            op = self.b(a)
            name, mode, cyc, sub, *rest = TABLE[op]
            if sub == "ill": return
            n = LEN[mode]
            if a + n > 0x10000: return
            self.code[a] = (name, mode, n)
            if mode == "rel":
                off = self.b(a + 1); off = off - 256 if off > 127 else off
                t = (a + 2 + off) & 0xFFFF
                if name == "BSR":
                    self.calls.setdefault(t, set()).add(a); self.entries.add(t)
                else:
                    self.jumps.setdefault(t, set()).add(a)
                if self.inrom(t) and t not in self.code: work.append(t)
                if name == "BRA": return
            elif mode == "ext":
                t = self.w(a + 1)
                if name == "JSR":
                    self.calls.setdefault(t, set()).add(a)
                    if self.inrom(t):
                        self.entries.add(t)
                        if t not in self.code: work.append(t)
                elif name == "JMP":
                    self.jumps.setdefault(t, set()).add(a)
                    if self.inrom(t) and t not in self.code: work.append(t)
                    return
                elif not self.inrom(t):
                    self.ramrefs.setdefault(t, set()).add(a)
            elif mode == "dir":
                self.ramrefs.setdefault(self.b(a + 1), set()).add(a)
            elif mode == "imm16":
                pass
            if name in ("RTS", "RTI") or (name == "JMP"): return
            a += n

    def routine_of(self, addr, starts):
        """Start of the routine containing addr (greatest start <= addr)."""
        import bisect
        i = bisect.bisect_right(starts, addr) - 1
        return starts[i] if i >= 0 else None

    def graph(self):
        """routine start -> set(callee routine starts), following JSR/BSR and tail JMP/branches out of the routine."""
        starts = sorted(self.entries)
        g = {s: set() for s in starts}
        for callee, sites in self.calls.items():
            for site in sites:
                r = self.routine_of(site, starts)
                if r is not None: g[r].add(callee)
        for target, sites in self.jumps.items():
            if target not in self.entries: continue
            for site in sites:
                r = self.routine_of(site, starts)
                if r is not None and r != target: g[r].add(target)
        return g

    def disasm(self, a, names=None):
        names = names or {}
        name, mode, n = self.code.get(a) or (TABLE[self.b(a)][0], TABLE[self.b(a)][1], LEN[TABLE[self.b(a)][1]])
        def nm(x, w=4): return names.get(x) or ("$%0*X" % (w, x))
        if mode == "inh": o = ""
        elif mode == "rel":
            off = self.b(a + 1); off = off - 256 if off > 127 else off
            o = nm((a + 2 + off) & 0xFFFF)
        elif mode == "imm": o = "#$%02X" % self.b(a + 1)
        elif mode == "imm16": o = "#" + nm(self.w(a + 1))
        elif mode == "dir": o = nm(self.b(a + 1), 2)
        elif mode == "idx": o = "$%02X,X" % self.b(a + 1)
        else: o = nm(self.w(a + 1))
        return "%04X  %-5s %s" % (a, name, o), n
