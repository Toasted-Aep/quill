"""Set the SCRATCH anchor's dial dock and live theme, for run 16's two checks.

Restating run 11's trap: the LIVE theme is NOT library.json's Theme/
ThemeSource - those are a stale mirror.  It is settings.json ->
Settings.Theme and Ui.Theme, and BOTH have to move.

DialAnchor lives in the library and ToolWheel.CurrentAnchor parses it by name.

Both files are written with Python and no BOM; PowerShell's Out-File/
Set-Content -Encoding utf8 emits one and Quill's JSON reader chokes on it.

Usage: python vp12_setup.py <anchor> <theme>
"""
import json
import os
import sys

ROOT = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp12data'
LIB = os.path.join(ROOT, 'library.json')
SET = os.path.join(ROOT, 'settings.json')
REAL_DIR = r'C:\Users\irony\Documents\Quill'
for p in (LIB, SET):
    assert os.path.normcase(os.path.dirname(p)) != os.path.normcase(REAL_DIR), \
        'refusing to touch the real data folder'

anchor = sys.argv[1] if len(sys.argv) > 1 else 'BottomRight'
theme = sys.argv[2] if len(sys.argv) > 2 else 'Light'

b = open(LIB, 'rb').read()
assert not b.startswith(b'\xef\xbb\xbf')
lib = json.loads(b.decode('utf-8'))
old = lib.get('DialAnchor', '<absent>')
lib['DialAnchor'] = anchor
out = json.dumps(lib, indent=1).encode('utf-8')
assert not out.startswith(b'\xef\xbb\xbf')
open(LIB, 'wb').write(out)
print('library  DialAnchor %s -> %s   (%d bytes)' % (old, anchor, len(out)))

if os.path.exists(SET):
    b = open(SET, 'rb').read()
    assert not b.startswith(b'\xef\xbb\xbf')
    s = json.loads(b.decode('utf-8'))
else:
    s = {}
before = (s.get('Settings', {}).get('Theme'), s.get('Ui', {}).get('Theme'))
s.setdefault('Settings', {})['Theme'] = theme
s.setdefault('Ui', {})['Theme'] = theme
out = json.dumps(s).encode('utf-8')
assert not out.startswith(b'\xef\xbb\xbf')
open(SET, 'wb').write(out)
after = (s['Settings']['Theme'], s['Ui']['Theme'])
print('settings Settings.Theme/Ui.Theme %s -> %s   (%d bytes)' % (before, after, len(out)))
print('ThemeSource stays', s['Settings'].get('ThemeSource'))
