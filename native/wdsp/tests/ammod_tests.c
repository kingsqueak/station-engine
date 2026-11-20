// SPDX-License-Identifier: GPL-2.0-or-later
//
// Deterministic tests for the SimpleHPSDR broadcast-AM extension of ammod.c,
// compiled standalone with WDSP_AMMOD_CORE_TEST (no FFTW / channel state).

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef int CRITICAL_SECTION;
#define WDSP_AMMOD_CORE_TEST 1
#include "ammod.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

void *malloc0 (int size) {
  return calloc (1, (size_t)size);
}

enum { RATE = 48000, BLOCK = 256, BLOCKS = 200 };

static void fill_tone (double* buf, int n, double freq, double amp, long* phase_idx) {
  int i;

  for (i = 0; i < n; i++) {
    double t = (double)(*phase_idx + i) / (double)RATE;
    buf[2 * i + 0] = amp * sin (2.0 * M_PI * freq * t);
    buf[2 * i + 1] = 0.0;
  }

  *phase_idx += n;
}

// Drive `amp` sine at `freq` through the modulator; return min/max envelope
// over the settled second half of the run.
static void run_tone (AMMOD a, double* in, double* out, double freq, double amp,
                      double* env_min, double* env_max) {
  long phase = 0;
  int b, i;
  *env_min = 1.0e9;
  *env_max = -1.0e9;

  for (b = 0; b < BLOCKS; b++) {
    fill_tone (in, BLOCK, freq, amp, &phase);
    xammod (a);

    for (i = 0; i < BLOCK; i++) {
      assert (isfinite (out[2 * i + 0]));
      assert (out[2 * i + 0] == out[2 * i + 1]);

      if (b >= BLOCKS / 2) {
        if (out[2 * i + 0] < *env_min) *env_min = out[2 * i + 0];
        if (out[2 * i + 0] > *env_max) *env_max = out[2 * i + 0];
      }
    }
  }
}

static void test_legacy_formula_unchanged (void) {
  double in[2 * BLOCK], out[2 * BLOCK];
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  long phase = 0;
  int i;
  double mult = 1.0 / sqrt (2.0);

  fill_tone (in, BLOCK, 1000.0, 0.8, &phase);
  xammod (a);

  for (i = 0; i < BLOCK; i++) {
    double expected = mult * (0.5 + 0.5 * in[2 * i + 0]);
    assert (fabs (out[2 * i + 0] - expected) < 1.0e-12);
  }

  destroy_ammod (a);
}

static void test_broadcast_holds_negative_limit_and_pep (void) {
  double in[2 * BLOCK], out[2 * BLOCK];
  double mult = 1.0 / sqrt (2.0);
  double env_min, env_max, pos_pct, neg_pct;
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);

  setFilter_ammod (a, 50.0, 4500.0);
  setBroadcast_ammod (a, 1, 1.25, 0.97, 0, 0);

  // Full-scale tone: the limiter turns it down to the negative limit on both
  // sides (a tone is even), without clipping.
  run_tone (a, in, out, 1000.0, 1.0, &env_min, &env_max);
  printf ("broadcast 1k full-scale: env_min=%.6f env_max=%.6f mult=%.6f\n", env_min, env_max, mult);

  // Carrier never pinches off: envelope floor = mult*c*(1-0.97) > 0.
  assert (env_min > 0.0);
  assert (env_min >= mult / 2.25 * (1.0 - 0.97) - 1.0e-12);
  // PEP never exceeds the legacy peak (mult * 1.0); an even tone stops at the
  // negative limit upwards too.
  assert (env_max <= mult + 1.0e-12);
  assert (fabs (env_max - mult / 2.25 * 1.97) < 0.01 * mult);
  assert (fabs (env_min - mult / 2.25 * 0.03) < 0.01 * mult);

  getModPeaks_ammod (a, &pos_pct, &neg_pct);
  printf ("broadcast mod peaks: +%.2f%% -%.2f%%\n", pos_pct, neg_pct);
  // The meter covers the whole run: while the tone starts, the upward side may pass
  // the settled 97% (it has room up to 125%); the downward side never passes its limit.
  assert (pos_pct > 95.0 && pos_pct <= 125.0 + 1.0e-9);
  assert (neg_pct > 95.0 && neg_pct <= 97.0 + 1.0e-9);

  // Peaks reset after a read.
  getModPeaks_ammod (a, &pos_pct, &neg_pct);
  assert (pos_pct == 0.0 && neg_pct == 0.0);

  // Pre-emphasis + invert still honour the limits (worst case for overshoot).
  setBroadcast_ammod (a, 1, 1.25, 0.97, 1, 1);
  run_tone (a, in, out, 3500.0, 1.5, &env_min, &env_max);
  printf ("broadcast 3.5k preemph+invert: env_min=%.6f env_max=%.6f\n", env_min, env_max);
  assert (env_min > 0.0);
  assert (env_max <= mult + 1.0e-12);

  destroy_ammod (a);
}

