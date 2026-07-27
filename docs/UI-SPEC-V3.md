# Quill UI spec v3 — Concepts-style shell

User-authored, 2026-07-23. Builds on UI-SPEC-V2.md. Where they conflict, v3 wins.

---

## A. Radial dial — DEFECTS in the shipped build (fix first)

The docked dial works but is "nothing like the Concepts dial, very buggy":

1. **Phantom hover** — the dial lights up while the pointer is over the *page*,
   not the dial. Hit-testing is firing outside the wheel.
2. **Shows in the notebook gallery** — it must only load/appear on a page.
3. **Tools cannot be selected** — clicking a slot does nothing.
4. **Opacity not implemented.**
5. **No preview circle** — scrubbing a setting must draw a real circle with the
   current tool (see v2 §1.1).
6. **COPIC wheel unreachable** from the centre disc.
7. **Icons off-centre** inside their sectors.
8. **Dial icons differ from the top-bar icons** — they must match.
9. **A tool present in the dial must be removed from the top bar automatically.**

## B. Page chrome

- When the radial dial is enabled, **hide the page name, date and time** from
  the page surface.
- The page title becomes **renamable from the top bar**.
- Add the **Quill icon to the top bar**, identical to the gallery one.
- **Remove the Backgrounds tab** from the top bar.
- Move **notebook gallery + page name** into a **separate pane**, a sibling of
  Layers / Precision / Objects (same floating-pane family as Settings).
- Top-right, beside import/export/settings: **zoom and tilt readouts with a
  lock**, per the reference screenshot.

## C. Settings

- Port **all Concepts settings**.
- **Measurement units** — Digital: px, pts. Metric: m/cm/mm, mm, cm, m, km.
  Imperial: ft/in, in, ft, yds, mi.
- Move the **light/dark toggle into Settings as an on/off slider**, replacing
  the dropdown.
- Add **grid opacity**.
- **Toggle sliders**: styled per the screenshots, colour `#78a19c`.

### Interaction page
- **Keyboard & Mouse**: keyboard shortcuts + an "edit shortcuts" affordance;
  enable-keyboard-shortcuts on/off.
- **Touch Input — Finger Action**: Do Nothing · Use Active Tool · Pen Canvas ·
  Select · Nudge · Slice · Zoom · Rotate.
- **Gesture shortcuts**: top-button click, double-click, hold, and two/three/
  four-finger tap.

## D. Paper textures — too weak

Backgrounds are "too unrealistic, texture is almost non visible". Increase
contrast/among grain so each paper reads clearly at 100% zoom.

## E. Export / import panes

- Rebuild **Export** in the Concepts layout (see screenshot): a **Format** row
  of circular buttons, a **Region** row, **Options** toggles (include
  background / include grid), a **Details** block (output size, scale
  100/200/400%), and a blue export button `#3282aa`.
- Formats: keep Quill's existing set, and **replace "Concepts" with `.quill`**.
- **Region is dynamic**: the last region chip is the current page size (A4 when
  the page is A4).
- **Import** is a simple dropdown: files · paste from clipboard · take a picture.

## F. COPIC wheel

- It **sticks to the mouse** — fix.
- Give it the **start animation** from the web version.

## G. Pen library ⭐⭐⭐⭐⭐

Pens and brushes from Krita, Fresco and especially Concepts.

## H. Whole-UI conversion to Concepts style ⭐⭐⭐⭐⭐

Convert the entire shell. Existing Quill-only features become **tools** for now
until better homes are found — and the orchestrator owes a recommendation on
where each should end up.
