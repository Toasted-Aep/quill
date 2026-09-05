"""Seed the scratch QUILL_DATA_FOLDER *before* the first launch, so
LibraryStore.MigrateFromLegacyIfNeeded() bails on File.Exists(FilePath) and
none of the user's real notebooks are ever copied in.

Run 16's brief is two checks left over from S30 (b8e3512):

    1  the COPIC wheel's flipped upper-half labels (bottom dock)
    2  the BottomMenu pill under Theme = Light, on a live page

Run 15 used a red page with the mouse tool for the BottomMenu check, so page 1
here is that same red, #E10619.  A black page is added too (panel #343434,
clearly not the stale gallery #C8C8C6) in case the red page's panel colour
ever reads ambiguous next to a live measurement.

Theme / ThemeSource are LEFT OUT so the library keeps the model defaults; the
LIVE theme is settings.json (Settings.Theme / Ui.Theme), set separately by
vp12_setup.py, per run 11's trap.

Written with Python, no BOM - PowerShell's Out-File/Set-Content -Encoding utf8
emits one and Quill's JSON reader chokes on it.
"""
import json, os, uuid, datetime

root = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill'
data = os.path.join(root, 'scratchpad', 'vp12data')
os.makedirs(data, exist_ok=True)


def ticks():
    epoch = datetime.datetime(1, 1, 1)
    now = datetime.datetime.utcnow()
    return int((now - epoch).total_seconds() * 10_000_000)


def page(name, bg):
    return {
        "Id": str(uuid.uuid4()),
        "Name": name,
        "CreatedTicks": ticks(),
        "ViewX": 0.0, "ViewY": 0.0, "ViewZoom": 1.0,
        "Background": bg,
        "Grid": 0,
        "GridSpacing": 32.0,
        "GridOpacity": 1.0,
        "Strokes": [],
    }


pages = [
    page("Red", "#E10619"),
    page("Black", "#000000"),
]

lib = {
    "Notebooks": [{
        "Id": str(uuid.uuid4()),
        "Name": "VP12",
        "CreatedTicks": ticks(),
        "Color": "#D97757",
        "Sections": [{
            "Id": str(uuid.uuid4()),
            "Name": "Run16",
            "CreatedTicks": ticks(),
            "Pages": pages,
        }],
    }],
    "Folders": [],
    "Pens": [],
    "DefaultBackground": "#E10619",
    "DefaultGrid": 0,
    "DefaultGridSpacing": 32.0,
    "Language": "",
    "DefaultFont": "Lora",
    "DefaultFontSize": 16.0,
}

p = os.path.join(data, 'library.json')
b = json.dumps(lib, indent=1).encode('utf-8')
assert not b.startswith(b'\xef\xbb\xbf')
assert b'"Theme"' not in b, 'Theme must stay at the model default; settings.json is the live one'
open(p, 'wb').write(b)
print('wrote', p, len(b), 'bytes; first bytes', b[:12])
print('folder now holds:', sorted(os.listdir(data)))
