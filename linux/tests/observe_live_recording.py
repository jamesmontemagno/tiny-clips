"""Observe and verify one human-authorized GNOME recording; never start, stop, or grant capture."""
import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path

from verify_recording import check, memory_result

APP = 'com.refractored.tinyclips.Devel'
TAKE = re.compile(r'^\.take-([0-9a-fA-F-]{36})$')


def app_pid():
    rows = subprocess.check_output(['flatpak', 'ps', '--columns=application,child-pid'], text=True)
    matches = [int(parts[1]) for row in rows.splitlines() if len(parts := row.split()) == 2 and parts[0] == APP and parts[1].isdigit()]
    if len(matches) != 1:
        raise ValueError('Open exactly one installed Tiny Clips preview before starting the observer.')
    return matches[0]


def start_time(proc):
    # Field 22; comm may itself contain whitespace or parentheses.
    return (proc / 'stat').read_text().rsplit(')', 1)[1].split()[19]


def take_names(directory):
    return {p.name for p in directory.iterdir() if TAKE.fullmatch(p.name) and not p.is_symlink() and p.is_dir()}


def observe(seconds):
    proc = Path('/proc') / str(app_pid())
    if (proc / 'exe').readlink().name != 'tinyclips-gnome':
        raise ValueError('The Flatpak child is not the capture process. Do not measure a wrapper or the synthetic test tool.')
    started = start_time(proc)
    directory = Path.home() / '.var' / 'app' / APP / 'data' / 'tinyclips' / 'videos'
    old = take_names(directory)
    print('Now start a NEW screen/window recording with microphone and system audio in Tiny Clips.', file=sys.stderr, flush=True)
    timeout = time.monotonic() + 180
    while True:
        fresh = take_names(directory) - old
        if fresh or time.monotonic() >= timeout:
            break
        time.sleep(0.5)
    if len(fresh) != 1:
        raise ValueError('Expected exactly one new take within three minutes. No capture was changed.')
    take = fresh.pop()
    output = directory / f'TinyClip-{TAKE.fullmatch(take)[1]}.mp4'
    beginning = time.monotonic()
    samples = []
    prompted = False
    while not output.is_file() or (directory / take).exists():
        elapsed = int(time.monotonic() - beginning)
        if start_time(proc) != started:
            raise ValueError('The capture process exited or restarted.')
        rss = re.search(r'^VmRSS:\s+(\d+)\s+kB$', (proc / 'status').read_text(), re.MULTILINE)
        if not rss:
            raise ValueError('Cannot measure capture-process RSS.')
        samples.append((elapsed, int(rss[1]) / 1024))
        if elapsed >= seconds + 3 and not prompted:
            print('Target duration reached. Stop and save in Tiny Clips now.', file=sys.stderr, flush=True)
            prompted = True
        if elapsed > seconds + 300:
            raise ValueError('No finalized MP4 observed. Check Tiny Clips; this observer does not stop or discard recordings.')
        time.sleep(5)
    result = check(output, seconds, audio=True, minimum_duration=True)
    result.update({'source': 'observed GNOME app take', 'file': output.name})
    if seconds >= 1800:
        result.update(memory_result(samples, seconds))
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seconds', type=int, default=1800)
    args = parser.parse_args()
    if not 1 <= args.seconds <= 86400:
        parser.error('Use a duration between 1 and 86400 seconds.')
    print(json.dumps(observe(args.seconds), indent=2))
