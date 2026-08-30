using System;

namespace Quill.Services;

/// <summary>Which tool palette the user draws with. Settings ▸ Workspace ▸ Tool
/// Setup ▸ Interface offers exactly these two (CONCEPTS-REF-2026-08-07 §3.1),
/// and they are mutually exclusive: whichever one is current is the tool
/// surface, and the other is not on screen at all.</summary>
public enum ToolSurface
{
    /// <summary>The radial dial (§1). The default.</summary>
    Wheel = 0,
    /// <summary>The vertical "Bar" palette with its attached settings popover (§2).</summary>
    Bar = 1,
}

/// <summary>
/// The one place that knows which tool surface is showing.
///
/// ── WHY A SERVICE AND NOT A BOOL ON THE LIBRARY ──────────────────────────
/// The choice is read by three unrelated places - the dial, the pen bar, and
/// the Settings panel that lets the user change it - and those three are built
/// by different code on different branches. A shared static with a Changed
/// event lets each of them subscribe without any of them referencing the
/// others, which is what stops the surfaces from ever both being on screen (or
/// both being off it) because two callers disagreed about who owns the flag.
///
/// ── THE API THE SETTINGS PANEL CALLS ─────────────────────────────────────
/// The Interface control in Settings needs exactly three members:
/// <code>
///     ToolSurfaceService.Current                  // ToolSurface, for the initial selection
///     ToolSurfaceService.Set(ToolSurface.Bar);    // on pick - persists and repaints
///     ToolSurfaceService.Changed += s => ...;     // if it wants to follow external changes
/// </code>
/// Nothing else is required of it. <see cref="Set"/> is idempotent, persists
/// through the host wiring, and raises <see cref="Changed"/> only on a real
/// move, so binding a two-way control to it cannot loop.
///
/// ── HOST WIRING ──────────────────────────────────────────────────────────
/// MainWindow calls <see cref="Configure"/> once at startup with a getter and a
/// setter over the persisted library field. Before that call the service still
/// works - it simply keeps the choice in memory - so any surface can be wired
/// to it at construction time without ordering constraints.
/// </summary>
public static class ToolSurfaceService
{
    private static Func<string>? _load;
    private static Action<string>? _save;
    private static ToolSurface _current = ToolSurface.Wheel;
    private static bool _loaded;

    private static Func<bool>? _loadLegacy;
    private static Action<bool>? _saveLegacy;
    private static bool _legacy = true;
    private static bool _legacyLoaded;

    /// <summary>The tool surface that should be on screen right now.</summary>
    public static ToolSurface Current
    {
        get
        {
            // Read-through on first use so a surface constructed before
            // Configure still lands on the persisted choice rather than on the
            // default, without every caller having to order itself after the
            // host wiring.
            if (!_loaded && _load != null)
            {
                _loaded = true;
                _current = Parse(_load());
            }
            return _current;
        }
    }

    /// <summary>True when the radial dial is the current surface. Sugar for the
    /// several call sites that only care which of the two it is.</summary>
    public static bool IsWheel => Current == ToolSurface.Wheel;

    /// <summary>WHICH IMPLEMENTATION <see cref="ToolSurface.Bar"/> MEANS
    /// (CONCEPTS-REF-2026-08-07 §17.17).
    ///
    /// <para>True - the default, and what §10.3 item 10 ruled - means Bar is the
    /// ORIGINAL horizontal pen row in <c>MainWindow.xaml</c> (<c>PenRow</c>).
    /// False means Bar is the section 2 vertical palette,
    /// <c>Controls/PenBar.cs</c>.</para>
    ///
    /// <para><b>Deliberately not a third enum value.</b> §17.17: "a third enum
    /// value would multiply every place that already asks which surface is up".
    /// Every one of those sites keeps asking a two-valued question; only the two
    /// implementations of Bar consult this, and they consult it as a veto in
    /// their own <c>Wanted</c> - which is the same construction that stops the
    /// two SURFACES from ever both being on screen.</para>
    ///
    /// <para>The persisted field is <c>Library.ConceptsBarPalette</c>, whose
    /// sense is the inverse of this one. It is stored that way round because it
    /// already exists and already has that meaning in shipped libraries, and
    /// renaming a settings field to make a new property read nicer is how a
    /// user's saved choice silently reverts to a default.</para></summary>
    public static bool LegacyBar
    {
        get
        {
            if (!_legacyLoaded && _loadLegacy != null)
            {
                _legacyLoaded = true;
                _legacy = _loadLegacy();
            }
            return _legacy;
        }
    }