// The limiter must turn an over-driven tone down, not clip it: the settled
// output is still a sine (third harmonic more than 60 dB down), at the limit.
static void test_broadcast_limits_without_clipping (void) {
  enum { N = 48000 };
  static double in[2 * BLOCK], out[2 * BLOCK], m[N];
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  double env = (1.0 / sqrt (2.0)) / 2.25;
  long phase = 0;
  int b, i, n = 0;
  double f1r = 0, f1i = 0, f3r = 0, f3i = 0, a1, a3;

  setFilter_ammod (a, 50.0, 4500.0);
  setBroadcast_ammod (a, 1, 1.25, 0.95, 0, 0);

  for (b = 0; n < N; b++) {
    fill_tone (in, BLOCK, 1000.0, 1.0, &phase);
    xammod (a);

    if (b < 100) continue;                                  // settled

    for (i = 0; i < BLOCK && n < N; i++) m[n++] = out[2 * i] / env - 1.0;
  }

  for (i = 0; i < N; i++) {
    double w = 2.0 * M_PI * 1000.0 * (double)i / (double)RATE;
    f1r += m[i] * cos (w);
    f1i += m[i] * sin (w);
    f3r += m[i] * cos (3.0 * w);
    f3i += m[i] * sin (3.0 * w);
  }

  a1 = 2.0 * sqrt (f1r * f1r + f1i * f1i) / N;
  a3 = 2.0 * sqrt (f3r * f3r + f3i * f3i) / N;
  printf ("limited tone: fundamental %.4f, third harmonic %.1f dB\n", a1, 20.0 * log10 (a3 / a1 + 1.0e-300));
  assert (fabs (a1 - 0.95) < 0.005);
  assert (a3 < a1 * 1.0e-3);
  destroy_ammod (a);
}

// Lopsided audio (taller upwards, no DC) inside both limits passes untouched:
// +125% up, -75% down, and nothing is turned down.
static void test_broadcast_keeps_lopsided_audio (void) {
  static double in[2 * BLOCK], out[2 * BLOCK];
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  double env = (1.0 / sqrt (2.0)) / 2.25, lo = 1.0e9, hi = -1.0e9;
  long idx = 0;
  int b, i;

  setFilter_ammod (a, 0.0, 0.0);
  setBroadcast_ammod (a, 1, 1.25, 0.95, 0, 0);

  for (b = 0; b < BLOCKS; b++) {
    for (i = 0; i < BLOCK; i++, idx++) {
      double w = 2.0 * M_PI * 500.0 * (double)idx / (double)RATE;
      in[2 * i] = (sin (w) - 0.25 * cos (2.0 * w)) / 1.25;   // +1.0 / -0.6
      in[2 * i + 1] = 0.0;
    }

    xammod (a);

    if (b < BLOCKS / 2) continue;

    for (i = 0; i < BLOCK; i++) {
      double v = out[2 * i] / env - 1.0;

      if (v < lo) lo = v;

      if (v > hi) hi = v;
    }
  }

  printf ("lopsided audio: +%.1f%% / %.1f%%\n", 100.0 * hi, 100.0 * lo);
  assert (fabs (hi - 1.25) < 0.01);
  assert (fabs (lo + 0.75) < 0.01);
  destroy_ammod (a);
}

// A burst after quiet: the limiter is already down when the first peak
// arrives (look-ahead), so no sample passes the limits even then.
static void test_broadcast_limiter_catches_first_peak (void) {
  static double in[2 * BLOCK], out[2 * BLOCK];
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  double env = (1.0 / sqrt (2.0)) / 2.25, lo = 1.0e9, hi = -1.0e9;
  long phase = 0;
  int b, i;

  setFilter_ammod (a, 50.0, 4500.0);
  setBroadcast_ammod (a, 1, 1.25, 0.90, 0, 0);

  for (b = 0; b < 120; b++) {
    fill_tone (in, BLOCK, 700.0, (b / 20) % 2 ? 1.0 : 0.0, &phase);   // on and off every 107 ms
    xammod (a);

    for (i = 0; i < BLOCK; i++) {
      double v = out[2 * i] / env - 1.0;

      if (v < lo) lo = v;

      if (v > hi) hi = v;
    }
  }

  printf ("keyed bursts: +%.2f%% / %.2f%%\n", 100.0 * hi, 100.0 * lo);
  assert (lo >= -0.90 - 1.0e-9);
  assert (hi <= 1.25 + 1.0e-9);
  assert (lo < -0.85);                                        // and it does get there
  destroy_ammod (a);
}

