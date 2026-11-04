"""Validate an installed recorder's MP4 without using its muxer or decoder."""
import argparse
import json
import math
import re
import statistics
import struct
import subprocess
from pathlib import Path


def run(*args):
    return subprocess.run(args, check=True, text=True, capture_output=True).stdout


def check(path, seconds, audio, *, minimum_duration=False):
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError(f"No recording at {path}")
    probe = json.loads(run('ffprobe', '-v', 'error', '-show_streams', '-show_format', '-of', 'json', str(path)))
    streams = probe['streams']
    video = [s for s in streams if s['codec_type'] == 'video']
    sound = [s for s in streams if s['codec_type'] == 'audio']
    if len(video) != 1 or video[0]['codec_name'] != 'h264':
        raise ValueError('Expected one H.264 video track')
    if len(sound) != int(audio) or any(s['codec_name'] != 'aac' for s in sound):
        raise ValueError('Unexpected audio tracks or codec')
    duration = float(probe['format']['duration'])
    tolerance = min(3, seconds * 0.15)
    if not math.isfinite(duration) or duration < seconds - tolerance or (not minimum_duration and duration > seconds + 3):
        raise ValueError(f'Expected {seconds}s; got {duration}s')
    result = {'durationSeconds': duration, 'width': video[0]['width'], 'height': video[0]['height'], 'bytes': path.stat().st_size}
    if audio:
        start = lambda s: float(s['start_time'])
        end = lambda s: start(s) + float(s['duration'])
        for label, timestamp in [('startSkewSeconds', start), ('endSkewSeconds', end)]:
            delta = abs(timestamp(video[0]) - timestamp(sound[0]))
            if not math.isfinite(delta) or delta > 0.25:
                raise ValueError(f'{label} outside 250ms budget: {delta}')
            result[label] = delta
    # framemd5 reports decoded frames. A successful seek producing zero frames is not a pass.
    maps = ['-map', '0:v:0'] + (['-map', '0:a:0'] if audio else [])
    tail = run('ffmpeg', '-nostdin', '-v', 'error', '-xerror', '-sseof', '-1', '-i', str(path), *maps, '-t', '1', '-f', 'framemd5', '-')
    decoded = {line.split(',')[0].strip() for line in tail.splitlines() if line and not line.startswith('#')}
    if not {'0', *(['1'] if audio else [])} <= decoded:
        raise ValueError('Could not seek and decode the final second of every expected track')
    return result


def check_tones(path):
    # The diagnostic uses distinct 440/880 Hz sources. Stream metadata alone cannot
    # tell whether both made it through the mixer, particularly after pause/remux.
    result = {}
    for label, seek in [('begin', ['-ss', '0.4']), ('end', ['-sseof', '-0.8'])]:
        raw = subprocess.check_output(['ffmpeg', '-nostdin', '-v', 'error', *seek, '-i', str(path),
                                       '-map', '0:a:0', '-t', '0.5', '-ac', '1', '-ar', '8000', '-f', 's16le', '-'])
        samples = [v / 32768 for (v,) in struct.iter_unpack('<h', raw)]
        if len(samples) < 2000:
            raise ValueError(f'Not enough synthetic audio at {label}')
        rms = math.sqrt(sum(x * x for x in samples) / len(samples))
        for hz in (440, 880):
            step = math.tau * hz / 8000
            i = sum(x * math.cos(step * n) for n, x in enumerate(samples))
            q = sum(x * math.sin(step * n) for n, x in enumerate(samples))
            amplitude = 2 * math.hypot(i, q) / len(samples)
            if amplitude < max(0.01, 0.15 * rms):
                raise ValueError(f'Missing {hz} Hz synthetic audio input at {label}')
            result[f'{label}{hz}Hz'] = round(amplitude, 4)
    return result


def check_memory(log, seconds):
    samples = [(int(t), int(kb) / 1024) for t, kb in re.findall(
        r'synthetic elapsed=(\d+)s\s+VmRSS:\s*(\d+)\s*kB', log.read_text())]
    return memory_result(samples, seconds)


def memory_result(samples, seconds):
    warm = [mb for t, mb in samples if 60 <= t <= 180]
    tail = [mb for t, mb in samples if seconds - 120 <= t <= seconds + 30]
    if len(warm) < 5 or len(tail) < 5:
        raise ValueError('Missing sustained recorder RSS samples')
    start, end = statistics.median(warm), statistics.median(tail)
    if end - start > max(64, start * 0.20):
        raise ValueError(f'Recorder RSS grew from {start:.1f} to {end:.1f} MiB')
    return {'warmRssMiB': start, 'tailRssMiB': end, 'peakSampleMiB': max(mb for _, mb in samples)}


if __name__ == '__main__':
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('file', type=Path)
    cli.add_argument('--seconds', type=int, required=True)
    cli.add_argument('--audio', action='store_true')
    cli.add_argument('--synthetic-tones', action='store_true', help='Require both diagnostic audio inputs near the beginning and end')
    cli.add_argument('--rss-log', type=Path, help='Required for endurance checks of 30 minutes or longer')
    args = cli.parse_args()
    result = check(args.file, args.seconds, args.audio)
    if args.synthetic_tones:
        result['syntheticToneAmplitudes'] = check_tones(args.file)
    if args.seconds >= 1800:
        if args.rss_log is None:
            cli.error('--rss-log is required for endurance checks')
        result.update(check_memory(args.rss_log, args.seconds))
    print(json.dumps(result, indent=2))
