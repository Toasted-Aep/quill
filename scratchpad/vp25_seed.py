"""Run 25 / 49.8 - seed the page the REORDER check is measured on.

The commit 16a557a claims the draw path now honours LAYER ORDER, and 49 admits
that until it landed "reordering the stack moved the model and nothing on the
glass". Nothing in the UI can reorder a layer, so the two orders are seeded
here and the GLASS is compared between them.

The page is built so the answer is a measurement, not an impression:

  layer key 0   BLUE   #1B5FC1  bar y=400  x 200..700   opaque
  layer key 1   ORANGE #E07A1F  bar y=400  x 500..1000  opaque

They OVERLAP on x 500..700 and nowhere else. Whichever layer paints last owns
the overlap, so the BOUNDARY between the two colours moves from x=500 to x=700
when the list order flips - and the blue-only and orange-only stretches are the
built-in control: both bars must still be on the page in BOTH orders.

Every element keeps its LayerKey in both orders. Reordering is a LIST MOVE
(18.2); if any key moves, that is the defect 18.3 says the key exists to stop.

A second group carries DISTINCTIVE OWN OPACITIES for 49.3's round trip - none
of them 1, which is the only way that check can fail loudly:

  layer 1  green bar   y=560  opacity 0.31
  layer 0  purple bar  y=640  opacity 0.73
  layer 1  rectangle   y=700  opacity 0.46
  layer 1  text box    y=780

ActiveLayer is seeded to 1 so seam 5's stamping can be watched on the glass:
nothing in the app calls SetActiveLayer, but ActiveLayer is a PERSISTED field,
so seeding it is the only way to make new ink land anywhere but the base layer.

usage:  python vp25_seed.py AB|BA
"""
import io, json, os, sys, uuid

ROOT = r"C:\Users\irony\Downloads\Quill Gem - Fable\Quill"
P = os.path.join(ROOT, "scratchpad", "vp25data", "library.json")

TICKS = 639243044360369255
BLUE = "#1B5FC1"
ORANGE = "#E07A1F"
GREEN = "#2E8B4A"
PURPLE = "#7B3FA0"

RTF = (
    "{\\rtf1\\fbidis\\ansi\\ansicpg1252\\deff0\\nouicompat\\deflang2057"
    "{\\fonttbl{\\f0\\fnil\\fcharset0 Segoe UI;}{\\f1\\fnil Segoe UI;}}\r\n"
    "{\\colortbl ;\\red123\\green63\\blue160;}\r\n"
    "{\\*\\generator Riched20 3.1.0008}\\viewkind4\\uc1 \r\n"
    "\\pard\\sl300\\slmult1\\cf1\\f0\\fs24 LAYER ONE TEXT\\par\r\n}\r\n\x00"
)


def bar(y, colour, opacity, layer, x0, x1, size=40.0):
    s = {
        "Id": str(uuid.uuid4()),
        "Pen": 0,
        "Color": colour,
        "Size": size,
        "Sens": 1.0,
        "Points": [{"X": float(x), "Y": float(y), "Pressure": 0.85}
                   for x in range(x0, x1 + 1, 20)],
        "CreatedTicks": TICKS,
        "PressureCurve": None,
    }
    if opacity is not None:
        s["Opacity"] = opacity
    if layer:
        s["LayerKey"] = layer
    return s


order = (sys.argv[1] if len(sys.argv) > 1 else "AB").upper()
if order not in ("AB", "BA"):
    raise SystemExit("order must be AB (base first) or BA (base on top)")

d = json.load(io.open(P, encoding="utf-8"))
page = d["Notebooks"][0]["Sections"][0]["Pages"][0]

L0 = {"Key": 0, "Name": "", "Opacity": 1.0, "CreatedTicks": TICKS}
L1 = {"Key": 1, "Name": "", "Opacity": 1.0, "CreatedTicks": TICKS}
# AB = [0,1] -> layer 1 paints last -> ORANGE owns the overlap
# BA = [1,0] -> layer 0 paints last -> BLUE   owns the overlap
page["Layers"] = [L0, L1] if order == "AB" else [L1, L0]
page["ActiveLayer"] = 1

page["Strokes"] = [
    bar(400, BLUE, None, 0, 200, 700),
    bar(400, ORANGE, None, 1, 500, 1000),
    bar(560, GREEN, 0.31, 1, 200, 900, size=26.0),
    bar(640, PURPLE, 0.73, 0, 200, 900, size=26.0),
]

page["Shapes"] = [{
    "Id": str(uuid.uuid4()),
    "Kind": 0,
    "X": 200.0, "Y": 700.0, "W": 700.0, "H": 60.0,
    "Color": GREEN, "Size": 6.0, "Pen": 0, "Opacity": 0.46,
    "ImagePath": None, "EquationLatex": None,
    "AxisLabelX": None, "AxisLabelY": None, "AxisLabelZ": None,
    "Rotation": 0.0, "TRows": 0, "TCols": 0, "TColW": None, "TRowH": None,
    "FillColor": None, "BorderColor": None, "BorderWidth": None,
    "MergeColSpan": 1, "MergeRowSpan": 1, "HeaderRow": False,
    "LayerKey": 1, "CreatedTicks": TICKS,
}]

page["Texts"] = [{
    "Id": str(uuid.uuid4()),
    "X": 200.0, "Y": 790.0, "Width": 520.0,
    "WidthPinned": False, "MaxWidth": 520.0, "AutoWidth": True,
    "Rtf": RTF, "TextColor": PURPLE, "Rotation": 0.0,
    "TableId": None, "TableRow": 0, "TableCol": 0,
    "FillColor": None, "BorderColor": None, "BorderWidth": None,
    "CellColSpan": 1, "CellRowSpan": 1,
    "LayerKey": 1, "CreatedTicks": TICKS,
}]

page["Background"] = "#FCFCFC"
page["Grid"] = 0
d["StartOnGallery"] = False
d["LastPageId"] = page["Id"]

with io.open(P, "w", encoding="utf-8", newline="") as f:
    json.dump(d, f, ensure_ascii=False)

print("order   :", order, "->", [l["Key"] for l in page["Layers"]])
print("page    :", page["Id"])
print("strokes :", [(s.get("LayerKey", 0), s["Color"], s.get("Opacity"),
                     s["Points"][0]["X"], s["Points"][-1]["X"], s["Points"][0]["Y"])
                    for s in page["Strokes"]])
print("shapes  :", [(s.get("LayerKey", 0), s.get("Opacity")) for s in page["Shapes"]])
print("texts   :", [(t.get("LayerKey", 0), len(t["Rtf"])) for t in page["Texts"]])
print("active  :", page["ActiveLayer"])
print("bytes   :", os.path.getsize(P))
