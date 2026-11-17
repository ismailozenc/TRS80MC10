# Sample BASIC programs

In the emulator: **Programs > Sample programs (F4)**. Each screen below is what the headless emulator (`tools/run_samples.py`) showed after RUN.

## 01_hello.bas

- title: 01 Hello - PRINT, strings and numbers
- summary: The smallest program: PRINT with text, numbers, ";" (no gap) and "," (next column).
- look: Tick PRINT on the "BASIC commands" tab, RUN, and step through stm_PRINT to see how text reaches the screen.
- look: Memory tab, button "Screen": the characters appear at $4000 as you step.
- break: stm_PRINT

```basic
10 CLS
20 PRINT "HELLO FROM THE MC-10"
30 PRINT "2+2 =";2+2
40 PRINT "A","B","C"
50 PRINT "DONE"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
HELLO FROM THE MC-10
2+2 = 4
A               B
C
DONE
OK
#
```

## 02_loops.bas

- title: 02 Loops - FOR/NEXT, STEP and IF/GOTO
- summary: Counts up, counts down with STEP, and builds a loop by hand with IF and GOTO.
- look: Break on stm_FOR and stm_NEXT: FOR pushes a loop record on the CPU stack (Stack tab), NEXT finds it again.
- look: The Profile tab shows where the time goes while the loop runs.
- break: stm_FOR, stm_NEXT

```basic
10 CLS
20 FOR I=1 TO 5:PRINT I;:NEXT I:PRINT
30 FOR I=10 TO 0 STEP -2:PRINT I;:NEXT:PRINT
40 N=1
50 PRINT N;:N=N*2
60 IF N<200 THEN GOTO 50
70 PRINT:PRINT "DONE"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
 1  2  3  4  5
 10  8  6  4  2  0
 1  2  4  8  16  32  64  128
DONE
OK
#
```

## 03_subroutines.bas

- title: 03 Subroutines - GOSUB, RETURN and ON...GOSUB
- summary: Calls a subroutine three times, then picks one of three subroutines with ON...GOSUB.
- look: Break on stm_GOSUB and stm_RETURN and watch the Stack tab: BASIC keeps its own return record there.
- break: stm_GOSUB, stm_RETURN, stm_ON

```basic
10 CLS
20 FOR K=1 TO 3:GOSUB 100:NEXT
30 FOR K=1 TO 3:ON K GOSUB 200,300,400:NEXT
40 PRINT "DONE":END
100 PRINT "CALL";K:RETURN
200 PRINT "ONE":RETURN
300 PRINT "TWO":RETURN
400 PRINT "THREE":RETURN
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
CALL 1
CALL 2
CALL 3
ONE
TWO
THREE
DONE
OK
#
```

## 04_math.bas

- title: 04 Math - operators and functions
- summary: Arithmetic with precedence, powers (the ^ key is CONTROL+W on the MC-10), and the math functions.
- look: Tick SIN or SQR on the "BASIC commands" tab to stop inside the floating point code.
- look: op_MUL and op_DIV are the multiply and divide routines every expression uses.
- break: fn_SQR, op_MUL

```basic
10 CLS
20 PRINT 2+3*4;(2+3)*4;7/2
30 PRINT 2^8;SQR(144);ABS(-5);INT(7.9)
40 PRINT SGN(-3);SGN(0);SGN(3)
50 FOR A=0 TO 3:PRINT SIN(A);:NEXT:PRINT
60 PRINT EXP(1);LOG(10)
70 PRINT RND(6);RND(6);RND(6)
80 PRINT "DONE"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
 14  20  3.5
 256.000001  12  5  7
-1  0  1
 0  .841470985  .909297427  .141
120008
 2.71828183  2.30258509
 3  2  1
DONE
OK
```

## 05_strings.bas

- title: 05 Strings - joining, slicing and converting
- summary: Builds a string, takes pieces with LEFT$/MID$/RIGHT$, converts with ASC/CHR$/STR$/VAL and reverses it.
- look: Break on fn_MIDS and step: string functions work on a (length, address) descriptor, not on the text itself.
- break: fn_LEFTS, fn_MIDS, fn_CHRS

```basic
10 CLS
20 A$="MICRO"+"COLOR"
30 PRINT A$;LEN(A$)
40 PRINT LEFT$(A$,5);" ";RIGHT$(A$,5);" ";MID$(A$,3,4)
50 PRINT ASC("A");CHR$(66);STR$(12);VAL("34")+1
60 B$=""
70 FOR I=LEN(A$) TO 1 STEP -1:B$=B$+MID$(A$,I,1):NEXT
80 PRINT B$
90 PRINT "DONE"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
MICROCOLOR 10
MICRO COLOR CROC
 65 B 12 35
ROLOCORCIM
DONE
OK
#
```

## 06_graphics.bas

- title: 06 Graphics - CLS colours, SET, RESET and POINT
- summary: Draws a frame and a diagonal with SET on the 64 x 32 block grid, reads a block back with POINT, erases with RESET. It then waits: press BREAK (Esc) to stop.
- look: Memory tab, "Screen": each screen byte at $4000 holds four blocks plus a colour; SET changes single bits.
- break: stm_SET, stm_RESET, fn_POINT

