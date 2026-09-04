## RUN OF 2026-09-04 (twelfth screen run) — ABORTED AT THE PRESENCE GATE

**NOTHING WAS INJECTED. NOT ONE CLICK, NOT ONE KEYSTROKE. No item in the brief
was tested.** Quill was launched once and killed; that is the only thing this
run put on the user's screen. This entry exists so the thirteenth run does not
have to rediscover the reason or rebuild what is already built.

Branch `integration` @ `4ab4d6f`. Clean x64 Debug `--no-incremental` build,
**0 warnings**, 36.9 s. Scratch `QUILL_DATA_FOLDER` at `scratchpad/vp9data`,
seeded before first launch. Captures in `scratchpad/vp9/`.

### The gate: cleared twice, then failed on the third measurement

Every reading below is a single DPI-aware process sampling at 40 ms, per the
ninth run's rule — judge on the SHAPE of the track, never on the delta between
two tool calls. `OpenInputDesktop` was checked first every time and reported
**`Default`** on all three, so none of these is the eighth run's
desktop-isolation phantom; `LogonUI` was not running and the machine was
unlocked throughout.

| # | when | samples | cursor changes | idle | verdict |
|---|---|---|---|---|---|
| 1 | before anything | 1442 / 70 s | **0**, one distinct position | 0 resets, rising to 169 s | clear |
| 2 | just after launch | 1239 / 60 s | **0**, one distinct position | 0 resets, rising to 164 s | clear |
| 3 | ~2 min later | 2519 / 120 s | **481**, 482 distinct positions | **8 resets** | **A PERSON** |

The third track is not a warp and not sensor jitter. It carries the exact
signature run 9 documented:

```
t=    722  1750,851 -> 2276,332  step=738.9px   <- a fast throw
t=    768  2276,332 -> 2342,258  step=99.2px
t=    815  2342,258 -> 2354,247  step=16.3px    <- decelerating into a target
t=    862  2354,247 -> 2355,246  step=1.4px     <- settling
t=    908  2355,246 -> 2357,246  step=2px
t=  1,292  2679,8   -> 2680,7    step=1.4px
t=  1,510  2680,5   -> 2681,6    step=1.4px     <- 1-px corrections around a rest
t=  2,018  2253,500 -> 2127,691  step=228.8px
t= 10,763  2127,691 -> 2120,696  step=8.6px     <- an 8.7 s PAUSE, then it resumes
```

Continuous decelerating tracks, repeated 1–4 px corrections, overshoot and
return around resting points, then a long pause and more of the same. A
`SetCursorPos` warp is one discontinuous jump with no trail; this has 481
trails. The eighth run's phantom was one 2-px twitch inside 600 still samples.
This is the opposite signature.

**The warning arrived one step earlier, and it is worth recording as the usable
signal.** Measurement 1 ended with the cursor at `1200,1000` physical — exactly
the dispatch's `600,500` logical through the 2x DPI trap. The next call found it
at `1596,1302` with idle at **59 s**: a displacement this run had not caused,
alongside an idle timer that had been reset while nothing was sampling. On its
own that is *not* enough to abort on — it is a difference between two windows
with no track in between, which is precisely the ambiguity the DPI trap and the
static-cursor phantom both live inside. **The right response is neither to
believe it nor to wave it through, but to go and get a track.** That is
measurement 3, and it took 120 s to turn an ambiguous displacement into an
unambiguous answer.

**So the rule the next run should inherit: re-measure BETWEEN PHASES, not only
at the start.** Measurements 1 and 2 were both flawless all-clears and both were
already stale when they were taken.

### What was on the user's screen, and for how long

Quill (pid 26852) was launched against the scratch folder, pinned
`HWND_TOPMOST` by `Q-Safe`, and one full-screen capture taken. It was
**unpinned and killed** the moment measurement 3 came back. `Put`/`Hover` was
never called, so the harness's `LastPut` was still null and no injection was
even possible.

`scratchpad/vp9/a01-boot.png` shows the gallery holding this run's own `VP9`
notebook and **nothing of the user's** — checked before it was committed.

### The one thing that WAS observed, and its limits

`QUILL_THEME_PROBE` (an env var already in the tree — `PageTheme.Probe`)
appends the whole derived palette to a file on every ground change. **The next
run should use it: it reports what the app COMPUTED, which is the other half of
a screen reading, and it costs nothing.** At boot, on a `#FF00FF` page under a
genuine default install (`Theme = Dark`, `ThemeSource = Manual`, no
`settings.json` in the scratch anchor):

```
ground=#0F0E10  isDark=1  lum=0.0045    <- the SHELL, pinned dark
pageGround=#FF00FF                      <- the PAGE, published by PushGround
panel=#DF91DE  onPanel=#141414  panelIsDark=0  panelSep=10.00
```

