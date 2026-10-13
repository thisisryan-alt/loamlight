"""
Procedural music for Loamlight: three seamless loops, composed and synthesised with numpy from fixed seeds
(byte-identical every run), written as 22.05 kHz mono WAV:
  00_meadow.wav  day: D major pentatonic plucks over I-V-vi-IV pads, warm bass, soft hats (92 bpm)
  01_deep.wav    night and caverns: A minor drone, sparse bell tones through a long echo, slow heartbeat (68 bpm)
  02_warden.wav  boss: E minor driving bass ostinato, kick/snare, a square lead motif (138 bpm)

  python Tools/audio/synth_music.py --out Assets/Audio/Music
"""
import argparse
import os
import wave

import numpy as np

SR = 22050


def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12)


def env(n, a, d, sustain=0.0, release=0.0):
    t = np.arange(n) / SR
    e = np.minimum(1.0, t / max(a, 1e-4)) * (sustain + (1 - sustain) * np.exp(-t / max(d, 1e-4)))
    if release > 0:
        r = int(release * SR)
        if r < n:
            e[-r:] *= np.linspace(1, 0, r)
    return e


def osc(freq, n, kind="sine", phase=0.0):
    t = np.arange(n) / SR
    ph = 2 * np.pi * freq * t + phase
    if kind == "sine":
        return np.sin(ph)
    if kind == "tri":
        return 2 / np.pi * np.arcsin(np.sin(ph))
    if kind == "square":
        return np.sign(np.sin(ph)) * 0.6
    if kind == "saw":
        return 2 * ((freq * t + phase / (2 * np.pi)) % 1.0) - 1
    raise ValueError(kind)


def lowpass(x, cutoff):
    a = 1 - np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):
        acc += a * (x[i] - acc)
        y[i] = acc
    return y


def add(buf, sig, start):
    end = min(len(buf), start + len(sig))
    if start < len(buf):
        buf[start:end] += sig[: end - start]


def echo(x, delay_s, feedback, mix, wrap=True):
    """Feedback delay; wraps around the loop end so the tail continues seamlessly into the start."""
    d = int(delay_s * SR)
    y = x.copy()
    for _ in range(8):
        shifted = np.roll(y, d) if wrap else np.concatenate([np.zeros(d), y[:-d]])
        y = x + shifted * feedback
    return x * (1 - mix) + y * mix


def norm(x, peak=0.85):
    return x / (np.max(np.abs(x)) or 1) * peak


def meadow():
    rng = np.random.default_rng(1)
    bpm, bars = 92, 32
    beat = 60 / bpm
    n = int(bars * 4 * beat * SR)
    out = np.zeros(n)
    prog = [(62, [62, 66, 69]), (69, [69, 73, 76]), (71, [71, 74, 78]), (67, [67, 71, 74])]   # D A Bm G
    scale = [62, 64, 66, 69, 71, 74, 76, 78, 81]
    for bar in range(bars):
        root, chord = prog[bar % 4]
        t0 = int(bar * 4 * beat * SR)
        blen = int(4 * beat * SR)
        pad = sum(osc(midi(c - 12), blen, "sine") for c in chord) * env(blen, 0.6, 9, 0.7, 0.4) * 0.08
        add(out, pad, t0)
        bass = osc(midi(root - 24), blen, "sine") * env(blen, 0.02, 1.6, 0.35, 0.2) * 0.28
        add(out, bass, t0)
        # plucked arpeggio, eighth notes, chord tones with pentatonic passing notes
        for k in range(8):
            if rng.random() < 0.18:
                continue
            note = chord[k % 3] + (12 if k >= 4 else 0) if rng.random() < 0.75 else int(rng.choice(scale))
            ln = int(beat * 0.5 * SR * 1.8)
            p = osc(midi(note), ln, "tri") * env(ln, 0.004, 0.35) * 0.13
            add(out, p, t0 + int(k * beat * 0.5 * SR))
        # soft hats on the off-beats
        for k in range(4):
            ln = int(0.05 * SR)
            hat = np.diff(rng.uniform(-1, 1, ln + 1)) * env(ln, 0.001, 0.015) * 0.05
            add(out, hat, t0 + int((k + 0.5) * beat * SR))
    out = echo(out, beat * 0.75, 0.3, 0.25)
    return norm(out, 0.8)


