#!/usr/bin/env python3
"""Two-radio loopback test for the custom firmware's digital TX/RX (440 Hz test tone, emulator only).

  python3 emu/txrx_loop.py --mode dmr                 # one mode
  python3 emu/txrx_loop.py --mode all --snr 20 --offset 300
  python3 emu/txrx_loop.py --mode p25 --skip-tx       # reuse radio A's last capture
  python3 emu/txrx_loop.py --mode all --simplex --chan dmr=1430,... --neg dmr=1409:1430   # simplex matrix

Radio A (cfw/build/cfw.elf): select the mode's built-in channel, test tone on (sets the firmware's tone frequency
variable, the same thing Menu > Test tone does), hold PTT --ptt seconds. models/Pwm.cs captures the TX audio PWM
(TIM4_CH4 on PB9, the stock MCU TX audio pin, docs cfw/docs/AUDIO_PATHS.md; duty -> 12-bit sample)
while the RF chip transmits: out/loop_<mode>_A/tx_1.adc16 (48 kHz 12-bit counts, 2048 + 0.25 count/Hz) + tx_1.json.
The capture is padded with idle (2048), optionally given AWGN (--snr dB, relative to the signal's RMS deviation) and a
frequency offset (--offset Hz = 0.25 count/Hz), and written to out/loop_<mode>_B/rx_in.adc16.
Radio B (same firmware, same channel): `analog Stream` plays it into ADC1 ch1; the MCU-DSP demodulator + proto + test
vocoder decode it. models/PcmTap.cs saves the decoded PCM (g_emu_pcm, 8 kHz) to out/loop_<mode>_B/rx_pcm.raw.
The tool measures the recovered tone (frequency by FFT peak with interpolation, SNR = tone vs everything else in
200-3800 Hz), reads radio B's screen text (IDs, the firmware's own "AF ... Hz ... dB" meter) and writes
out/txrx_loop.md (table) + montage_<mode>.png (A while transmitting, B at the end).
"""
import argparse, json, math, os, re, struct, subprocess, sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.dirname(HERE)
ELF = os.path.join(os.environ.get("CFW_SNAP") or os.path.join(SRC, "cfw"), "build", "cfw.elf")   # CFW_SNAP: ELF snapshot dir
OUT = os.path.join(HERE, "out")
# built-in channel offset from the first built-in channel number (cfw app/channels.c, as cfw/tools/emu_modes.sh)
MODES = {"dmr": 5, "p25": 7, "p25p2": 9, "dstar": 10, "nxdn": 11, "ysf": 12}
# simplex channels (tx == rx; cfw direct-mode framing): offsets from the first built-in channel
SIMPLEX = {"fm": 0, "dmr": 4, "p25": 7, "p25p2": 13, "dstar": 10, "nxdn": 11, "ysf": 12, "nxdn96": 14}
# per-radio identity, written to cfw debug symbols before PTT (g_dbg_my_id uint32, g_dbg_my_call char[9])
IDENT = {"A": (9990201, "XX0AAA"), "B": (9990202, "XX0BBB")}


def ident_steps(syms, who):
    rid, call = IDENT[who]
    if "g_dbg_my_id" not in syms or "g_dbg_my_call" not in syms:
        return []
    st = ["0|sysbus WriteDoubleWord 0x%08X %d" % (syms["g_dbg_my_id"][0], rid)]
    base = syms["g_dbg_my_call"][0]
    for i, c in enumerate(call.encode()[:8] + b"\0"):
        st.append("0|sysbus WriteByte 0x%08X %d" % (base + i, c))
    return st


# filter cases (cfw test symbols, uint16, write value + 1, applied to the current channel): (mode, symbol, TX value,
# RX value, expect decode). Wildcards: P25 NAC 0xF7E, NXDN RAN 0, YSF DG-ID 0 hear everything; D-STAR has no filter.
FILTERS = [
    ("dmr", "g_dbg_cc", 1, 2, False), ("dmr", "g_dbg_cc", 1, 1, True),
    ("p25", "g_dbg_nac", 0x293, 0x123, False), ("p25", "g_dbg_nac", 0x293, 0xF7E, True),
    ("nxdn", "g_dbg_ran", 1, 2, False), ("nxdn", "g_dbg_ran", 1, 0, True),
    ("ysf", "g_dbg_dgid", 20, 10, False), ("ysf", "g_dbg_dgid", 20, 0, True),
]