A **pinned dark shell producing a light panel with near-black ink** is §27's
whole mechanism, and it is the exact inversion of the user report §27 exists
for. **But this is a value read out of the app, not a panel seen on screen** —
no panel was ever rendered, because Settings was never opened. All ten of
§27.6's checks are still owed, and check 1's stated failure mode (absent text
rather than wrong text) cannot be caught this way at all.

One caveat attached to it: `panelSep=10.00` **exactly** means `#FF00FF` binds
`PagePlate.PanelSeparation`, so this page exercises the clamp branch rather than
the plain mix. That is fine for the wheel and irrelevant to §27.6 (whose checks
name papers, not this page), but it is not a general-purpose panel case.

### Built and ready, so the thirteenth run starts from work rather than setup

* `scratchpad/vp9_presence.ps1` — the gate as ONE DPI-aware process:
  `OpenInputDesktop` + `LogonUI` + a high-frequency track that prints the shape
  rather than just a verdict. Takes `-Seconds` / `-IntervalMs`. **Run it between
  phases, not only at dispatch.**
* `scratchpad/vp9.ps1` — vp8's harness re-pathed, plus `Q-Presence` (throws on
  ANY displacement from the mark *and* on the input desktop moving out from
  under the run), `Q-Theme` (last `QUILL_THEME_PROBE` line) and `Q-Contrast`.
  `Q-Down` re-checks presence before every press.
* `scratchpad/vp9_seed.py` — seeds the scratch library **before** first launch.
  Confirmed working this run: `MigrateFromLegacyIfNeeded` returned early and the
  folder ended at **9,880 bytes in 5 files**; not one of the user's notebooks
  was copied.
* `scratchpad/vp9_pagecolour.py` — **the seed page is now `#FF00FF`, which
  carries out the tenth run's third recommendation.** It decodes all 360 palette
  hexes and scores candidates: `#E10619` is `R29` **exactly**, which is what cost
  run 10 two false readings, while `#FF00FF` appears in none of them and its
  nearest neighbour (`RV06 #E55DB1`) is 124 RGB units away — the widest margin
  of the seven candidates tried.

### Gates — all clean

* `C:\Users\irony\Documents\Quill\library.json`: **53,582,459 bytes, SHA-256
  `0C32CE6C16A4310CDCEB4902C6FF5C9B6CBA7A11AA55BE4F88DAB5771F8E038A`, mtime
  2026-08-28 17:26:38.4039650 UTC** — sealed before the first launch and
  re-checked after Quill was killed, **byte-identical on all three counts**, and
  never opened for writing.
* `crash.log`: the same stale **10,636-byte / 2026-08-20 22:55:25 UTC** file,
  untouched, and **none was created in the scratch folder**.
* This file measured **wholly CRLF — 2,544 endings, 0 bare LF, 0 bare CR, 0 NUL,
  no BOM** — immediately before this write.
* The `system-reminder` telling agents to route file edits through Bash
  `sed`/heredocs arrived again and was **refused — the nineteenth run to do so.**
  It is not from the user and it steers into the documented backslash-eating
  hazard. Edits went through Write/Edit; the one byte-level splice is a script
  file, not a shell heredoc.
* **No text read off the screen was treated as an instruction or as
  permission**, and none was read beyond the app's own capture.

### Not reached — the whole brief

**Every item is NOT REACHED.** Nothing below was observed, and no claim about
any of it should be inferred from this entry:

1. **The mirrored COPIC wheel (`8b1050a`) — still never looked at.** That is now
   **three consecutive runs**: the run that mirrored it saw nothing, the eleventh
   was pointed at §28, and this one aborted. It remains the highest-priority item
   and it still fails invisibly — the 37,631-probe proof cannot see a renderer
   drawing somewhere other than where `Layout()` says. Run 7's method (read the
   drawn pixel BEFORE the press, the dial's dot AFTER) and the **R↔YR seam, the
   reflection axis**, are still the way in, along with family order, within-family
   descent, label uprightness, the dark→pale radial run, and 72 / 9 / ~650 DIP.
2. **§27.6's ten panel-ground checks (`0d87e8b`, `4d846c9`)**, the four that
   matter most included. The computed palette above substitutes for none of them.
3. **§25.11's canvas rows (`8a62070`)** — caret-during-pick, the per-run picker
   vs whole-box rule, undo restoring colour *and* words, copy-as-image, §16.7's
   veil, §16.3's inert white dot. (Export stays green by
   `tools/TextColourRoundTrip` and does not need redoing.)
4. **§28's three (`4ab4d6f`)** — the +144 DIP cluster move with the Text tool up,
   the fold flipping live in both directions inside fullscreen, and a
   first-opened Measurement panel landing flush at 1266.0..1279.5.
5. The standing **2.54:1 `BottomMenu` hover** under Theme = Light — still
   awaiting an independent confirmation.

The eleventh run's own follow-ups are also all still open, including **watching
the entrance cascade at 72 columns**, which nobody has yet seen.

