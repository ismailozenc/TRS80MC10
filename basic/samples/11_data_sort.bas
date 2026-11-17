;; title: 11 DATA, arrays and a bubble sort
;; summary: Reads eight numbers from DATA into an array, sorts them, prints them. RESTORE would rewind the DATA pointer.
;; look: Break on stm_READ and stm_DIM. Arrays live after the simple variables, above the program text in RAM.
;; break: stm_READ, stm_DIM
10 CLS:DIM A(8)
20 FOR I=1 TO 8:READ A(I):NEXT
30 FOR I=1 TO 7:FOR J=1 TO 8-I
40 IF A(J)>A(J+1) THEN T=A(J):A(J)=A(J+1):A(J+1)=T
50 NEXT J,I
60 FOR I=1 TO 8:PRINT A(I);:NEXT:PRINT
70 PRINT "DONE"
80 DATA 42,7,19,3,88,1,56,23
