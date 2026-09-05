"""Seed the scratch QUILL_DATA_FOLDER *before* the first launch, so
LibraryStore.MigrateFromLegacyIfNeeded() bails on File.Exists(FilePath) and
none of the user's real notebooks are ever copied in.

Run 15's brief is 29's ten tool seats, which are specifically about a FLAT
BLACK page, so page 1 is #000000 with no paper.  The other grounds 29.6 asks
for are seeded as their own pages rather than driven through the Settings
picker, because Paper is a per-page field and switching page is one tap:

    1  black          Background #000000   Paper null      <- the reported case
    2  darkprint      Paper "darkprint"                    <- 29.5's mover
    3  blueprint      Paper "blueprint"                    <- did NOT move
    4  brown paper    Paper "brown"                        <- did NOT move
    5  plain white    Background #FCFCFC   Paper null      <- must be untouched

Theme / ThemeSource are LEFT OUT so the library keeps the model defaults; the
LIVE theme is settings.json (Settings.Theme / Ui.Theme) and is set separately,
per run 11's trap.

Written with Python, no BOM - PowerShell's Out-File/Set-Content -Encoding utf8
emits one and Quill's JSON reader chokes on it.
"""
import json, os, uuid, datetime

root = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill'
data = os.path.join(root, 'scratchpad', 'vp11data')
os.makedirs(data, exist_ok=True)


def ticks():
    epoch = datetime.datetime(1, 1, 1)
    now = datetime.datetime.utcnow()
    return int((now - epoch).total_seconds() * 10_000_000)


def page(name, bg, paper=None):
    p = {
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
    if paper is not None:
        p["Paper"] = paper
    return p


pages = [
    page("Black", "#000000"),
    page("Darkprint", "#262B31", "darkprint"),
    page("Blueprint", "#2E80C2", "blueprint"),
    page("Brown", "#A9713F", "brown"),
    page("PlainWhite", "#FCFCFC"),
]

lib = {
    "Notebooks": [{
        "Id": str(uuid.uuid4()),
        "Name": "VP11",
        "CreatedTicks": ticks(),
        "Color": "#D97757",
        "Sections": [{
            "Id": str(uuid.uuid4()),
            "Name": "Seats",
            "CreatedTicks": ticks(),
            "Pages": pages,
        }],
    }],
    "Folders": [],
    "Pens": [],
    "DefaultBackground": "#000000",
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
