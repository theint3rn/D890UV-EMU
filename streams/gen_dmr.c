/* Test stream for the cfw symbol FIFO (emu/models/SymbolSource.cs): a DMR repeater-output (BS) voice call
 * built with cfw/proto/dmr_nxdn's transmitter, written as one dibit (0..3) per byte at 4800 symbols/s.
 *   noise 0.5 s | slot 1: 2 voice headers, N voice bursts (A..F superframes), terminator | slot 2: idle | noise
 * Build + run: emu/streams/make_streams.sh (needs only a host C compiler). Usage:
 *   gen_dmr OUT.dibits [dst=91] [src=9990201] [cc=1] [voice_bursts=30]
 * The voice payload is a counter pattern (frame k = bytes k, k+1, ...), so a decoder can check it. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "dmr.h"

static uint8_t g_out[200000];
static unsigned g_n;
static uint32_t g_seed = 12345;
static unsigned rnd(void) { g_seed = g_seed * 1103515245u + 12345u; return g_seed >> 16; }
static void put(const uint8_t *d, unsigned n) { memcpy(g_out + g_n, d, n); g_n += n; }
static void noise(unsigned n) { while(n--) { g_out[g_n++] = (uint8_t)(rnd() & 3u); } }

int main(int argc, char **argv)
{
    if(argc < 2) { fprintf(stderr, "usage: %s OUT.dibits [dst] [src] [cc] [voice_bursts]\n", argv[0]); return 1; }
    uint32_t dst = argc > 2 ? (uint32_t)strtoul(argv[2], 0, 0) : 91;
    uint32_t src = argc > 3 ? (uint32_t)strtoul(argv[3], 0, 0) : 9990201;
    uint8_t cc = argc > 4 ? (uint8_t)atoi(argv[4]) : 1;
    int nv = argc > 5 ? atoi(argv[5]) : 30;

    dmr_lc_t lc;
    dmr_lc_make_voice(&lc, true, dst, src, 0);
    dmr_slc_t slc = { DMR_SLCO_C_SYS_PARMS, 0x4A5B6C };
    dmr_voice_tx_t vt;
    dmr_voice_tx_init(&vt, &lc, cc, DMR_SYNC_BS_VOICE);
    uint8_t burst[DMR_BURST_DIBITS], cach[DMR_CACH_DIBITS];
    unsigned frame = 0;

    noise(2400);
    int slots = 2 + nv + 1 + 4;                       /* headers, voice, terminator, idle */
    for(int k = 0; k < 2 * slots; k++) {
        int slot = k & 1, i = k >> 1;
        dmr_tx_cach_slc(&slc, (unsigned)k % 4u, 1, (uint8_t)slot, cach);
        if(slot == 0) {
            if(i < 2) { dmr_tx_voice_header(&lc, cc, DMR_SYNC_BS_DATA, burst); }
            else if(i < 2 + nv) {
                uint8_t fr[27];
                for(int f = 0; f < 3; f++, frame++) { for(int b = 0; b < 9; b++) { fr[9 * f + b] = (uint8_t)(frame + b); } }
                dmr_voice_tx_burst(&vt, fr, burst);
            }
            else if(i == 2 + nv) { dmr_tx_terminator(&lc, cc, DMR_SYNC_BS_DATA, burst); }
            else { dmr_tx_idle(cc, DMR_SYNC_BS_DATA, burst); }
        } else {
            dmr_tx_idle(cc, DMR_SYNC_BS_DATA, burst);
        }
        put(cach, DMR_CACH_DIBITS);
        put(burst, DMR_BURST_DIBITS);
    }
    noise(2400);

    FILE *f = fopen(argv[1], "wb");
    if(!f) { perror(argv[1]); return 1; }
    fwrite(g_out, 1, g_n, f);
    fclose(f);
    printf("%s: %u dibits (%.2f s at 4800/s), TG %u from %u, CC %u, %u voice frames\n",
           argv[1], g_n, g_n / 4800.0, dst, src, cc, frame);
    return 0;
}
