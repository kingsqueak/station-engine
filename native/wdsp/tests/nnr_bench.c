// SPDX-License-Identifier: GPL-2.0-or-later
//
// nnr_bench.c: speed and bit-exactness check for the NNR network (nnet.c).
// Not built by CMake and not part of the library.
//
//   nnr_bench <model slot 0..3> [buffers] [fftw wisdom file]
//
// Feeds a fixed pseudo-random signal (noise plus two tones) through one NNR
// and prints a hash of every output sample and the share of a processor core
// the network used.  To compare two versions of nnet.c, build this against
// each and compare the hashes; they must be equal.  NNR plans its FFTs with
// FFTW_PATIENT, which picks plans by timing and so changes the last bits from
// run to run: for a comparison, build nnr.c with FFTW_PATIENT replaced by
// FFTW_ESTIMATE (sed), which is deterministic.
//
// Link against every WDSP object (the NR3/NR4 stubs will do) and FFTW.
// Used for the 2026-11-17 vectorising change: equal hashes for all four
// models over 3000 buffers with GCC 14 on a Raspberry Pi 5 and Apple Clang on
// an M2, and 18 to 25% less time on the Pi, 36 to 42% less on the M2.
#include "comm.h"
#include <time.h>
#include <stdint.h>
int main (int argc, char** argv)
{
	int slot = argc > 1 ? atoi (argv[1]) : 0;
	int nbuf = argc > 2 ? atoi (argv[2]) : 3000;
	int size = 256, rate = 48000, i, k;
	double* in  = (double*) malloc0 (size * 2 * sizeof (double));
	double* out = (double*) malloc0 (size * 2 * sizeof (double));
	if (argc > 3) printf ("wisdom import: %d\n", fftw_import_wisdom_from_filename (argv[3]));
	NNR a = create_nnr (1, 1, size, in, out, rate, -25.0, 1);
	int rc = setModel_nnr (a, slot);
	uint64_t h = 1469598103934665603ULL, lcg = 88172645463325252ULL;
	double ph1 = 0, ph2 = 0, sum = 0;
	struct timespec t0, t1;
	clock_gettime (CLOCK_MONOTONIC, &t0);
	for (k = 0; k < nbuf; k++)
	{
		double env = 0.5 + 0.5 * sin (k * 0.013);
		for (i = 0; i < size; i++)
		{
			lcg = lcg * 6364136223846793005ULL + 1442695040888963407ULL;
			double n1 = ((double)(lcg >> 11) / 9007199254740992.0 - 0.5) * 0.1;
			lcg = lcg * 6364136223846793005ULL + 1442695040888963407ULL;
			double n2 = ((double)(lcg >> 11) / 9007199254740992.0 - 0.5) * 0.1;
			ph1 += 2 * 3.14159265358979323846 * 700.0 / rate;
			ph2 += 2 * 3.14159265358979323846 * 1650.0 / rate;
			in[2 * i]     = n1 + env * (0.2 * cos (ph1) + 0.1 * cos (ph2));
			in[2 * i + 1] = n2 + env * (0.2 * sin (ph1) + 0.1 * sin (ph2));
		}
		xnnr (a, 1);
		const unsigned char* p = (const unsigned char*) out;
		for (i = 0; i < (int)(size * 2 * sizeof (double)); i++) { h ^= p[i]; h *= 1099511628211ULL; }
		for (i = 0; i < size * 2; i++) sum += fabs (out[i]);
	}
	clock_gettime (CLOCK_MONOTONIC, &t1);
	double sec = (t1.tv_sec - t0.tv_sec) + (t1.tv_nsec - t0.tv_nsec) * 1e-9;
	double audio = (double) nbuf * size / rate;
	printf ("slot=%d rc=%d model=%d buffers=%d hash=%016llx sum=%.9g time=%.3fs audio=%.1fs load=%.1f%% of a core\n",
		slot, rc, getModel_nnr (a), nbuf, (unsigned long long) h, sum, sec, audio, 100.0 * sec / audio);
	return 0;
}
