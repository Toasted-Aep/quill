# Orchestration state — resume point

Last updated: 2026-07-13. If a session dies, resume from here. All decisions
below were made explicitly by the user.

## User decisions (binding)
- Art Mode lives INSIDE the Quill app (new ArtSurface + "Art Notebook" type in
  the gallery). NOT a separate copy.
- AI art features use CLOUD APIs (keys in Windows Credential Locker, like the
  chat AI). Providers: Stability/Replicate-class; sketch-to-image (ControlNet),
  inpaint/outpaint, SAM selection/flatting, pose assistant, stroke
  beautification.
- Sequencing: Phase A (Quill fixes) first, then Phase B (Art Mode).
- Depletion curves: S-curve, exponential, linear (third assumed, unconfirmed).
- Orchestrator role: sub-agents implement; the orchestrator reviews diffs,
  commits, and does A8 (liquid glass) personally. No pushes by agents.

## Phase A — DONE (all committed & pushed)
- Cell-deletion bug + table heal (da19626)
- Comment pins gated to comment mode + Settings toggle (d1812ff)
- Per-page gridline colours (fd600ee)
- Icon centering / motion blur setting / persistence audit (6182229)

## In flight / next (in order)
1. IMPLEMENT docs/TEXTBOX-SPEC.md (deep-reasoner spec, user shown a summary and
   said "continue"): vertical growth for all bubbles, table-cell row growth
   with in-place reflow (no focus loss), width hysteresis. Dispatch as a
   workflow; build-verify; orchestrator reviews + commits.
2. A6: Circulate glow v4 — produce 3 candidate motion designs as a visual
   artifact for the user to CHOOSE before implementing (current v3 =
   stop-driven single band; user unhappy with earlier versions).
3. A8: Liquid glass improvement — orchestrator does this personally, with
   screenshot review (user explicitly asked for this).
4. Phase B Art Mode (see the giant spec in the user's message of 2026-07-13,
   preserved in git history / conversation): B1 architecture doc FIRST for
   user review, then substrates, paint physics (depletion, water dip resets
   colour, mixing palette, drying presets + real-time toggle + dryness slider,
   static-wet rule), 13 media presets, palette knives, Art Notebook
   integration (trimmed top bar: keep comment/touch-draw/perfect-object/undo/
   redo/history/recording/AI; drop text/lasso/dictation; panning + mouse
   modes; touch-draw off by default), stroke list panel (hover-highlight,
   delete/recolour) + replay for painting history, .artq format, undo via
   tile snapshots, canvas setup dialog (A4/Letter/8x10/custom, in/cm, 150/300
   DPI, substrate picker: Canvas/Belgian Linen/Wood Panel/Rough Watercolor).
5. Phase C: close out ROADMAP.md, write the new one around Art Mode phases.

## Known environment facts
- Build: dotnet build src/Quill/Quill.csproj -c Debug -p:Platform=x64
- Files contain PUA glyphs: prefer python patch scripts over the Edit tool.
- Kill app before building: powershell Stop-Process -Name Quill -Force
- Session/sub-agent limits have been tripping; workflows are resumable via
  their saved scripts under the session's workflows/scripts dir.
- MSIX rebuild: -p:Msix=true -p:QuillCertThumbprint=FDAA8C46B072249CDF2344402E8FB7C9BEB9E762
