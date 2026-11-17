;; title: 05 Strings - joining, slicing and converting
;; summary: Builds a string, takes pieces with LEFT$/MID$/RIGHT$, converts with ASC/CHR$/STR$/VAL and reverses it.
;; look: Break on fn_MIDS and step: string functions work on a (length, address) descriptor, not on the text itself.
;; break: fn_LEFTS, fn_MIDS, fn_CHRS
10 CLS
20 A$="MICRO"+"COLOR"
30 PRINT A$;LEN(A$)
40 PRINT LEFT$(A$,5);" ";RIGHT$(A$,5);" ";MID$(A$,3,4)
50 PRINT ASC("A");CHR$(66);STR$(12);VAL("34")+1
60 B$=""
70 FOR I=LEN(A$) TO 1 STEP -1:B$=B$+MID$(A$,I,1):NEXT
80 PRINT B$
90 PRINT "DONE"
