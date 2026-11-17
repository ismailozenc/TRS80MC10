;; title: 03 Subroutines - GOSUB, RETURN and ON...GOSUB
;; summary: Calls a subroutine three times, then picks one of three subroutines with ON...GOSUB.
;; look: Break on stm_GOSUB and stm_RETURN and watch the Stack tab: BASIC keeps its own return record there.
;; break: stm_GOSUB, stm_RETURN, stm_ON
10 CLS
20 FOR K=1 TO 3:GOSUB 100:NEXT
30 FOR K=1 TO 3:ON K GOSUB 200,300,400:NEXT
40 PRINT "DONE":END
100 PRINT "CALL";K:RETURN
200 PRINT "ONE":RETURN
300 PRINT "TWO":RETURN
400 PRINT "THREE":RETURN
