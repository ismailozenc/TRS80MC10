;; title: 08 INPUT - a guessing game
;; summary: Asks for numbers until you find the secret one. Type a number and press ENTER when it asks.
;; look: Pause while it waits: the CPU sits in the keyboard scan. The Hardware tab shows the column strobe on port 1.
;; look: Break on stm_INPUT to follow a typed line from the keyboard buffer into a variable.
;; break: stm_INPUT
10 CLS:S=RND(20):T=0
20 PRINT "GUESS 1 TO 20"
30 INPUT G:T=T+1
40 IF G<S THEN PRINT "HIGHER":GOTO 30
50 IF G>S THEN PRINT "LOWER":GOTO 30
60 PRINT "RIGHT IN";T;"TRIES"
