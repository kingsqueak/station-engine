#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""The packaged FFTW wisdom file (wdspWisdom01.seed).

  wisdom-seed.py build <libwdsp> <out file>
      Runs WDSP's own WDSPwisdom, so the plans, flags and buffers are exactly
      what the engine asks for, and writes the result to <out file>. The FFTW
      library libwdsp loads (the one beside it) is the one the file is for.
      One plan at a time: about 8 minutes on a fast desktop.

  wisdom-seed.py check <libfftw3> <wisdom file>
      Counts how many of WDSP's 65 plans the file holds for that FFTW library
      (planned with FFTW_WISDOM_ONLY, so a gap gives no plan). Fails unless it
      holds all of them. The engine makes the same check before it uses the
      file (WdspWisdomParallel.TryUseSeed).

Used by the Native libraries workflow on the Linux arm64 runner; the engine
takes the file instead of planning on a Raspberry Pi.
"""
import ctypes
import os
import shutil
import sys
import tempfile

MIN_SIZE, MAX_SIZE = 64, 262144          # wisdom.c: MAX_WISDOM_SIZE
FFTW_PATIENT, FFTW_WISDOM_ONLY = 1 << 5, 1 << 21
FFTW_FORWARD, FFTW_BACKWARD = -1, 1


def build(wdsp_path, out_path):
    wdsp = ctypes.CDLL(os.path.abspath(wdsp_path))
    wdsp.WDSPwisdom.argtypes = [ctypes.c_char_p]
    wdsp.WDSPwisdom.restype = ctypes.c_int
    with tempfile.TemporaryDirectory() as work:
        # wisdom.c appends the file name to the directory as it is given.
        result = wdsp.WDSPwisdom((work + os.sep).encode())
        if result != 1:
            sys.exit(f"WDSPwisdom returned {result}, expected 1 (built)")
        os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
        shutil.copyfile(os.path.join(work, "wdspWisdom01"), out_path)
    print(f"wrote {out_path} ({os.path.getsize(out_path)} bytes)")


def plans():
    """wisdom.c's plans, in its order."""
    n = MIN_SIZE
    while n <= MAX_SIZE:
        yield ("cf", n)
        yield ("cb", n)
        yield ("cb", n + 1)
        n *= 2
    n = MIN_SIZE
    while n <= MAX_SIZE:
        yield ("rf", n)
        yield ("ri", n)
        n *= 2


def check(fftw_path, wisdom_path):
    fftw = ctypes.CDLL(os.path.abspath(fftw_path))
    libc = ctypes.CDLL(None)
    vp, i, u = ctypes.c_void_p, ctypes.c_int, ctypes.c_uint
    fftw.fftw_import_wisdom_from_filename.argtypes = [ctypes.c_char_p]
    fftw.fftw_import_wisdom_from_filename.restype = i
    fftw.fftw_plan_dft_1d.argtypes = [i, vp, vp, i, u]
    fftw.fftw_plan_dft_1d.restype = vp
    for name in ("fftw_plan_dft_r2c_1d", "fftw_plan_dft_c2r_1d"):
        getattr(fftw, name).argtypes = [i, vp, vp, u]
        getattr(fftw, name).restype = vp
    fftw.fftw_destroy_plan.argtypes = [vp]
    fftw.fftw_destroy_plan.restype = None
    libc.malloc.argtypes = [ctypes.c_size_t]
    libc.malloc.restype = vp

    if not fftw.fftw_import_wisdom_from_filename(os.path.abspath(wisdom_path).encode()):
        sys.exit(f"{wisdom_path}: FFTW could not read it (made with another FFTW build, or damaged)")

    # As wisdom.c: two separate malloc'd buffers of (MAX_WISDOM_SIZE + 1)
    # complex values; the alignment is part of each plan's key.
    size = (MAX_SIZE + 1) * 2 * ctypes.sizeof(ctypes.c_double)
    a, b = libc.malloc(size), libc.malloc(size)
    ctypes.memset(a, 0, size)
    ctypes.memset(b, 0, size)
    flags = FFTW_PATIENT | FFTW_WISDOM_ONLY
    missing = []
    total = 0
    for kind, n in plans():
        total += 1
        if kind == "cf":
            plan = fftw.fftw_plan_dft_1d(n, a, b, FFTW_FORWARD, flags)
        elif kind == "cb":
            plan = fftw.fftw_plan_dft_1d(n, a, b, FFTW_BACKWARD, flags)
        elif kind == "rf":
            plan = fftw.fftw_plan_dft_r2c_1d(n, a, b, flags)
        else:
            plan = fftw.fftw_plan_dft_c2r_1d(n, a, b, flags)
        if plan:
            fftw.fftw_destroy_plan(plan)
        else:
            missing.append(f"{kind}:{n}")
    print(f"{wisdom_path}: {total - len(missing)} of {total} plans")
    if missing:
        sys.exit("missing: " + " ".join(missing))


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "build":
        build(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 4 and sys.argv[1] == "check":
        check(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
