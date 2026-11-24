#!/bin/sh
# cfw MCU-DSP demodulators end to end through the ADC1 sample-stream injector (normal cfw build, no EMU=1):
# select the built-in channel of each mode, play its 48 kHz discriminator file (cfw/build_streams/*.adc16, 12-bit
# counts, from `make -C cfw/dsp waves`) with `analog Stream`, screenshot after 2.5 s and at the end.
# Path: Analog.cs timer-triggered mode (ADC1 EXTSEL 14 = TIM15 TRGO, 48 kHz) -> 5-channel scan frames into the
# GPDMA1 ch0 ring + C0BR1.BNDT -> cfw drivers/adc.c -> hal/dsp_backend.c -> dsp demod -> proto glue.
# Usage: emu/streams/test_cfw_adc.sh [FIRST_BUILTIN_CHANNEL_NUMBER (default: emu/run.py first_builtin(), 1404 / 9 on synth.pages)] [MODE...]
# Out: emu/out/cfw_a_<mode>/montage.png (+ then_out.txt with analog StreamStatus)
set -e
R=$(cd "$(dirname "$0")/../.." && pwd); S="$R/cfw/build_streams"; cd "$R"
B=${1:-$(python3 -c "import sys; sys.path.insert(0, \"emu\"); import run; print(run.first_builtin())")}; [ $# -gt 0 ] && shift
ONLY="$*"
make -s -C cfw >/dev/null
[ -f "$S/dmr_call.adc16" ] || make -s -C cfw/dsp waves >/dev/null
keys() { out=""; for d in $(echo "$1" | sed 's/./& /g'); do out="$out key:$((d + 1))"; done; echo "$out key:12"; }
run() { # name channel_offset file seconds
  if [ -n "$ONLY" ] && ! echo " $ONLY " | grep -q " $1 "; then return; fi
  python3 cfw/tools/emutest.py "cfw_a_$1" --elf cfw/build/cfw.elf $(keys $(($B + $2))) \
    "0|analog Stream @$S/$3 48000" wait:2.5 wait:$4 "0|analog StreamStatus" | tail -1
}
run dmr     5 dmr_call.adc16   1.5
run dmr_t3  6 dmr_t3.adc16     6
run p25     7 p25_call.adc16   1.5
run p25trk  8 p25_trunk.adc16  5
run p25p2   9 p25p2_call.adc16 2
run dstar  10 dstar_call.adc16 2
run nxdn   11 nxdn_call.adc16  2.5
run ysf    12 ysf_call.adc16   6.5
