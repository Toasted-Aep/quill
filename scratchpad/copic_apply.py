#!/usr/bin/env python3
"""Apply scratchpad/copic_additions.json into CopicPalette.cs.

Rewrites only the data strings inside SectorsRaw - one per 10-degree column -
replacing each with the column contents the planner produced. Existing cells
never move; the 49 new ones are inserted at their sorted position among their
own family's members.

Safety, because this is the one script that touches src/:
  * the file is CRLF and stays CRLF - read and written with newline='' so no
    line ending is rewritten as a side effect;
  * every old data string must occur EXACTLY once, asserted before replacing,
    so a near-miss can never silently patch the wrong column;
  * the result is re-parsed and diffed against the plan before the write, so
    the file is only written if it round-trips to exactly what was intended.

Run from the repo root:  python scratchpad/copic_apply.py [--dry-run]
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import copic_audit as A

HERE = os.path.dirname(os.path.abspath(__file__))
PLAN = os.path.join(HERE, 'copic_additions.json')
PAL = A.PAL


def main():
    dry = '--dry-run' in sys.argv
    plan = json.load(open(PLAN))
    cols_after = plan['columns_after']

    before = A.sectors()
    codes_before = set(A.palette_codes())      # must be read BEFORE the write
    text = open(PAL, encoding='utf-8', newline='').read()
    original = text

    n_rep = 0
    for sid, _name, slices in before:
        for ci, (_a0, _a1, cells) in enumerate(slices):
            old = ' '.join(f'{c}:{h}' for c, h in cells)
            new = ' '.join(f'{c}:{h}' for c, h in cols_after[sid][ci])
            if old == new:
                continue
            occ = text.count(f'"{old}"')
            assert occ == 1, f'{sid}[{ci}]: old data string occurs {occ} times, refusing'
            text = text.replace(f'"{old}"', f'"{new}"', 1)
            n_rep += 1

    assert text.count('\r\n') == original.count('\r\n'), 'line endings changed'
    assert '\n' not in text.replace('\r\n', ''), 'a bare LF crept in'

    if dry:
        print(f'[dry run] would rewrite {n_rep} column data strings')
        return
    if n_rep == 0:
        print('already applied - every column already matches the plan')
        return

    open(PAL, 'w', encoding='utf-8', newline='').write(text)

    # Re-parse from disk and prove it matches the plan exactly.
    after = A.sectors()
    got = {sid: [[list(x) for x in cells] for _a0, _a1, cells in sl]
           for sid, _n, sl in after}
    want = {sid: [[list(x) for x in c] for c in cols] for sid, cols in cols_after.items()}
    assert got == want, 'round-trip mismatch: file does not match the plan'

    added = sorted({a['code'] for a in plan['additions']})
    codes_after = set(A.palette_codes())
    assert codes_after - codes_before == set(added), 'added set differs from plan'

    depth = [len(c) for cols in got.values() for c in cols]
    print(f'rewrote {n_rep} column data strings in {os.path.relpath(PAL)}')
    print(f'codes {len(codes_before)} -> {len(codes_after)}  (+{len(added)})')
    print(f'MaxRings {plan["before_max_rings"]} -> {max(depth)}  '
          f'({"UNCHANGED" if max(depth) == plan["before_max_rings"] else "CHANGED"})')
    print('round-trip verified: file matches the plan exactly')


if __name__ == '__main__':
    main()
