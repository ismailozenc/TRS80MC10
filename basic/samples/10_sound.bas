;; title: 10 SOUND - a rising scale
;; summary: SOUND pitch,duration. The firmware makes the tone by toggling a bit in a timed loop, so the CPU is busy while it plays.
;; look: Break on stm_SOUND; the Profile tab shows almost all the time inside the tone loop.
;; break: stm_SOUND
10 CLS:PRINT "SOUND TEST"
20 FOR P=50 TO 200 STEP 30
30 SOUND P,1
40 NEXT
50 PRINT "DONE"