def expected_ids(mode, who):
    rid, call = IDENT[who]
    if mode == "fm":
        return []
    if mode in ("dstar", "ysf"):
        return [call]
    if mode in ("nxdn", "nxdn96"):
        return [str(rid & 0xFFFF)]
    return [str(rid)]


def nm(elf):
    out = {}
    for l in subprocess.check_output(["arm-none-eabi-nm", "-S", elf], text=True).splitlines():
        f = l.split()
        if len(f) >= 3:
            out.setdefault(f[-1], []).append(int(f[0], 16))
    return out


def keys(n):
    steps = []
    for d in str(n):
        steps += ["0|lcd Press %d 150" % (int(d) + 1), "0.35|shot"]
    return steps + ["0|lcd Press 12 150", "0.6|shot"]


def text_hook(elf_syms, path):
    a = elf_syms["gfx_text"][0] & ~1
    return ("0|cpu AddHook 0x%08X \"import System; p = self.GetRegister(2).RawValue; "
            "s = ''.join([chr(self.Bus.ReadByte(p + i)) for i in range(48)]).split(chr(0))[0]; "
            "System.IO.File.AppendAllText('%s', s + chr(10))\"" % (a, path))


def run(name, steps, seconds=4.0):
    d = os.path.join(OUT, name)
    if os.path.isdir(d):                                  # the same radio dir is reused as TX and RX: no stale shots
        for f in os.listdir(d):
            if re.match(r"(lcd_\d+\.(png|ppm)|then_out\.txt|rx_pcm\.(raw|wav))$", f):
                os.remove(os.path.join(d, f))
    args = [sys.executable, os.path.join(HERE, "run.py"), "--elf", ELF, "--seconds", str(seconds), "--name", name,
            "--timeout", "1500"]
    for s in steps:
        args += ["--then", s]
    subprocess.call(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    d = os.path.join(OUT, name)
    shots = sorted(f for f in os.listdir(d) if re.match(r"lcd_\d+\.png$", f))
    return d, shots


def tone_measure(x, fs):
    """Peak frequency (Hz, parabolic interpolation) and SNR (dB) of the strongest tone in 200..3800 Hz."""
    x = np.asarray(x, dtype=float)
    if len(x) < 256:
        return None, None, 0.0
    x = x - x.mean()
    w = np.hanning(len(x))
    X = np.abs(np.fft.rfft(x * w)) ** 2
    f = np.fft.rfftfreq(len(x), 1.0 / fs)
    band = (f >= 200) & (f <= 3800)
    k = int(np.argmax(np.where(band, X, 0)))
    if 0 < k < len(X) - 1:
        a, b, c = np.log(X[k - 1] + 1e-30), np.log(X[k] + 1e-30), np.log(X[k + 1] + 1e-30)
        p = 0.5 * (a - c) / (a - 2 * b + c) if (a - 2 * b + c) != 0 else 0.0
    else:
        p = 0.0
    fpk = (k + p) * fs / len(x)
    half = max(3, int(round(8.0 * len(x) / fs)))                     # +-8 Hz (+ window main lobe)
    sig = X[max(0, k - half):k + half + 1].sum()
    noise = X[band].sum() - sig
    snr = 10 * math.log10(sig / noise) if noise > 0 else 99.0
    rms = float(np.sqrt(np.mean(x ** 2)))
    return fpk, snr, rms


def parse_chan(spec):
    """'dmr=1409' or 'dmr=1409:1420' (A channel : B channel) -> dict mode -> (chA, chB)"""
    out = {}
    for item in filter(None, (spec or "").split(",")):
        m, v = item.split("=")
        a_, _, b_ = v.partition(":")
        out[m] = (int(a_), int(b_ or a_))
    return out


def link(case, a, syms, tone_addr, chT, chR, nameT, nameR, expect_decode=True, extra_T=(), extra_R=(), want_ids=(),
         neg_ids_ok=False, rf_gate=None):
    """Radio T transmits the test tone on channel chT (DAC capture); radio R receives the capture on chR."""
    dT, dR = os.path.join(OUT, nameT), os.path.join(OUT, nameR)
    if not a.skip_tx:
        print("[%s] TX radio %s: channel %d, tone %d Hz, PTT %.1f s" % (case, nameT, chT, a.tone, a.ptt), flush=True)
        os.makedirs(dT, exist_ok=True)
        cap = os.path.join(dT, "tx_ptt.adc16")
        for f in ("tx_ptt.adc16", "tx_ptt.json"):
            if os.path.exists(os.path.join(dT, f)):
                os.remove(os.path.join(dT, f))
        mic = ["0|spi2 MicHz %d" % a.tone] if a.mic else []       # SCT3288 microphone stand-in
        steps = keys(chT) + list(extra_T) + ["0|sysbus WriteWord 0x%08X %d" % (tone_addr, 0 if a.mic else a.tone)] + mic + ["0.2|shot",
                                             "0|pwm AutoCapture false", "0|pwm Capture @%s" % cap,
                                             '0|board Set "A10" false', "%.3f|shot" % min(1.0, a.ptt / 2),
                                             "0|rf0 Transmitting", "0|rf0 Status",
                                             "%.3f|shot" % max(0.0, a.ptt - min(1.0, a.ptt / 2)),
                                             '0|board Set "A10" true', "1.0|shot", "0|pwm Status",
                                             "0|pwm CaptureStop"]
        run(nameT, steps)
    jp = os.path.join(dT, "tx_ptt.json")
    meta = json.load(open(jp)) if os.path.exists(jp) else None
    if not meta or meta["samples"] < 1000:
        return {"case": case, "error": "TX radio: no PWM capture (see %s)" % dT}
    tx = np.fromfile(os.path.join(dT, meta["file"]), dtype="<u2").astype(float)
    dev = (tx - 2048.0) / 0.25
    tx_rms_hz = float(np.sqrt(np.mean((dev - dev.mean()) ** 2))) if len(tx) else 0.0
    thenT = open(os.path.join(dT, "then_out.txt")).read() if os.path.exists(os.path.join(dT, "then_out.txt")) else ""
    keyed = "> rf0 Transmitting\nTrue" in thenT
    m = re.search(r"> rf0 Status\ntuned (\d+) Hz", thenT)
    tx_hz = int(m.group(1)) if m else meta.get("rf_tx_reg_hz")
    # channel: idle padding, offset, noise
    fs = meta["rate"] or 48000.0
    pad = np.full(int(0.3 * fs), 2048.0)
    y = np.concatenate([pad, tx, pad]) + 0.25 * a.offset
    if a.snr is not None and tx_rms_hz > 0:
        sigma = 0.25 * tx_rms_hz / (10 ** (a.snr / 20.0))
        y = y + np.random.default_rng(1).normal(0, sigma, len(y))
    os.makedirs(dR, exist_ok=True)
    rx_in = os.path.join(dR, "rx_in.adc16")
    np.clip(np.round(y), 0, 4095).astype("<u2").tofile(rx_in)
    dur = len(y) / fs
    print("[%s] RX radio %s: channel %d, %.2f s into analog Stream" % (case, nameR, chR, dur), flush=True)
    tlog = os.path.join(dR, "text.log")
    if os.path.exists(tlog):
        os.remove(tlog)
    gate = a.rf_gate if rf_gate is None else rf_gate
    # RF gate: the signal is on the TX radio's frequency; the receiver hears it only if tuned there (else noise)
    rf = ["0|rf0 Signal %d -70" % tx_hz, "0|analog StreamGate true"] if (gate and tx_hz) else ["0|rf0 SignalHere -70"]
    spk = os.path.join(dR, "spk.raw")
    spk_steps = []
    if a.speaker:                                        # SCT3288 speaker stand-in, no PCM copy to the MCU
        spk_steps = ["0|sysbus WriteByte 0x%08X 0" % syms["g_sct_tap"][0], "0|spi2 SpeakerFile @%s" % spk]
    steps = keys(chR) + list(extra_R) + spk_steps + ["0|rf0 Status"] + rf + [text_hook(syms, tlog),
                                         "0|analog Stream @%s %d" % (rx_in, int(round(fs))),
                                         "%.3f|shot" % (dur / 2), "%.3f|shot" % (dur / 2 + 1.0), "0|analog StreamStatus", "0|spi2 VoiceStatus"]
    run(nameR, steps)
    thenR = open(os.path.join(dR, "then_out.txt")).read() if os.path.exists(os.path.join(dR, "then_out.txt")) else ""
    m = re.search(r"> rf0 Status\ntuned (\d+) Hz", thenR)
    rx_hz = int(m.group(1)) if m else None
    pcm_path = spk if a.speaker else os.path.join(dR, "rx_pcm.raw")
    pcm = np.fromfile(pcm_path, dtype="<i2") if os.path.exists(pcm_path) else np.zeros(0)
    f_hz, snr, rms = tone_measure(pcm, 8000.0)
    if len(pcm):
        import wave
        with wave.open(os.path.join(dR, "rx_pcm.wav"), "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(8000); w.writeframes(pcm.astype("<i2").tobytes())
    texts = open(tlog).read().splitlines() if os.path.exists(tlog) else []
    uniq = list(dict.fromkeys(t.strip() for t in texts if t.strip()))
    ids = [t for t in uniq if "<" in t or ">" in t]
    af = [t for t in uniq if re.search(r"AF\s*[\d.]+\s*Hz", t)]
    shotsT = sorted(f for f in os.listdir(dT) if re.match(r"lcd_\d+\.png$", f))
    shotsR = sorted(f for f in os.listdir(dR) if re.match(r"lcd_\d+\.png$", f))
    mont = os.path.join(OUT, "montage_%s.png" % case)
    if shotsT and shotsR:
        pick = shotsT[-3] if len(shotsT) >= 3 else shotsT[-1]
        subprocess.call([sys.executable, os.path.join(HERE, "montage.py"), mont,
                         "TX=" + os.path.join(dT, pick), "RX_mid=" + os.path.join(dR, shotsR[-2]),
                         "RX_end=" + os.path.join(dR, shotsR[-1])])
    decoded = f_hz is not None and abs(f_hz - a.tone) < a.tone_tol and len(pcm) >= 4000 and (snr or -99) > a.min_snr
    ids_ok = all(any(w in t for t in ids) for w in want_ids)
    ok = (decoded and ids_ok) if expect_decode else (not decoded and (neg_ids_ok or not ids))
    r = {"case": case, "tx": "%s ch%d" % (nameT, chT), "rx": "%s ch%d" % (nameR, chR), "rf_keyed": keyed,
         "tx_hz": tx_hz, "rx_hz": rx_hz, "freq_match": (tx_hz == rx_hz) if (tx_hz and rx_hz) else None,
         "tx_s": round(len(tx) / fs, 2), "tx_dev_rms_hz": round(tx_rms_hz), "rx_pcm_s": round(len(pcm) / 8000.0, 2),
         "tone_hz": None if f_hz is None else round(float(f_hz), 1), "snr_db": None if snr is None else round(float(snr), 1),
         "fw_meter": af[-1] if af else "", "ids": "; ".join(ids[-2:]),
         "expect": ("decode" + (" " + "/".join(want_ids) if want_ids else "")) if expect_decode else "no decode",
         "pass": ok, "montage": mont}
    print("  ->", r, flush=True)
    return r


def first_builtin():
    """First cfw built-in channel number on the default NAND image (run.py first_builtin)."""
    import run
    return run.first_builtin()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", default="dmr", help="|".join(MODES) + "|all (comma list ok)")
    ap.add_argument("--first", type=int, help="first built-in channel number (Info screen; default: run.first_builtin(), 1404 on extflash.pages, 9 on synth.pages)")
    ap.add_argument("--ptt", type=float, default=3.0, help="seconds of PTT on the transmitting radio")
    ap.add_argument("--tone", type=int, default=440)
    ap.add_argument("--snr", type=float, help="add AWGN at this SNR (dB, vs the TX signal's RMS deviation)")
    ap.add_argument("--offset", type=float, default=0.0, help="frequency offset in Hz (0.25 count/Hz)")
    ap.add_argument("--skip-tx", action="store_true", help="reuse the previous TX capture")
    ap.add_argument("--tag", default="", help="suffix for the out dirs/report (parallel runs)")
    ap.add_argument("--tone-symbol", default="s_tone_hz", help="firmware variable holding the test-tone frequency")
    ap.add_argument("--simplex", action="store_true",
                    help="simplex matrix: per mode A->B and B->A on the simplex channel (--chan), plus the negative "
                         "case (--neg: TX on a repeater channel, RX on the simplex channel must not decode)")
    ap.add_argument("--chan", help="channel numbers per mode, 'dmr=1430,fm=1431' or 'dmr=TX:RX' (overrides the table)")
    ap.add_argument("--neg", help="negative cases 'dmr=REPEATER_CH:SIMPLEX_CH,...' (expected: no decode)")
    ap.add_argument("--filters", action="store_true", help="filter matrix: DMR CC, P25 NAC, NXDN RAN, YSF DG-ID "
                    "mismatch (must not decode voice) and match/wildcard (must decode), on the simplex channels")
    ap.add_argument("--tone-tol", type=float, default=5.0,
                    help="pass if the tone is within this many Hz (SCT3288 emulator stand-in: 8 Hz steps, use ~45)")
    ap.add_argument("--rf-gate", action="store_true",
                    help="put the signal on the TX frequency: the receiver hears it only if tuned there")
    ap.add_argument("--talkaround", action="store_true",
                    help="talkaround cases on the DMR repeater channel (g_dbg_talkaround 1 on / 2 off), RF gate on: both = "
                         "decode, TX only = decode (sends on the output), RX only = no decode (TX on the input)")
    ap.add_argument("--min-snr", type=float, default=10.0, help="decoded tone must beat this SNR (dB)")
    ap.add_argument("--mic", action="store_true", help="no test tone: the tone comes from the SCT3288 microphone stand-in (--tone Hz)")
    ap.add_argument("--speaker", action="store_true", help="measure the SCT3288 speaker stand-in instead of the MCU's PCM ring (g_sct_tap 0)")
    ap.add_argument("--voc", type=int, help="vocoder backend g_voc_select on both radios: 0 auto, 1 sine, 2 imbe, 3 SCT3288")
    ap.add_argument("--elf", help="firmware ELF (default: make cfw, then cfw/build/cfw.elf); copied to out/ first")
    ap.add_argument("--extra-tx", action="append", default=[], help="extra --then step for the TX radio")
    ap.add_argument("--extra-rx", action="append", default=[], help="extra --then step for the RX radio")
    a = ap.parse_args()
    if a.first is None:
        a.first = first_builtin()
    modes = list(MODES) if a.mode == "all" else a.mode.split(",")
    global ELF
    if a.elf:
        ELF = os.path.abspath(a.elf)
    else:
        subprocess.check_call(["make", "-s", "-C", os.path.join(SRC, "cfw")], stdout=subprocess.DEVNULL)
    # snapshot: cfw/build may be rebuilt by another session mid-run (symbol addresses would move)
    snap = os.path.join(OUT, "txrx_elf%s.elf" % a.tag)
    import shutil
    shutil.copyfile(ELF, snap)
    ELF = snap
    syms = nm(ELF)
    for need in ("gfx_text", a.tone_symbol, "g_emu_pcm"):
        if need not in syms:
            raise SystemExit("cfw ELF lacks %s (digital TX / test tone / PCM ring not in this build?)" % need)
    if "g_backend_emusyms" in syms and (a.filters or a.talkaround or modes != ["fm"]):
        raise SystemExit("this is an EMU=1 ELF: its digital RX takes symbols from the emulator FIFO, not from the analog "
                         "stream, so every digital case would fail. Use a normal build (EMU=1 is ok for --mode fm only).")
    if len(syms[a.tone_symbol]) != 1:
        raise SystemExit("symbol %s is not unique: %s" % (a.tone_symbol, syms[a.tone_symbol]))
    tone_addr = syms[a.tone_symbol][0]
    if a.voc is not None:
        if "g_voc_select" not in syms:
            raise SystemExit("cfw ELF lacks g_voc_select")
        v = "0|sysbus WriteByte 0x%08X %d" % (syms["g_voc_select"][0], a.voc)
        a.extra_tx = [v] + a.extra_tx
        a.extra_rx = [v] + a.extra_rx
    table = SIMPLEX if a.simplex else MODES
    if a.mode == "all":
        modes = list(table)
    if (a.filters or a.talkaround) and a.mode == "dmr" and "--mode" not in sys.argv:
        modes = []                                         # --filters alone: only the filter cases
    chans = {m: (a.first + o, a.first + o) for m, o in table.items()}
    chans.update(parse_chan(a.chan))
    rows = []
    for mode in modes:
        if mode not in chans:
            rows.append({"case": mode, "error": "no channel for mode (use --chan %s=N)" % mode})
            continue
        chA, chB = chans[mode]
        A, B = "loop_%s%s_A" % (mode, a.tag), "loop_%s%s_B" % (mode, a.tag)
        if a.simplex:
            iA, iB = ident_steps(syms, "A"), ident_steps(syms, "B")
            rows.append(link("%s%s_AtoB" % (mode, a.tag), a, syms, tone_addr, chA, chB, A, B, True,
                             iA + a.extra_tx, iB + a.extra_rx, expected_ids(mode, "A")))
            rows.append(link("%s%s_BtoA" % (mode, a.tag), a, syms, tone_addr, chB, chA, B, A, True,
                             iB + a.extra_tx, iA + a.extra_rx, expected_ids(mode, "B")))
        else:
            rows.append(link("%s%s" % (mode, a.tag), a, syms, tone_addr, chA, chB, A, B, True, a.extra_tx, a.extra_rx))
    if a.filters:
        fmodes = None if a.mode == "all" or "--mode" not in sys.argv else set(a.mode.split(","))
        for i, (mode, sym, vt, vr, exp) in enumerate(FILTERS):
            if fmodes is not None and mode not in fmodes:    # --mode X --filters: only X's filter cases
                continue
            if sym not in syms:
                rows.append({"case": "%s_%s" % (mode, sym), "error": "ELF lacks %s" % sym})
                continue
            ch = a.first + SIMPLEX[mode]
            wr = lambda v: "0|sysbus WriteWord 0x%08X %d" % (syms[sym][0], v + 1)
            case = "%s%s_f%d_%s_%x_to_%x" % (mode, a.tag, i, sym.replace("g_dbg_", ""), vt, vr)
            rows.append(link(case, a, syms, tone_addr, ch, ch, "loop_%s%s_fT" % (mode, a.tag),
                             "loop_%s%s_fR" % (mode, a.tag), exp, ident_steps(syms, "A") + [wr(vt)] + a.extra_tx,
                             ident_steps(syms, "B") + [wr(vr)] + a.extra_rx, expected_ids(mode, "A") if exp else (),
                             neg_ids_ok=mode in ("nxdn", "nxdn96", "ysf")))
    if a.talkaround:
        rep = a.first + MODES["dmr"]
        if "g_dbg_talkaround" not in syms:
            rows.append({"case": "talkaround", "error": "ELF lacks g_dbg_talkaround"})
        else:
            ta = lambda v: "0|sysbus WriteByte 0x%08X %d" % (syms["g_dbg_talkaround"][0], v)
            # TX talkaround sends on the repeater output (= what a normal receiver listens to): heard. A normal TX
            # sends on the repeater input: a talkaround receiver (listening on the output) does not hear it.
            for case, vt, vr, exp in (("ta_both", 1, 1, True), ("ta_tx_only", 1, 2, True), ("ta_rx_only", 2, 1, False)):
                rows.append(link("dmr%s_%s" % (a.tag, case), a, syms, tone_addr, rep, rep,
                                 "loop_dmr%s_taT" % a.tag, "loop_dmr%s_taR" % a.tag, exp,
                                 ident_steps(syms, "A") + [ta(vt)] + a.extra_tx,
                                 ident_steps(syms, "B") + [ta(vr)] + a.extra_rx,
                                 expected_ids("dmr", "A") if exp else (), rf_gate=True))
    for mode, (rep, smp) in parse_chan(a.neg).items():
        rows.append(link("%s%s_neg" % (mode, a.tag), a, syms, tone_addr, rep, smp, "loop_%s%s_negT" % (mode, a.tag),
                         "loop_%s%s_negR" % (mode, a.tag), False, a.extra_tx, a.extra_rx))
    write_report(rows, a)
    return 0 if all(r.get("pass") for r in rows) else 1


def write_report(rows, a):
    p = os.path.join(OUT, "txrx_loop%s.md" % a.tag)
    with open(p, "w") as f:
        f.write("# cfw two-radio loopback, %d Hz test tone (SNR %s, offset %+.0f Hz%s%s)\n\n" %
                (a.tone, "off" if a.snr is None else "%.0f dB" % a.snr, a.offset, ", simplex" if a.simplex else "",
                 "" if a.voc is None else ", g_voc_select %d" % a.voc))
        f.write("| case | TX | RX | keyed | TX Hz | RX Hz | same freq | TX dev rms | RX audio s | tone Hz | SNR dB | "
                "firmware meter | IDs | expect | result |\n")
        f.write("|" + "---|" * 15 + "\n")
        for r in rows:
            if "error" in r:
                f.write("| %s |" % r["case"] + " |" * 13 + " %s |\n" % r["error"])
                continue
            f.write("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n" % (
                r["case"], r["tx"], r["rx"], "yes" if r["rf_keyed"] else "NO", r["tx_hz"], r["rx_hz"],
                {True: "yes", False: "NO", None: "?"}[r["freq_match"]], r["tx_dev_rms_hz"], r["rx_pcm_s"],
                r["tone_hz"], r["snr_db"], r["fw_meter"], r["ids"], r["expect"], "PASS" if r["pass"] else "FAIL"))
    print(open(p).read())
    print("report:", p)


if __name__ == "__main__":
    sys.exit(main())
