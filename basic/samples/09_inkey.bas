;; title: 09 INKEY$ - reading keys without ENTER
;; summary: Shows the code of every key you press until you press Q. INKEY$ returns "" when no key is down.
;; look: Break on fn_INKEYS, then Step over (F11) across the keyboard scan and look at register A.
;; break: fn_INKEYS
10 CLS:PRINT "PRESS KEYS, Q TO QUIT"
20 K$=INKEY$:IF K$="" THEN 20
30 PRINT K$;ASC(K$)
40 IF K$<>"Q" THEN 20
50 PRINT "DONE"