```basic
10 CLS 0
20 FOR X=0 TO 63:SET(X,0,4):SET(X,29,4):NEXT
30 FOR Y=0 TO 29:SET(0,Y,2):SET(63,Y,2):NEXT
40 FOR I=2 TO 27:SET(I*2,I,5):NEXT
50 P=POINT(10,5)
60 RESET(10,5)
70 PRINT @ 448,"POINT(10,5) WAS";P;
80 PRINT @ 480,"PRESS BREAK";
90 GOTO 90
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
################################
################################
################################
################################
################################
################################
################################
################################
################################
################################
################################
################################
################################
################################
POINT(10,5) WAS 5 ##############
PRESS BREAK#####################
```

## 07_peek_poke.bas

- title: 07 PEEK and POKE - writing straight to screen memory
- summary: The screen is 512 bytes at 16384 ($4000). POKE puts character codes there; PEEK reads them back.
- look: Add a write-watch on 4020 ("Break on write" tab), RUN, and the debugger stops on the instruction that writes it.
- look: Codes 0-63 are reverse video, 64-127 normal, 128-255 colour blocks.
- break: stm_POKE, fn_PEEK

```basic
10 CLS
20 FOR I=0 TO 31:POKE 16384+32+I,65+I:NEXT
30 FOR I=0 TO 31:POKE 16384+96+I,I+1:NEXT
40 FOR I=0 TO 31:POKE 16384+160+I,128+I*4:NEXT
50 PRINT @ 256,"PEEK(16416) =";PEEK(16416)
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```

ABCDEFGHIJKLMNOPQRSTUVWXYZ[\]^_

abcdefghijklmnopqrstuvwxyz[\]^_

################################


PEEK(16416) = 65
OK
```

## 08_input.bas

- title: 08 INPUT - a guessing game
- summary: Asks for numbers until you find the secret one. Type a number and press ENTER when it asks.
- look: Pause while it waits: the CPU sits in the keyboard scan. The Hardware tab shows the column strobe on port 1.
- look: Break on stm_INPUT to follow a typed line from the keyboard buffer into a variable.
- break: stm_INPUT

```basic
10 CLS:S=RND(20):T=0
20 PRINT "GUESS 1 TO 20"
30 INPUT G:T=T+1
40 IF G<S THEN PRINT "HIGHER":GOTO 30
50 IF G>S THEN PRINT "LOWER":GOTO 30
60 PRINT "RIGHT IN";T;"TRIES"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
GUESS 1 TO 20
? 10
RIGHT IN 1 TRIES
OK
#
```

## 09_inkey.bas

- title: 09 INKEY$ - reading keys without ENTER
- summary: Shows the code of every key you press until you press Q. INKEY$ returns "" when no key is down.
- look: Break on fn_INKEYS, then Step over (F11) across the keyboard scan and look at register A.
- break: fn_INKEYS

```basic
10 CLS:PRINT "PRESS KEYS, Q TO QUIT"
20 K$=INKEY$:IF K$="" THEN 20
30 PRINT K$;ASC(K$)
40 IF K$<>"Q" THEN 20
50 PRINT "DONE"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
PRESS KEYS, Q TO QUIT
A 65
7 55
Q 81
DONE
OK
#
```

## 10_sound.bas

- title: 10 SOUND - a rising scale
- summary: SOUND pitch,duration. The firmware makes the tone by toggling a bit in a timed loop, so the CPU is busy while it plays.
- look: Break on stm_SOUND; the Profile tab shows almost all the time inside the tone loop.
- break: stm_SOUND

```basic
10 CLS:PRINT "SOUND TEST"
20 FOR P=50 TO 200 STEP 30
30 SOUND P,1
40 NEXT
50 PRINT "DONE"
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
SOUND TEST
DONE
OK
#
```

## 11_data_sort.bas

- title: 11 DATA, arrays and a bubble sort
- summary: Reads eight numbers from DATA into an array, sorts them, prints them. RESTORE would rewind the DATA pointer.
- look: Break on stm_READ and stm_DIM. Arrays live after the simple variables, above the program text in RAM.
- break: stm_READ, stm_DIM

```basic
10 CLS:DIM A(8)
20 FOR I=1 TO 8:READ A(I):NEXT
30 FOR I=1 TO 7:FOR J=1 TO 8-I
40 IF A(J)>A(J+1) THEN T=A(J):A(J)=A(J+1):A(J+1)=T
50 NEXT J,I
60 FOR I=1 TO 8:PRINT A(I);:NEXT:PRINT
70 PRINT "DONE"
80 DATA 42,7,19,3,88,1,56,23
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
 1  3  7  19  23  42  56  88
DONE
OK
```

## 12_machine_code.bas

- title: 12 Machine code - POKE a 6803 routine and EXEC it
- summary: CLEAR reserves memory above 20299, the loop POKEs a 14-byte routine at 20300 ($4F4C) and EXEC calls it. The routine fills the second half of the screen with "*" and returns.
- look: In the debugger add a breakpoint at 4F4C (Breakpoints tab, Add), RUN, then use the "Live (from PC)" tab: it disassembles your own code in RAM.
- look: The routine is LDX #$4100 / LDAA #$6A / STAA 0,X / INX / CPX #$4200 / BNE loop / RTS.
- break: stm_EXEC

```basic
10 CLEAR 100,20299
20 CLS
30 FOR I=0 TO 13:READ B:POKE 20300+I,B:NEXT
40 EXEC 20300
50 PRINT "BACK IN BASIC"
60 DATA 206,65,0,134,106,167,0,8,140,66,0,38
70 DATA 248,57
```

Screen after RUN (`#` = colour block or cursor, lowercase = reverse video):

```
BACK IN BASIC
OK
#





********************************
********************************
********************************
********************************
********************************
********************************
********************************
********************************
```
