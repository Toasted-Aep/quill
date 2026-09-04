"""Set the SCRATCH page background to a flat colour (plain-colour page, Paper
null).  Used to put run 11's own #E10619 back so 28's cluster numbers are
directly comparable with theirs.  Scratch only; Theme/ThemeSource asserted."""
import json
import os
import sys

DATA = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp10data\library.json'
REAL = r'C:\Users\irony\Documents\Quill\library.json'
assert os.path.normcase(DATA) != os.path.normcase(REAL), 'refusing to touch the real library'

hexs = sys.argv[1] if len(sys.argv) > 1 else '#E10619'
b = open(DATA, 'rb').read()
assert not b.startswith(b'\xef\xbb\xbf')
lib = json.loads(b.decode('utf-8'))
assert lib.get('Theme') in (None, 'Dark'), 'Theme moved: %r' % lib.get('Theme')
assert lib.get('ThemeSource') in (None, 'Manual'), 'ThemeSource moved: %r' % lib.get('ThemeSource')

n = 0
for nb in lib['Notebooks']:
    for s in nb['Sections']:
        for p in s['Pages']:
            p['Paper'] = None
            p['Background'] = hexs
            n += 1
lib['DefaultBackground'] = hexs
lib['DefaultPaper'] = None
out = json.dumps(lib, indent=1).encode('utf-8')
assert not out.startswith(b'\xef\xbb\xbf')
open(DATA, 'wb').write(out)
print('background %s on %d pages (%d bytes)' % (hexs, n, len(out)))
