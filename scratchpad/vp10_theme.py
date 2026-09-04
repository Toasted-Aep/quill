"""Set the LIVE theme in the SCRATCH settings.json.

The live theme is settings.json -> Settings.Theme and Ui.Theme.  library.json's
Theme / ThemeSource are a stale mirror and editing them does nothing - that trap
cost run 11 a before/after.  Both fields are written here, together.

Scratch only, and no BOM: PowerShell's Out-File -Encoding utf8 emits one and
Quill's JSON reader chokes on it.
"""
import json
import os
import sys

DATA = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill\scratchpad\vp10data\settings.json'
REAL = r'C:\Users\irony\Documents\Quill'
assert not os.path.normcase(DATA).startswith(os.path.normcase(REAL)), 'refusing the real data folder'

theme = sys.argv[1] if len(sys.argv) > 1 else 'Light'
b = open(DATA, 'rb').read()
assert not b.startswith(b'\xef\xbb\xbf')
d = json.loads(b.decode('utf-8'))
old = (d.get('Settings', {}).get('Theme'), d.get('Ui', {}).get('Theme'))
d.setdefault('Settings', {})['Theme'] = theme
d.setdefault('Ui', {})['Theme'] = theme
out = json.dumps(d, indent=1).encode('utf-8')
assert not out.startswith(b'\xef\xbb\xbf')
open(DATA, 'wb').write(out)
print('Settings.Theme %s -> %s ; Ui.Theme %s -> %s  (%d bytes)'
      % (old[0], theme, old[1], theme, len(out)))
