#!/usr/bin/env bash
# Build + validate the simplehpsdr_rade shim against a real off-air RADE sample, using
# a prior radae_nopy build in $HOME/rade-build. Linux/WSL only (validation harness).
set -e
SHIM="$(cd "$(dirname "$0")" && pwd)"
B="$HOME/rade-build"
RSRC="$B/src"
BUILD="$B/build"

OPUS="$(find "$BUILD" -type d -name build_opus | head -1)"
LIBOPUS="$(find "$BUILD" -name libopus.a | head -1)"
mapfile -t RADE_OBJ < <(find "$BUILD" -path '*rade.dir*' -name '*.o')
REAL2IQ="$(find "$BUILD" -name real2iq -type f | head -1)"

echo "opus=$OPUS"
echo "libopus=$LIBOPUS"
echo "rade objs=${#RADE_OBJ[@]}"
echo "real2iq=$REAL2IQ"

mkdir -p "$B/simplehpsdr" && cd "$B/simplehpsdr"
echo "### compile shim + test"
gcc -O2 -I"$SHIM" -I"$RSRC" -I"$OPUS/dnn" -I"$OPUS/celt" -I"$OPUS/include" -I"$OPUS" \
    "$SHIM/simplehpsdr_rade.c" "$SHIM/simplehpsdr_rade_test.c" "${RADE_OBJ[@]}" "$LIBOPUS" -lm \
    -o simplehpsdr_rade_test

echo "### decode off-air sample through the shim's single IQ->PCM API"
sox "$B/FDV_offair.wav" -r 8000 -e float -b 32 -c 1 -t raw - 2>/dev/null \
  | "$REAL2IQ" 2>/dev/null \
  | ./simplehpsdr_rade_test > simplehpsdr_out.pcm
sox -t .s16 -r 16000 -c 1 simplehpsdr_out.pcm simplehpsdr_decoded.wav 2>/dev/null
echo "### output audio stats (proof of real speech)"
sox simplehpsdr_decoded.wav -n stat 2>&1 | grep -iE "samples read|length|maximum amplitude|rms amplitude"
