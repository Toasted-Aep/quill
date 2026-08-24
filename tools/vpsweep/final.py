import sys, os, json
sys.path.insert(0, r'C:\Users\irony\Downloads\Quill Gem - Fable\quill-sweep\tools')
import measure_vp as M

V = os.path.dirname(os.path.abspath(__file__))
base = os.path.join(V, 'BASELINE-nogrid.png')

ORDER = [
    ('1-Point', ['1 Point']),
    ('2-Point', ['2 Point', '1/2 Narrow', '1/4 Narrow', 'Side Narrow', '1/2 Wide',
                 '1/4 Wide', 'Side Wide', '1/2 Wide Below', 'Side Ultrawide']),
    ('3-Point', ['3 Point', '3/4 Narrow', '1/2 Narrow', '3/4 Wide', '1/4 Wide',
                 'Side Wide Below', '1/4 Wide Below', '3/4 Ultrawide Below',
                 '3/4 Ultrawide']),
]

rows = []
for lst, names in ORDER:
    for n in names:
        f = os.path.join(V, '%s - %s.png' % (lst, n.replace('/', '_')))
        r = M.measure(f, base)
        r['list'], r['preset'] = lst, n

        # Grade the evidence. A row is TRUSTED only when Concepts actually drew
        # a full-width horizon in frame (so the row-scan measured it rather than
        # a fit inferring it) AND both vanishing points carry real support.
        scan_ok = r['horizon_scan'] is not None
        vps = r['vps']
        need = 1 if lst == '1-Point' else 2
        support_ok = len(vps) >= need and all(v['nlines'] >= 15 for v in vps)
        dots = sum(1 for v in vps + r['vps_off_horizon']
                   if v.get('dot') and v['dot']['dist_px'] <= 8)
        r['trusted'] = bool(scan_ok and support_ok)
        r['dot_confirmed'] = dots
        rows.append(r)

        mark = 'OK ' if r['trusted'] else '?? '
        print('%s%-8s %-22s hor=%+.4f %-11s tilt=%-6s vps=%s dots=%d'
              % (mark, lst, n, r['horizon_frac'],
                 'row-scan' if scan_ok else 'INFERRED',
                 r['horizon_tilt_deg'],
                 ', '.join('%+.4f(%d)' % (v['x_frac'], v['nlines']) for v in vps),
                 dots))
        if r['vps_off_horizon']:
            print('        off-horizon: %s'
                  % ', '.join('%+.4f@y%+.4f(%d)' % (v['x_frac'], v['y_frac'], v['nlines'])
                              for v in r['vps_off_horizon'][:2]))
        sys.stdout.flush()

with open(os.path.join(V, 'out', 'final.json'), 'w') as fh:
    json.dump(rows, fh, indent=2)
t = sum(1 for r in rows if r['trusted'])
print('\n%d of %d rows trusted' % (t, len(rows)))
