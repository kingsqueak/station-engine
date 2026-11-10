/* SPDX-License-Identifier: BSD-2-Clause
 *
 * simplehpsdr_rade — a thin single-entry shim over RADE V1 (radae_c) + the Opus DNN
 * FARGAN vocoder + LPCNet feature analyzer, packaged for SimpleHPSDR. It hides RADE's
 * two-stage RX/TX behind one streaming surface so the .NET side P/Invokes a
 * single library instead of binding rade_api.h AND the Opus DNN APIs.
 *
 * RX: rade_rx(IQ@8kHz) -> feature frames (36 floats) -> FARGAN -> 16 kHz PCM
 *     (prime with the first 5 frames, then 160 PCM/frame).
 * TX: 16 kHz PCM -> LPCNet analyzer -> feature frames -> rade_tx -> modem IQ@8kHz
 *     (transmit the real part). The EOO callsign uses the FreeDV reliable-text
 *     LDPC (rade_text), interoperable with FreeDV-GUI.
 * RADE + FARGAN weights and the LPCNet analyzer are compiled in — no model files.
 *
 * All sample rates / buffer sizes come from the underlying rade_api.h:
 *   modem IQ  = RADE_MODEM_SAMPLE_RATE  (8000)
 *   speech    = RADE_SPEECH_SAMPLE_RATE (16000)
 */
#ifndef SIMPLEHPSDR_RADE_H
#define SIMPLEHPSDR_RADE_H

#include "rade_api.h"   /* RADE_COMP, RADE_MODEM_SAMPLE_RATE, RADE_SPEECH_SAMPLE_RATE */

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#  define SIMPLEHPSDR_RADE_EXPORT __declspec(dllexport)
#else
#  define SIMPLEHPSDR_RADE_EXPORT __attribute__((visibility("default")))
#endif

typedef struct simplehpsdr_rade simplehpsdr_rade;

/* Library lifecycle (wrap rade_initialize/finalize; call once per process). */
SIMPLEHPSDR_RADE_EXPORT void        simplehpsdr_rade_global_init(void);
SIMPLEHPSDR_RADE_EXPORT void        simplehpsdr_rade_global_shutdown(void);

/* Open/close a decoder context. Returns NULL on failure. */
SIMPLEHPSDR_RADE_EXPORT simplehpsdr_rade  *simplehpsdr_rade_open(void);
SIMPLEHPSDR_RADE_EXPORT void        simplehpsdr_rade_close(simplehpsdr_rade *z);

/* Buffer-sizing helpers (call after open). */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_nin(simplehpsdr_rade *z);       /* IQ samples for the NEXT rx call (varies) */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_nin_max(simplehpsdr_rade *z);   /* max ever — size rx_in[] with this */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_max_pcm_per_rx(simplehpsdr_rade *z); /* upper bound on PCM produced per rx call */

/* Decode one block of IQ to 16 kHz PCM.
 *   rx_in   : simplehpsdr_rade_nin(z) complex (interleaved real,imag) samples @ 8 kHz
 *   pcm_out : caller buffer, >= simplehpsdr_rade_max_pcm_per_rx(z) int16 samples @ 16 kHz
 * Returns the number of int16 PCM samples written (0 while unsynced / priming). */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_rx(simplehpsdr_rade *z, const RADE_COMP *rx_in, short *pcm_out);

/* ---- Transmit (mirror of the RX surface) -------------------------------- *
 * RADE TX turns 16 kHz speech into the modem waveform. Internally:
 *   speech 16 kHz --(opus_dnn LPCNet feature analyzer)--> 36-float frames
 *               --(rade_tx)--> RADE_COMP modem IQ @ 8 kHz.
 * The transmitted SSB audio is the REAL part of the modem IQ (the OFDM sits at
 * an audio passband, exactly the inverse of the RX feed which uses imag=0). The
 * shim consumes a fixed frame-group of speech per call and emits a fixed block
 * of modem samples — frame-synchronous, no priming. */

/* TX sizing (call after open). */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_n_speech_samples(simplehpsdr_rade *z); /* int16 @16k consumed per tx call */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_n_tx_out(simplehpsdr_rade *z);         /* RADE_COMP produced per tx call */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_n_tx_eoo_out(simplehpsdr_rade *z);     /* RADE_COMP in the End-of-Over frame */

