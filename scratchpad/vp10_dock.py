"""Set Library.DialAnchor in the SCRATCH library so the dial docks at the far
corner and the wheel's other half comes on screen.  ToolWheel.CurrentAnchor
parses this string by name.  Written with Python, no BOM (PowerShell's utf8
emits one and Quill's JSON reader chokes on it)."""
import json
import os
import sys

DATA = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp10data\library.json'
REAL = r'C:\Users\irony\Documents\Quill\library.json'
assert os.path.normcase(DATA) != os.path.normcase(REAL), 'refusing to touch the real library'

anchor = sys.argv[1] if len(sys.argv) > 1 else 'BottomRight'
b = open(DATA, 'rb').read()
assert not b.startswith(b'\xef\xbb\xbf')
lib = json.loads(b.decode('utf-8'))
old = lib.get('DialAnchor', '<absent>')
lib['DialAnchor'] = anchor
# 27.6 check 1 is the DEFAULT install.  Quill itself writes Theme/ThemeSource on
# its first save, at the MODEL DEFAULTS - that is the default install, and it is
# left exactly as the app wrote it.  Nothing here may change them.
assert lib.get('Theme') in (None, 'Dark'), 'Theme is not the model default: %r' % lib.get('Theme')
assert lib.get('ThemeSource') in (None, 'Manual'), 'ThemeSource moved: %r' % lib.get('ThemeSource')
out = json.dumps(lib, indent=1).encode('utf-8')
assert not out.startswith(b'\xef\xbb\xbf')
open(DATA, 'wb').write(out)
print('DialAnchor %s -> %s   (%d bytes)' % (old, anchor, len(out)))