def deep():
    rng = np.random.default_rng(2)
    bpm, bars = 68, 24
    beat = 60 / bpm
    n = int(bars * 4 * beat * SR)
    out = np.zeros(n)
    drone = (osc(midi(45), n, "sine") * 0.12 + osc(midi(52), n, "sine") * 0.07) * (0.75 + 0.25 * osc(0.07, n, "sine"))
    out += drone
    scale = [57, 60, 62, 64, 67, 69, 72, 74, 76]
    for bar in range(bars):
        t0 = int(bar * 4 * beat * SR)
        for k in range(rng.integers(1, 4)):
            note = int(rng.choice(scale))
            ln = int(2.5 * SR)
            mod = osc(midi(note) * 3.5, ln, "sine") * env(ln, 0.001, 0.4) * 2.0
            t = np.arange(ln) / SR
            bell = np.sin(2 * np.pi * midi(note) * t + mod) * env(ln, 0.003, 1.2) * 0.1
            add(out, bell, t0 + int(rng.integers(0, 4) * beat * SR))
        # heartbeat thump every bar
        for off in (0.0, 0.32):
            ln = int(0.25 * SR)
            thump = osc(55, ln, "sine") * env(ln, 0.002, 0.08) * 0.22
            add(out, thump, t0 + int(off * SR))
    out = echo(out, beat * 1.5, 0.5, 0.45)
    return norm(out, 0.75)


def warden():
    rng = np.random.default_rng(3)
    bpm, bars = 138, 32
    beat = 60 / bpm
    n = int(bars * 4 * beat * SR)
    out = np.zeros(n)
    roots = [40, 40, 36, 38]          # E E C D
    motif = [64, 67, 71, 69, 67, 66, 64, 62]
    for bar in range(bars):
        t0 = int(bar * 4 * beat * SR)
        root = roots[(bar // 2) % 4]
        for k in range(8):           # driving eighth-note bass
            ln = int(beat * 0.5 * SR)
            note = root + (12 if k % 4 == 3 else 0)
            b = lowpass(osc(midi(note), ln, "saw"), 900) * env(ln, 0.003, 0.18, 0.4) * 0.28
            add(out, b, t0 + k * ln)
        for k in range(4):           # kick on every beat, snare on 2 and 4
            ln = int(0.18 * SR)
            t = np.arange(ln) / SR
            kick = np.sin(2 * np.pi * (50 + 90 * np.exp(-t * 30)) * t) * env(ln, 0.001, 0.09) * 0.45
            add(out, kick, t0 + int(k * beat * SR))
            if k % 2 == 1:
                sn = lowpass(rng.uniform(-1, 1, ln), 3500) * env(ln, 0.001, 0.07) * 0.22
                add(out, sn, t0 + int(k * beat * SR))
        if bar % 8 >= 4:             # the lead motif enters every other phrase
            for k, note in enumerate(motif):
                ln = int(beat * 0.5 * SR * 0.9)
                lead = osc(midi(note + (0 if bar % 2 == 0 else 3)), ln, "square") * env(ln, 0.005, 0.25, 0.5) * 0.07
                add(out, lowpass(lead, 2600), t0 + int(k * beat * 0.5 * SR))
        if bar % 4 == 0:             # a dark stab at each phrase start
            ln = int(beat * 2 * SR)
            stab = sum(osc(midi(root + 24 + i), ln, "saw") for i in (0, 3, 7)) * env(ln, 0.01, 0.6) * 0.04
            add(out, lowpass(stab, 1600), t0)
    out = echo(out, beat * 0.75, 0.25, 0.18)
    return norm(out, 0.85)


def write(path, x):
    # 5 ms crossfade across the loop seam
    f = int(0.005 * SR)
    x[:f] = x[:f] * np.linspace(0, 1, f) + x[-f:] * np.linspace(1, 0, f)
    pcm = np.clip(np.round(x * 32767), -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    for name, fn in (("00_meadow", meadow), ("01_deep", deep), ("02_warden", warden)):
        x = fn()
        write(os.path.join(args.out, name + ".wav"), x)
        print(f"[synth_music] {name}: {len(x) / SR:.1f} s")


if __name__ == "__main__":
    main()
