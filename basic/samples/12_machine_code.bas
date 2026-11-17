;; title: 12 Machine code - POKE a 6803 routine and EXEC it
;; summary: CLEAR reserves memory above 20299, the loop POKEs a 14-byte routine at 20300 ($4F4C) and EXEC calls it. The routine fills the second half of the screen with "*" and returns.
;; look: In the debugger add a breakpoint at 4F4C (Breakpoints tab, Add), RUN, then use the "Live (from PC)" tab: it disassembles your own code in RAM.
;; look: The routine is LDX #$4100 / LDAA #$6A / STAA 0,X / INX / CPX #$4200 / BNE loop / RTS.
;; break: stm_EXEC
10 CLEAR 100,20299
20 CLS
30 FOR I=0 TO 13:READ B:POKE 20300+I,B:NEXT
40 EXEC 20300
50 PRINT "BACK IN BASIC"
60 DATA 206,65,0,134,106,167,0,8,140,66,0,38
70 DATA 248,57
