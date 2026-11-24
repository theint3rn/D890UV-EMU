/* Stand-in for the D890UV bootloader (not available): at 0x08000000, also seen at 0x0.
 * Does what an STM32 app-jump bootloader does: VTOR = app, MSP = app[0], jump app[1]. */
    .syntax unified
    .thumb
    .equ APP, 0x0800C000
    .section .vectors, "a"
    .word 0x20025FA0
    .word stub_reset + 1
    .space 0x100 - 8
    .text
    .thumb_func
stub_reset:
    ldr   r0, =APP
    ldr   r1, =0xE000ED08
    str   r0, [r1]
    ldr   r1, [r0]
    msr   msp, r1
    ldr   r1, [r0, #4]
    bx    r1
    .ltorg
