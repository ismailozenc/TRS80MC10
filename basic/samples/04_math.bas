;; title: 04 Math - operators and functions
;; summary: Arithmetic with precedence, powers (the ^ key is CONTROL+W on the MC-10), and the math functions.
;; look: Tick SIN or SQR on the "BASIC commands" tab to stop inside the floating point code.
;; look: op_MUL and op_DIV are the multiply and divide routines every expression uses.
;; break: fn_SQR, op_MUL
10 CLS
20 PRINT 2+3*4;(2+3)*4;7/2
30 PRINT 2^8;SQR(144);ABS(-5);INT(7.9)
40 PRINT SGN(-3);SGN(0);SGN(3)
50 FOR A=0 TO 3:PRINT SIN(A);:NEXT:PRINT
60 PRINT EXP(1);LOG(10)
70 PRINT RND(6);RND(6);RND(6)
80 PRINT "DONE"