    /// <summary>Raised after <see cref="Current"/> actually changes. Carries the
    /// new surface so a subscriber never has to read the property back.</summary>
    public static event Action<ToolSurface>? Changed;

    /// <summary>Point the service at the persisted setting. Called once by
    /// MainWindow; the getter and setter close over the library field, so this
    /// service never takes a dependency on the model.</summary>
    public static void Configure(Func<string> load, Action<string> save)
    {
        _load = load;
        _save = save;
        _loaded = false;
        var was = _current;
        var now = Current;                 // forces the read-through above
        if (was != now) Changed?.Invoke(now);
    }

    /// <summary>Point <see cref="LegacyBar"/> at the persisted setting. Same
    /// contract as <see cref="Configure"/>: the getter and the setter close over
    /// the library field, and the read-through means a surface constructed before
    /// this call still lands on the persisted choice.
    ///
    /// <para><paramref name="load"/> and <paramref name="save"/> both speak in
    /// terms of LEGACY, not of the stored field's inverted sense - the inversion
    /// lives at the one call site in MainWindow, so nothing else has to remember
    /// which way round <c>ConceptsBarPalette</c> reads.</para></summary>
    public static void ConfigureBarStyle(Func<bool> load, Action<bool> save)
    {
        _loadLegacy = load;
        _saveLegacy = save;
        _legacyLoaded = false;
        bool was = _legacy;
        bool now = LegacyBar;              // forces the read-through above
        if (was != now) Changed?.Invoke(Current);
    }

    /// <summary>Choose which implementation <see cref="ToolSurface.Bar"/> means.
    ///
    /// <para>It raises <see cref="Changed"/> rather than an event of its own,
    /// and that is not laziness: every subscriber to <c>Changed</c> is asking
    /// "what is on screen now, and does my surface belong there" - which is
    /// exactly the question a change of Bar's implementation re-opens. Giving it
    /// a second event would mean every one of them had to subscribe twice to
    /// stay correct, and the one that forgot would be the live-switch bug §17.17
    /// and §16.8 item 4 both rule out.</para></summary>
    public static void SetLegacyBar(bool legacy)
    {
        _ = LegacyBar;                     // ensure the persisted value is in
        if (_legacy == legacy) return;
        _legacy = legacy;
        _legacyLoaded = true;
        try { _saveLegacy?.Invoke(legacy); } catch { }
        Changed?.Invoke(Current);
    }

    /// <summary>Choose a surface. Persists it and tells everyone. A no-op when
    /// the surface is already current, so a settings control that writes back
    /// what it was just given cannot loop.</summary>
    public static void Set(ToolSurface surface)
    {
        _ = Current;                       // ensure the persisted value is in
        if (_current == surface) return;
        _current = surface;
        _loaded = true;
        try { _save?.Invoke(surface.ToString()); } catch { }
        Changed?.Invoke(surface);
    }

    /// <summary>Tolerant of the enum NAME, the legacy boolean and an index, so a
    /// library.json written by any build in this family still loads. An
    /// unrecognised value means the default rather than a throw - the tool
    /// palette is not worth failing a library load over.</summary>
    public static ToolSurface Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ToolSurface.Wheel;
        var s = raw.Trim();
        if (Enum.TryParse<ToolSurface>(s, ignoreCase: true, out var v)) return v;
        if (int.TryParse(s, out int n)) return n == 1 ? ToolSurface.Bar : ToolSurface.Wheel;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return ToolSurface.Bar;
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return ToolSurface.Wheel;
        return ToolSurface.Wheel;
    }
}