/* Encode one frame-group of speech to modem IQ.
 *   pcm_in  : simplehpsdr_rade_n_speech_samples(z) int16 samples @ 16 kHz
 *   tx_out  : caller buffer, >= simplehpsdr_rade_n_tx_out(z) RADE_COMP @ 8 kHz
 * Returns the number of RADE_COMP samples written. */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_tx(simplehpsdr_rade *z, const short *pcm_in, RADE_COMP *tx_out);

/* Flush the End-of-Over frame (carries the callsign set via set_tx_callsign).
 * Call once on un-key, after the last simplehpsdr_rade_tx.
 *   tx_eoo_out : caller buffer, >= simplehpsdr_rade_n_tx_eoo_out(z) RADE_COMP @ 8 kHz
 * Returns the number of RADE_COMP samples written. */
SIMPLEHPSDR_RADE_EXPORT int  simplehpsdr_rade_tx_eoo(simplehpsdr_rade *z, RADE_COMP *tx_eoo_out);

/* Set the callsign embedded in the EOO frame (<= 8 chars). Encoded with the
 * FreeDV reliable-text LDPC (rade_text) so it interoperates with FreeDV-GUI RADE
 * stations — NOT radae_c's internal 7-bit packing. Empty/NULL clears it. */
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_tx_callsign(simplehpsdr_rade *z, const char *callsign);

/* Telemetry (valid when synced). */
SIMPLEHPSDR_RADE_EXPORT int   simplehpsdr_rade_sync(simplehpsdr_rade *z);
SIMPLEHPSDR_RADE_EXPORT float simplehpsdr_rade_freq_offset(simplehpsdr_rade *z);
SIMPLEHPSDR_RADE_EXPORT int   simplehpsdr_rade_snr_db(simplehpsdr_rade *z);

/* TX-side mic tap meters, updated by the most recent simplehpsdr_rade_tx() call.
 *   simplehpsdr_rade_tx_mic_level_db: block peak |sample| as dBFS, -120 if silent.
 *   simplehpsdr_rade_tx_mic_clip:     1 if any sample hit full scale within roughly
 *     the last 500 ms of TX audio (a short hold so a single clipped sample is
 *     visible on a UI meter), else 0. */
SIMPLEHPSDR_RADE_EXPORT int   simplehpsdr_rade_tx_mic_level_db(simplehpsdr_rade *z);
SIMPLEHPSDR_RADE_EXPORT int   simplehpsdr_rade_tx_mic_clip(simplehpsdr_rade *z);

/* Pre-encoder mic conditioning (TX-side). Both stages are OFF by default
 * (bit-exact passthrough, matching the original shim behaviour) and take
 * effect on the next simplehpsdr_rade_tx() call after a setter changes state.
 *
 * AGC targets a block RMS level in dBFS with asymmetric attack (~10ms, turn a
 * hot mic down fast) / release (~300ms, bring a quiet mic up slowly).
 *
 * EQ is a fixed 3-band voice shaper (bass low-shelf ~200Hz, mid peaking
 * ~1kHz/Q0.7, treble high-shelf ~3kHz) — a per-band gain in dB, NOT a general
 * parametric EQ (center frequency/Q are fixed, unlike Thetis's fully
 * parametric mic EQ) to keep the API small. */
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_mic_agc_enabled(simplehpsdr_rade *z, int enable);
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_mic_agc_target_db(simplehpsdr_rade *z, float target_dbfs);
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_mic_eq_enabled(simplehpsdr_rade *z, int enable);
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_mic_eq_bass_db(simplehpsdr_rade *z, float gain_db);
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_mic_eq_mid_db(simplehpsdr_rade *z, float gain_db);
SIMPLEHPSDR_RADE_EXPORT void simplehpsdr_rade_set_mic_eq_treble_db(simplehpsdr_rade *z, float gain_db);

/* Last decoded End-of-Over callsign (RADE carries up to 8 chars in the EOO
 * frame). Decoded with the FreeDV reliable-text LDPC (rade_text) — CRC-checked,
 * so it interoperates with FreeDV-GUI RADE stations. Copies into callsign_out
 * (>= 9 bytes); returns chars written, 0 if none since the last over. Maps to
 * the existing FreeDV RX-text UI. */
SIMPLEHPSDR_RADE_EXPORT int   simplehpsdr_rade_get_eoo_callsign(simplehpsdr_rade *z, char *callsign_out);

#ifdef __cplusplus
}
#endif
#endif /* SIMPLEHPSDR_RADE_H */
