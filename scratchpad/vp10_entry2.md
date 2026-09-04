## RUN OF 2026-09-04 (fourteenth screen run, part 2) — §27.6's PANEL GROUNDS

Same run, same build, same scratch folder; item 1 was committed at `78fe492`
before this was started. Captures in `scratchpad/vp10/`.

### ITEM 2 — §27.6's four priority checks: **ALL FOUR PASS, on screen**

Each was read **twice**: from `QUILL_THEME_PROBE` (what the app *computed*) and
from the pixels (what it *drew*). The two agree everywhere. The panel grounds
below are the drawn pixels.

#### Check 1 — default install, Plain White, Settings open: **PASS**

`ThemeSource` Manual, `Theme` Dark, untouched; Plain White selected in the
Background row (the picker's own state confirms it); shell pinned dark.

```
probe   ground=#0F0E10 isDark=1   pageGround=#FCFCFC
        panel=#CACACA  panelIsDark=0  onPanel=#141414  panelSep=17.64
drawn   panel #CACACA      heading/label ink #141414     11.24:1
        muted caption ink #646464                         3.61:1
        page beside the panel #FCFCFC
```

**The text is there and it is near-black** — "Canvas", "Background", "Grid Type",
"Artboard", "Measurements" all plainly legible (`j02-settings-open.png`). This is
the check whose failure "looks like an empty panel, not like a bug", so the thing
looked for was *absent* text, and there is none absent.

#### Check 2 — the panel against the paper: **PASS**

`panelSep=17.64`, which is §27.6's "~17 L\* below `#FCFCFC`", and on screen it is
a plainly distinct light-grey plate on near-white paper. Not a near-black slab —
`PushGround`'s `SetGrounds` call is running.

#### Check 3 — switch to Darkprint WITHOUT touching Theme: **PASS**

The paper was changed **through the app's own picker, live, with Settings open**,
so this is the transition the old `SetGround` could not see and not a boot state.

```
Theme before   Settings.Theme=Dark   Ui.Theme=Dark
Theme after    Settings.Theme=Dark   Ui.Theme=Dark      <- untouched, both fields
probe          pageGround=#262B31  panel=#404143  panelIsDark=1  onPanel=#F2F2F2
drawn          panel #404143   ink #F2F2F2   9.13:1
               muted caption #A4A5A5          4.14:1
```

**And it is not a stale plate.** Run 11's warning was taken seriously: the panel
was read as it stood after the switch, and again after a forced rebuild (tab away
to Interaction and back). **Byte-identical on every measured region.** The panel
repaints on the paper change itself, not merely on a rebuild.
`p01-darkprint-asis.png`, `p02-darkprint-rebuilt.png`.

#### Check 4 — stock TextBox / Slider / ComboBox on white paper: **PASS, all three**

Measured against WinUI's own resource values composited over the `#CACACA`
panel, so each answer is decisive rather than an impression:

| control | drawn | WinUI LIGHT | WinUI DARK | verdict |
|---|---|---|---|---|
| TextBox, rest (Artboard H:) | **#EFEFEF**, ink #191919, 15.29:1 | `ControlFillColorDefault` white 0.70 → **#EFEFEF** | white 0.0605 → #CDCDCD | **light** |
| TextBox, focused (W:) | **#FFFFFF**, ink #000000, 21.00:1 | white | #1F1F1F | **light** |
| Slider track, unfilled (Stylus) | **#707070** | `ControlStrongFillColorDefault` black 0.4463 → **#707070** | white 0.5442 → #E7E7E7 | **light** |
| ComboBox (Gestures, Two-finger tap) | **#EFEFEF**, text #191919 | white 0.70 → **#EFEFEF** | white 0.0605 → #CDCDCD | **light** |

`PanelIsDark` is reaching `RequestedTheme`. No grey-on-grey fields.
`m01-canvas-collapsed.png`, `k02-stylus.png`, `n01-gestures.png`.

**One thing worth knowing for a future run:** most of the Settings panel's
toggles are **not** stock — `k01-toggle-off.png` is a filled grey track with a
white knob, which no stock `ToggleSwitch` state produces. Only the TextBox,
Slider and ComboBox above are actually WinUI's, so those are the only three that
answer check 4. Nothing else on the panel does.

#### Check 7 — the muted flag, on Blueprint and Brown Paper: **PASS, and thin**

```
Blueprint     panel #8CA4B8   caption ink #48535C   3.04:1     heading #141414  7.12:1
Brown Paper   panel #B1A091   caption ink #59514B   3.07:1     heading #141414  7.29:1
```

Both clear the 3.0 floor, by **0.04 and 0.07**, which is as thin as `4d846c9`'s
own arithmetic predicted (3.03). Both were re-measured after a forced rebuild and
came back identical.

**The human-legible confirmation the brief asked for**: read at 2x
(`cap-blueprint.png`, `cap-brown.png`), *"Standard paper or custom background
color?"* is comfortably readable on both, and clearly secondary to the near-black
"Background" above it — which is what a muted caption is for. It does not read as
too faint. **But 0.04 of margin is not margin**, and any future darkening of a
panel ground or lightening of `OnPanelMuted` will cross the floor without anyone
noticing; the number is worth a regression test rather than another screen run.

### A Windows notification toast landed on the screen mid-item, and how it was handled

While the Gestures tab was being captured, a **TikTok notification toast** from
the user's own desktop appeared bottom-right, over the pinned Quill window, and
was caught in `n01-gestures.png`.

**Injection stopped immediately and a 60 s track was taken before anything else**
— which is the rule this file already carries for an unexplained event:

```
1261 samples / 60 s   0 cursor changes   1 distinct position (2782,553 - where
                      the run left it)   0 idle resets   idle 81 s and rising
```

Then a read-only `EnumWindows` sweep: **no TikTok window existed at all.** The
only topmost windows were Quill's own `Pop-upHost` (the Settings panel) and
Quill itself. So it was a transient toast, which needs no user input to appear
and is gone by itself — and 81 s of monotonically rising idle says the last
input event on this desktop was the run's own. The toast region was re-sampled
afterwards and reads `#CACACA`, Quill's panel, on all five points.

**The contaminated capture was deleted, not committed** — it showed the user's
own notification — and the Gestures shot was retaken clean. This entry records
what happened instead of the pixels.

### Gates — still clean

* `library.json`: **53,582,459 bytes, SHA-256 `0C32CE6C…8E038A`, mtime
  2026-08-28 17:26:38.4039650 UTC** — re-checked after item 2, byte-identical.
* No `crash.log` in the scratch folder, still.
* `Settings.Theme` and `Ui.Theme` read `Dark` before and after every paper
  switch. `vp10_paper.py` and `vp10_dock.py` both assert Theme/ThemeSource never
  move and both refuse the real library path.
* **A trap worth restating, because this run hit it from the other side:** the
  scratch `library.json`'s `Paper` field still read empty while the app was
  plainly drawing Darkprint. The library is the mirror; the live state is
  `settings.json` plus the app itself, and `QUILL_THEME_PROBE` is the only
  honest reading of the derived palette.
* This file measured **wholly CRLF — 2,932 endings, 0 bare LF, 0 bare CR, 0 NUL,
  no BOM** — immediately before this write.

### Items 3 and 4 — NOT REACHED

§25.11's canvas rows and §28's three were not started. Nothing about either
should be inferred from this run.

