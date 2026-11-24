#!/bin/sh
# Build the symbol-stream generators against the cfw protocol libraries (read-only use) and write the test
# streams next to this script. Streams are played into the custom firmware with `syms Load @<file> <rate>`.
set -e
D=$(cd "$(dirname "$0")" && pwd)
P="$D/../../cfw/proto/dmr_nxdn"
B="$D/build"
mkdir -p "$B"
cc -std=c11 -O2 -I"$P/common" -I"$P/dmr" "$D/gen_dmr.c" "$P"/common/*.c "$P"/dmr/*.c -o "$B/gen_dmr"
"$B/gen_dmr" "$D/dmr_call.dibits" 91 9990201 1 30
