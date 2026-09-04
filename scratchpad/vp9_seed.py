"""Seed the scratch QUILL_DATA_FOLDER *before* the first launch, so
LibraryStore.MigrateFromLegacyIfNeeded() bails on File.Exists(FilePath) and
none of the user's real notebooks are ever copied in.

Page background is #FF00FF.  Run 10 lost two readings to #E10619, which is
R29 EXACTLY - a page-showing-through pixel was indistinguishable from a real
swatch.  vp9_pagecolour.py checked every one of the 360 palette hexes: #FF00FF
is in none of them and its nearest neighbour (RV06 #E55DB1) is 124 RGB units
away, the largest margin of any candidate tried.

Theme / ThemeSource are LEFT AT THE MODEL DEFAULTS (Dark / Manual) and no
settings.json is written, because 27.6 check 1 is specifically the DEFAULT
INSTALL: a pinned dark shell over a light page.  Writing either field would
destroy the case under test.

Written with Python, no BOM - PowerShell's Out-File/Set-Content -Encoding utf8
emits one and Quill's JSON reader chokes on it.
"""
import json, os, uuid, datetime

root = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill'
data = os.path.join(root, 'scratchpad', 'vp9data')
os.makedirs(data, exist_ok=True)

PAGE_BG = '#FF00FF'


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


lib = {
    "Notebooks": [{
        "Id": str(uuid.uuid4()),
        "Name": "VP9",
        "CreatedTicks": ticks(),
        "Color": "#D97757",
        "Sections": [{
            "Id": str(uuid.uuid4()),
            "Name": "Sweep",
            "CreatedTicks": ticks(),
            "Pages": [page("Page 1", PAGE_BG), page("Page 2", PAGE_BG)],
        }],
    }],
    "Folders": [],
    "Pens": [],
    "DefaultBackground": PAGE_BG,
    "DefaultGrid": 0,
    "DefaultGridSpacing": 32.0,
    "Language": "",
    "DefaultFont": "Lora",
    "DefaultFontSize": 16.0,
}

p = os.path.join(data, 'library.json')
b = json.dumps(lib, indent=1).encode('utf-8')
assert not b.startswith(b'\xef\xbb\xbf')
assert b'"Theme"' not in b, 'Theme must stay at the model default for 27.6 check 1'
open(p, 'wb').write(b)
print('wrote', p, len(b), 'bytes; first bytes', b[:12])
print('folder now holds:', sorted(os.listdir(data)))
