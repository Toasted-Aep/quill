"""Set the SCRATCH page to a given paper, the way SetPagePaper would:
Paper = the texture id (null for the plain-colour papers) and Background = that
paper's own ground hex.  Plain White is Paper null + #FCFCFC.

Only ever touches scratchpad/vp10data.  Theme / ThemeSource are asserted
unchanged - 27.6 check 1 is the DEFAULT install and writing either would destroy
the case under test.  No BOM.
"""
import json
import os
import sys

DATA = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp10data\library.json'
REAL = r'C:\Users\irony\Documents\Quill\library.json'
assert os.path.normcase(DATA) != os.path.normcase(REAL), 'refusing to touch the real library'

# PaperGrain.GroundRgb, transcribed
GROUND = {
    '': '#FCFCFC',            # Plain White  - PaperKind.PlainWhite
    'transparent': '#F2F2F2',
    'crumpled': '#F0ECE3',
    'lightweight': '#F5F3EE',
    'heavyweight': '#E9E4D9',
    'rippled': '#F3F0E8',
    'blueprint': '#2E80C2',
    'brown': None,            # filled from source below
    'darkprint': None,
}

# read the two grounds not transcribed above straight out of PaperGrain.cs
import re
src = open(os.path.normpath(os.path.join(os.path.dirname(DATA), '..', '..',
           'src', 'Quill', 'Controls', 'PaperGrain.cs')), encoding='utf-8').read()
for key, kind in (('brown', 'BrownPaper'), ('darkprint', 'Darkprint')):
    m = re.search(r'PaperKind\.%s\s*=>\s*\(0x([0-9A-Fa-f]{2}),\s*0x([0-9A-Fa-f]{2}),\s*0x([0-9A-Fa-f]{2})\)' % kind, src)
    assert m, 'no ground found for ' + kind
    GROUND[key] = ('#%s%s%s' % (m.group(1), m.group(2), m.group(3))).upper()

paper = sys.argv[1] if len(sys.argv) > 1 else ''
assert paper in GROUND, 'unknown paper %r; known: %s' % (paper, sorted(GROUND))

b = open(DATA, 'rb').read()
assert not b.startswith(b'\xef\xbb\xbf')
lib = json.loads(b.decode('utf-8'))
assert lib.get('Theme') in (None, 'Dark'), 'Theme moved: %r' % lib.get('Theme')
assert lib.get('ThemeSource') in (None, 'Manual'), 'ThemeSource moved: %r' % lib.get('ThemeSource')

hexs = GROUND[paper]
n = 0
for nb in lib['Notebooks']:
    for s in nb['Sections']:
        for p in s['Pages']:
            p['Paper'] = paper or None
            p['Background'] = hexs
            n += 1
lib['DefaultBackground'] = hexs
lib['DefaultPaper'] = paper or None

out = json.dumps(lib, indent=1).encode('utf-8')
assert not out.startswith(b'\xef\xbb\xbf')
open(DATA, 'wb').write(out)
print('paper=%-11s ground=%s   %d pages   (%d bytes)'
      % (paper or '<plain>', hexs, n, len(out)))
print('grounds:', {k: v for k, v in GROUND.items() if k in ('', 'blueprint', 'brown', 'darkprint')})
