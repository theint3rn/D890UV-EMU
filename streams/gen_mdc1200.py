#!/usr/bin/env python3
"""MDC1200 test audio for the ADC sample-stream injector (`analog Stream @file 48000`).

Writes mono int16 little-endian raw audio (default 48 kHz): silence, one MDC1200 packet (1200 baud FSK,
1200 Hz / 1800 Hz, differential: a bit equal to the previous one is one 1200 Hz cycle, a changed bit 1.5 cycles
of 1800 Hz), silence. Packet layout as in the widely used MDC1200 format: preamble, 40-bit sync 0x07092A446F,
then 112 coded bits: 4 data bytes (op, arg, unit id high, low) + CRC-16 + 0 -> rate 1/2 convolutional code
(taps 0, 2, 5, 6) -> 16 x 7 interleave.

The stock V1.05 decoder is FUN_0813f3d8 (TIM6 IRQ49 at 4.8 kHz reads ADC1 ch1 = DAT_20001d34[0];
zero crossings per bit at 4 bit phases; sync check FUN_0813c16c).

  python3 emu/streams/gen_mdc1200.py emu/streams/mdc1200_1234.raw --unit 0x1234 --op 0x01 --arg 0x80
"""
import argparse, math, struct


def crc16(data):
    crc = 0
    for c in data:
        j = 1
        while j <= 0x80:
            bit = crc & 0x8000
            crc = (crc << 1) & 0xFFFF
            if c & j:
                bit ^= 0x8000
            if bit:
                crc ^= 0x1021
            j <<= 1
    r = 0                                   # bit-reverse 16, invert
    for i in range(16):
        if crc & (1 << i):
            r |= 1 << (15 - i)
    return r ^ 0xFFFF


def encode_packet(op, arg, unit):
    d = [op & 0xFF, arg & 0xFF, (unit >> 8) & 0xFF, unit & 0xFF]
    c = crc16(d)
    d += [c & 0xFF, (c >> 8) & 0xFF, 0]
    d += [0] * 7
    csr = [0] * 7
    for i in range(7):                      # convolutional parity bytes 7..13
        for j in range(8):
            csr = [(d[i] >> j) & 1] + csr[:6]
            b = csr[0] + csr[2] + csr[5] + csr[6]
            d[i + 7] |= (b & 1) << j
    lbits = [0] * 112                       # interleave
    k = m = 0
    for i in range(14):
        for j in range(8):
            lbits[k] = (d[i] >> j) & 1
            k += 16
            if k > 111:
                m += 1
                k = m
    out = []
    k = 0
    for i in range(14):
        v = 0
        for j in range(7, -1, -1):
            if lbits[k]:
                v |= 1 << j
            k += 1
        out.append(v)
    return out


def packet_bits(op, arg, unit, preamble=7):
    data = [0x55] * preamble + [0x07, 0x09, 0x2A, 0x44, 0x6F] + encode_packet(op, arg, unit)
    return [(b >> (7 - i)) & 1 for b in data for i in range(8)]       # MSB first


def modulate(bits, rate, amp):
    out, phase, prev = [], 0.0, 0
    spb = rate / 1200.0
    n = 0
    for i, b in enumerate(bits):
        f = 1800.0 if b != prev else 1200.0
        prev = b
        end = int(round((i + 1) * spb))
        while n < end:
            out.append(int(amp * math.sin(phase)))
            phase += 2 * math.pi * f / rate
            n += 1
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--rate", type=int, default=48000)
    ap.add_argument("--unit", type=lambda s: int(s, 0), default=0x1234)
    ap.add_argument("--op", type=lambda s: int(s, 0), default=0x01, help="0x01 = PTT ID")
    ap.add_argument("--arg", type=lambda s: int(s, 0), default=0x80, help="0x80 = post-ID, 0x00 = pre-ID")
    ap.add_argument("--amp", type=int, default=12000)
    ap.add_argument("--repeat", type=int, default=3, help="packets, 0.5 s apart")
    a = ap.parse_args()
    sil = [0] * (a.rate // 2)
    s = list(sil)
    for _ in range(a.repeat):
        s += modulate(packet_bits(a.op, a.arg, a.unit), a.rate, a.amp) + sil
    open(a.out, "wb").write(struct.pack("<%dh" % len(s), *s))
    print("%s: %d samples, %.2f s at %d Hz, MDC1200 op 0x%02X arg 0x%02X unit %04X x%d" %
          (a.out, len(s), len(s) / a.rate, a.rate, a.op, a.arg, a.unit, a.repeat))


if __name__ == "__main__":
    main()
