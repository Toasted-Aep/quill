import sys, os
sys.path.insert(0, r'C:\Users\irony\Downloads\Quill Gem - Fable\quill-sweep\tools')
import measure_vp as M

V = os.path.dirname(os.path.abspath(__file__))
base = os.path.join(V, 'BASELINE-nogrid.png')

for name in sys.argv[1:]:
    p = os.path.join(V, name + '.png')
    mask, shape = M.build_mask(p, base)
    h, w = shape
    lines = M.peak_lines(M.hough(mask))
    scan = M.horizon_scan(mask)
    pts = M.ransac_vps(lines, shape)
    print('=== %s   lines=%d  scan_horizon=%s' % (
        name, len(lines), ('%.1f' % scan['y']) if scan else 'off-frame'))
    for q in pts:
        print('    n=%3d  x=%10.1f y=%10.1f   x_frac=%8.4f y_frac=%8.4f'
              % (q['n'], q['x'], q['y'], q['x'] / w, q['y'] / h))
    print()
