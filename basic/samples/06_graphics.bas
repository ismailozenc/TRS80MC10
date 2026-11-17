;; title: 06 Graphics - CLS colours, SET, RESET and POINT
;; summary: Draws a frame and a diagonal with SET on the 64 x 32 block grid, reads a block back with POINT, erases with RESET. It then waits: press BREAK (Esc) to stop.
;; look: Memory tab, "Screen": each screen byte at $4000 holds four blocks plus a colour; SET changes single bits.
;; break: stm_SET, stm_RESET, fn_POINT
10 CLS 0
20 FOR X=0 TO 63:SET(X,0,4):SET(X,29,4):NEXT
30 FOR Y=0 TO 29:SET(0,Y,2):SET(63,Y,2):NEXT
40 FOR I=2 TO 27:SET(I*2,I,5):NEXT
50 P=POINT(10,5)
60 RESET(10,5)
70 PRINT @ 448,"POINT(10,5) WAS";P;
80 PRINT @ 480,"PRESS BREAK";
90 GOTO 90
