"""
Procedural placeholder sound effects for Loamlight (numpy + the stdlib wave module).

Every sound is a few lines of synthesis (decaying oscillators, filtered noise, pitch sweeps) with a seeded RNG,
so the output is byte-identical on every run. Variants of one sound are written as <name>_<i>.wav; the Unity
importer groups them by name and picks a random variant (plus pitch jitter) per play.

  python Tools/audio/synth_sfx.py --out Assets/Audio/Generated
"""
import argparse
import os
import wave

import numpy as np

SR = 44100


def t_axis(dur):
    return np.arange(int(SR * dur)) / SR


def env(n, attack=0.002, decay=0.08):
    t = np.arange(n) / SR
    a = np.clip(t / max(attack, 1e-6), 0, 1)
    return a * np.exp(-t / max(decay, 1e-6))


def noise(rng, n):
    return rng.uniform(-1, 1, n)


def lowpass(x, cutoff):
    """One-pole low-pass, cutoff in Hz (array or scalar)."""
    y = np.zeros_like(x)
    c = np.broadcast_to(np.asarray(cutoff, dtype=np.float64), x.shape)
    a = 1 - np.exp(-2 * np.pi * c / SR)
    acc = 0.0
    for i in range(len(x)):
        acc += a[i] * (x[i] - acc)
        y[i] = acc
    return y


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def bandpass(x, lo, hi):
    return lowpass(highpass(x, lo), hi)


def sine(freq, dur, phase=0.0):
    t = t_axis(dur)
    f = np.broadcast_to(np.asarray(freq, dtype=np.float64), t.shape)
    return np.sin(2 * np.pi * np.cumsum(f) / SR + phase)


def norm(x, peak=0.85):
    m = np.max(np.abs(x)) or 1.0
    return x / m * peak


# ------------------------------------------------------------------------------------------------- sounds
def dig_soft(rng, v):
    dur = 0.16
    n = int(SR * dur)
    thud = sine(70 + 25 * v + 40 * np.exp(-t_axis(dur) * 40), dur) * env(n, 0.001, 0.05)
    grit = bandpass(noise(rng, n), 300, 1800 + 300 * v) * env(n, 0.001, 0.03)
    return norm(thud * 0.9 + grit * 0.8)


def dig_hard(rng, v):
    dur = 0.22
    n = int(SR * dur)
    click = highpass(noise(rng, n), 2500) * env(n, 0.0005, 0.008)
    ring = (sine(1700 + 230 * v, dur) * 0.5 + sine(2650 + 310 * v, dur) * 0.3) * env(n, 0.001, 0.05)
    body = bandpass(noise(rng, n), 600, 2400) * env(n, 0.001, 0.025)
    return norm(click * 0.8 + ring * 0.45 + body * 0.7)


def dig_wood(rng, v):
    dur = 0.18
    n = int(SR * dur)
    knock = sine(210 + 30 * v, dur) * env(n, 0.001, 0.04)
    tone = sine(520 + 40 * v, dur) * env(n, 0.001, 0.02)
    grit = bandpass(noise(rng, n), 400, 1400) * env(n, 0.001, 0.02)
    return norm(knock + tone * 0.5 + grit * 0.6)


def place(rng, v):
    dur = 0.12
    n = int(SR * dur)
    thump = sine(120 + 20 * v + 60 * np.exp(-t_axis(dur) * 60), dur) * env(n, 0.001, 0.035)
    tick = bandpass(noise(rng, n), 800, 3000) * env(n, 0.0005, 0.01)
    return norm(thump + tick * 0.5, 0.7)


def step(rng, v):
    dur = 0.07
    n = int(SR * dur)
    return norm(bandpass(noise(rng, n), 200, 1200 + 200 * v) * env(n, 0.001, 0.018), 0.5)


def jump(rng, v):
    dur = 0.16
    n = int(SR * dur)
    sweep = bandpass(noise(rng, n), 300, np.linspace(800, 3200, n)) * env(n, 0.01, 0.06)
    return norm(sweep, 0.45)


def land(rng, v):
    dur = 0.14
    n = int(SR * dur)
    thud = sine(60 + 15 * v, dur) * env(n, 0.001, 0.05)
    dust = bandpass(noise(rng, n), 200, 1500) * env(n, 0.001, 0.04)
    return norm(thud + dust * 0.7, 0.6)


def cant(rng, v):
    dur = 0.18
    n = int(SR * dur)
    clank = (sine(330, dur) + sine(495, dur) * 0.5 + sine(990, dur) * 0.2) * env(n, 0.001, 0.05)
    return norm(clank + highpass(noise(rng, n), 3000) * env(n, 0.0005, 0.005) * 0.6, 0.6)


