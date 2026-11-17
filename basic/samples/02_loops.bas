;; title: 02 Loops - FOR/NEXT, STEP and IF/GOTO
;; summary: Counts up, counts down with STEP, and builds a loop by hand with IF and GOTO.
;; look: Break on stm_FOR and stm_NEXT: FOR pushes a loop record on the CPU stack (Stack tab), NEXT finds it again.
;; look: The Profile tab shows where the time goes while the loop runs.
;; break: stm_FOR, stm_NEXT
10 CLS
20 FOR I=1 TO 5:PRINT I;:NEXT I:PRINT
30 FOR I=10 TO 0 STEP -2:PRINT I;:NEXT:PRINT
40 N=1
50 PRINT N;:N=N*2
60 IF N<200 THEN GOTO 50
70 PRINT:PRINT "DONE"
