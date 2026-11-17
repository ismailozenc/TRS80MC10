;; title: 07 PEEK and POKE - writing straight to screen memory
;; summary: The screen is 512 bytes at 16384 ($4000). POKE puts character codes there; PEEK reads them back.
;; look: Add a write-watch on 4020 ("Watch writes" tab), RUN, and the debugger stops on the instruction that writes it.
;; look: Codes 0-63 are reverse video, 64-127 normal, 128-255 colour blocks.
;; break: stm_POKE, fn_PEEK
10 CLS
20 FOR I=0 TO 31:POKE 16384+32+I,65+I:NEXT
30 FOR I=0 TO 31:POKE 16384+96+I,I+1:NEXT
40 FOR I=0 TO 31:POKE 16384+160+I,128+I*4:NEXT
50 PRINT @ 256,"PEEK(16416) =";PEEK(16416)
