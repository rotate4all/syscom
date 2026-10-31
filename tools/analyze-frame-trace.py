"""Summarises a frame trace recorded by trace-frames.ps1 (frame-trace/frames.xml, decoded by tracerpt).

For the app presenting the most frames it prints, second by second:
  rendered   frames the game renders itself (NVIDIA Reflex "simulation start" markers)
  presented  present calls (Microsoft-Windows-DXGI / D3D9), what SYSCOM shows as "Frame rate"
  flips      frames the graphics kernel put on the screen (all apps)
and the Reflex marker counts. With frame generation working, "presented" is a multiple of "rendered".

Usage: python analyze-frame-trace.py frame-trace/frames.xml
Standard library only. Reads the XML line by line, so a 600 MB file takes seconds.
"""
import collections
import re
import sys

PCL_MARKERS = {
    0: 'simulation start', 1: 'simulation end', 2: 'render submit start', 3: 'render submit end',
    4: 'present start', 5: 'present end', 7: 'trigger flash', 8: 'latency ping',
    9: 'out-of-band render submit start', 10: 'out-of-band render submit end',
    11: 'out-of-band present start', 12: 'out-of-band present end', 13: 'controller input sample',
    14: 'delta t calculation', 15: 'late warp present start', 16: 'late warp present end',
    17: 'camera constructed', 18: 'late warp submit start', 19: 'late warp submit end',
}
FLIP_EVENTS = {116, 259}   # DxgKrnl MMIOFlip, MMIOFlipMultiPlaneOverlay

rx_provider = re.compile(r'<Provider Name="([^"]*)"')
rx_event_id = re.compile(r'<EventID>(\d+)<')
rx_process = re.compile(r'ProcessID="(\d+)"')
rx_time = re.compile(r'SystemTime="[^T]*T(\d\d):(\d\d):([\d.]+)')
rx_data = re.compile(r'<Data Name="([^"]*)">([^<]*)<')


def events(path):
    provider = event_id = pid = when = None
    data = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if '<Provider ' in line:
                m = rx_provider.search(line)
                provider, data = (m.group(1) if m else '?'), {}
            elif '<EventID>' in line:
                event_id = int(rx_event_id.search(line).group(1))
            elif 'ProcessID=' in line:
                pid = int(rx_process.search(line).group(1))
            elif 'SystemTime=' in line:
                m = rx_time.search(line)
                when = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
            elif '<Data Name=' in line:
                m = rx_data.search(line)
                if m:
                    data[m.group(1)] = m.group(2).strip()
            elif '</Event>' in line:
                yield provider, event_id, pid, when, data


def main(path):
    per_second = collections.defaultdict(collections.Counter)   # second → (kind, pid) → count
    presents = collections.Counter()
    markers = collections.defaultdict(collections.Counter)      # pid → marker → count
    providers = collections.Counter()
    start = None
    for provider, event_id, pid, when, data in events(path):
        providers[provider] += 1
        if when is None:
            continue
        start = when if start is None else start
        second = int(when - start)
        if provider == 'Microsoft-Windows-DXGI' and event_id == 42 or provider == 'Microsoft-Windows-D3D9' and event_id == 1:
            presents[pid] += 1
            per_second[second][('presented', pid)] += 1
        elif provider.startswith('PCLStats'):
            marker = data.get('Marker')
            if marker is not None and marker.isdigit():
                markers[pid][int(marker)] += 1
                if int(marker) == 0:
                    per_second[second][('rendered', pid)] += 1
        elif provider == 'Microsoft-Windows-DxgKrnl' and event_id in FLIP_EVENTS:
            per_second[second][('flips', 0)] += 1

    print('Events per provider:')
    for name, n in providers.most_common():
        print(f'  {n:8d}  {name}')
    if not presents:
        print('\nNo present events: was a game running in front?')
        return
    game, _ = presents.most_common(1)[0]
    seconds = sorted(per_second)
    print(f'\nApp presenting the most frames: process {game}')
    print('second     ' + ' '.join(f'{s:5d}' for s in seconds))
    for kind, pid in (('rendered', game), ('presented', game), ('flips', 0)):
        print(f'{kind:10s} ' + ' '.join(f'{per_second[s][(kind, pid)]:5d}' for s in seconds))

    if markers.get(game):
        print('\nReflex markers of that app:')
        for marker, n in sorted(markers[game].items()):
            print(f'  {marker:3d} {PCL_MARKERS.get(marker, "?"):34s} {n:7d}')
        rendered = markers[game].get(0, 0)
        ratio = presents[game] / rendered if rendered else 0
        print(f'\nPresented / rendered: {ratio:.2f}'
              + ('  (no generated frames)' if 0.9 < ratio < 1.1 else '  (frame generation adds frames)' if ratio >= 1.1 else ''))
    else:
        print('\nNo Reflex markers from that app (no Reflex, or not enabled in the game).')


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