def pickup(rng, v):
    dur = 0.12
    n = int(SR * dur)
    half = n // 2
    f = np.concatenate([np.full(half, 880.0 + 60 * v), np.full(n - half, 1320.0 + 60 * v)])
    tone = sine(f, dur) * env(n, 0.002, 0.06)
    return norm(tone, 0.4)


def swing(rng, v):
    dur = 0.2
    n = int(SR * dur)
    shape = np.sin(np.linspace(0, np.pi, n)) ** 2
    s = bandpass(noise(rng, n), 400, np.linspace(1200, 4000 + 300 * v, n)) * shape
    return norm(s, 0.5)


def ui_click(rng, v):
    dur = 0.05
    n = int(SR * dur)
    return norm(sine(1400 + 200 * v, dur) * env(n, 0.0005, 0.012), 0.35)


def door(rng, v, opening=True):
    dur = 0.28
    n = int(SR * dur)
    creak_f = np.linspace(260, 420, n) if opening else np.linspace(420, 230, n)
    creak = sine(creak_f, dur) * env(n, 0.02, 0.12) * (0.6 + 0.4 * np.sin(np.arange(n) / SR * 2 * np.pi * 38))
    knock = sine(140, dur) * env(n, 0.001, 0.03)
    thud = np.zeros(n)
    if not opening:
        k = int(SR * 0.2)
        thud[k:] = (sine(90, dur)[: n - k] * env(n - k, 0.001, 0.05)) + bandpass(noise(rng, n - k), 200, 900) * env(n - k, 0.001, 0.03) * 0.5
    return norm(creak * 0.35 + knock * (0.6 if opening else 0.2) + thud)


def chest(rng, v, opening=True):
    dur = 0.22
    n = int(SR * dur)
    latch = highpass(noise(rng, n), 2000) * env(n, 0.0005, 0.006)
    wood = sine(180 if opening else 150, dur) * env(n, 0.001, 0.05)
    clink = sine(1900, dur) * env(n, 0.001, 0.03)
    return norm(latch * 0.6 + wood + clink * 0.25, 0.6)


def craft(rng, v):
    dur = 0.35
    n = int(SR * dur)
    hits = np.zeros(n)
    for k, f in ((0.0, 1500), (0.09, 1850), (0.18, 2250)):
        i = int(SR * k)
        m = n - i
        hits[i:] += (sine(f, m / SR) * 0.5 + sine(f * 1.5, m / SR) * 0.25)[:m] * env(m, 0.001, 0.05)
    return norm(hits + bandpass(noise(rng, n), 1500, 5000) * env(n, 0.001, 0.02) * 0.3, 0.55)


def ui_open(rng, v):
    dur = 0.1
    n = int(SR * dur)
    return norm(sine(np.linspace(500, 900, n), dur) * env(n, 0.002, 0.04), 0.3)


def ui_close(rng, v):
    dur = 0.1
    n = int(SR * dur)
    return norm(sine(np.linspace(900, 500, n), dur) * env(n, 0.002, 0.04), 0.3)


SOUNDS = {
    "dig_soft": (dig_soft, 3), "dig_hard": (dig_hard, 3), "dig_wood": (dig_wood, 3), "dig": (dig_soft, 2),
    "place": (place, 2), "step": (step, 4), "jump": (jump, 1), "land": (land, 2), "cant": (cant, 1),
    "pickup": (pickup, 2), "swing": (swing, 3), "ui_click": (ui_click, 2),
    "door_open": (lambda r, v: door(r, v, True), 1), "door_close": (lambda r, v: door(r, v, False), 1),
    "chest_open": (lambda r, v: chest(r, v, True), 1), "chest_close": (lambda r, v: chest(r, v, False), 1),
    "craft": (craft, 1), "ui_open": (ui_open, 1), "ui_close": (ui_close, 1),
}


def write_wav(path, data):
    pcm = np.clip(np.round(data * 32767), -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", default="")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    count = 0
    for name, (fn, variants) in SOUNDS.items():
        if args.only and name != args.only:
            continue
        for v in range(variants):
            rng = np.random.default_rng(abs(hash_name(name)) * 31 + v)
            data = fn(rng, v)
            # 3 ms fade in/out to avoid clicks
            fade = int(SR * 0.003)
            data[:fade] *= np.linspace(0, 1, fade)
            data[-fade:] *= np.linspace(1, 0, fade)
            write_wav(os.path.join(args.out, f"{name}_{v}.wav"), data)
            count += 1
    print(f"[synth_sfx] {count} files -> {args.out}")


def hash_name(s):
    h = 2166136261
    for ch in s.encode():
        h = ((h ^ ch) * 16777619) & 0xFFFFFFFF
    return h


if __name__ == "__main__":
    main()