static void test_highpass_removes_low_tone (void) {
  double in[2 * BLOCK], out[2 * BLOCK];
  double env_min, env_max;
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  double mult = 1.0 / sqrt (2.0);

  // Legacy path + HPF at 300 Hz: a 30 Hz tone is attenuated > 40 dB, so the
  // output sits on the bare carrier (mult * 0.5).
  setFilter_ammod (a, 300.0, 0.0);
  run_tone (a, in, out, 30.0, 0.8, &env_min, &env_max);
  printf ("hpf 300 Hz, 30 Hz tone: env_min=%.6f env_max=%.6f carrier=%.6f\n", env_min, env_max, mult * 0.5);
  assert (fabs (env_max - mult * 0.5) < 0.01 * mult * 0.5 * 0.8 + 0.004);
  assert (fabs (env_min - mult * 0.5) < 0.01 * mult * 0.5 * 0.8 + 0.004);

  // HPF off: the same tone modulates fully.
  setFilter_ammod (a, 0.0, 0.0);
  run_tone (a, in, out, 30.0, 0.8, &env_min, &env_max);
  assert (env_max > mult * (0.5 + 0.5 * 0.79));

  destroy_ammod (a);
}

static void test_brickwall_lowpass_attenuates_out_of_band (void) {
  double in[2 * BLOCK], out[2 * BLOCK];
  double env_min, env_max;
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  double mult = 1.0 / sqrt (2.0);
  double carrier = mult / 2.25;

  setFilter_ammod (a, 0.0, 4500.0);
  setBroadcast_ammod (a, 1, 1.25, 0.97, 0, 0);
  // 0.5 amplitude 9 kHz tone is well below the clipper; the 8th-order
  // Butterworth at 4.5 kHz attenuates one octave up by ~48 dB.
  run_tone (a, in, out, 9000.0, 0.5, &env_min, &env_max);
  printf ("lpf 4.5 kHz, 9 kHz tone: env deviation=%.6f carrier=%.6f\n", env_max - carrier, carrier);
  assert (env_max - carrier < carrier * 0.625 * 0.01);

  destroy_ammod (a);
}

static void test_samplerate_and_flush (void) {
  double in[2 * BLOCK], out[2 * BLOCK];
  double env_min, env_max;
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);

  setFilter_ammod (a, 50.0, 4500.0);
  setBroadcast_ammod (a, 1, 1.25, 0.97, 1, 0);
  setSamplerate_ammod (a, 96000);
  assert (a->rate == 96000);
  assert (a->hp[0].run && a->lp[0].run);
  run_tone (a, in, out, 1000.0, 1.0, &env_min, &env_max);
  assert (env_min > 0.0);

  flush_ammod (a);
  assert (a->hp[0].x1 == 0.0 && a->lp[3].y2 == 0.0 && a->pe_y1 == 0.0);

  // Out-of-range limits are clamped to the documented envelope.
  setBroadcast_ammod (a, 1, 3.0, 0.1, 0, 0);
  assert (a->pos_limit == 1.5 && a->neg_limit == 0.8);

  destroy_ammod (a);
}

// A NaN / Inf sample must not poison the IIR states: the modulator flushes
// its filters, emits a finite (carrier-only) value, and recovers fully.
static void inject_nan_and_recover (int broadcast) {
  double in[2 * BLOCK], out[2 * BLOCK];
  double env_min, env_max;
  AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
  long phase = 0;
  int i, bad;

  setFilter_ammod (a, 50.0, 4500.0);
  setBroadcast_ammod (a, broadcast, 1.25, 0.97, 1, 0);

  for (bad = 0; bad < 3; bad++) {
    fill_tone (in, BLOCK, 1000.0, 0.8, &phase);
    in[2 * 10 + 0] = bad == 0 ? NAN : bad == 1 ? INFINITY : -INFINITY;
    xammod (a);

    for (i = 0; i < BLOCK; i++) {
      assert (isfinite (out[2 * i + 0]));
      assert (out[2 * i + 0] >= 0.0);
    }

    assert (isfinite (a->hp[0].y1) && isfinite (a->lp[3].y1) && isfinite (a->pe_y1));
  }

  // Afterwards it modulates normally again.
  run_tone (a, in, out, 1000.0, 1.0, &env_min, &env_max);
  assert (env_max > env_min);
  if (broadcast) assert (env_min > 0.0);

  destroy_ammod (a);
}

static void test_nan_injection_is_contained (void) {
  inject_nan_and_recover (1);
  inject_nan_and_recover (0);

  // The clamp itself maps NaN into range.
  {
    double in[2 * BLOCK], out[2 * BLOCK];
    AMMOD a = create_ammod (1, 0, BLOCK, in, out, 0.5, RATE);
    setBroadcast_ammod (a, 1, NAN, NAN, 0, 0);
    assert (a->pos_limit == 1.25 && a->neg_limit == 0.97);
    destroy_ammod (a);
  }
  printf ("nan injection contained\n");
}

int main (void) {
  test_legacy_formula_unchanged ();
  test_broadcast_holds_negative_limit_and_pep ();
  test_broadcast_limits_without_clipping ();
  test_broadcast_keeps_lopsided_audio ();
  test_broadcast_limiter_catches_first_peak ();
  test_highpass_removes_low_tone ();
  test_brickwall_lowpass_attenuates_out_of_band ();
  test_samplerate_and_flush ();
  test_nan_injection_is_contained ();
  printf ("ammod tests passed\n");
  return 0;
}
