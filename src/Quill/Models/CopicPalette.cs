namespace Quill.Models;

/// <summary>
/// One Copic marker: the published code ("B21", "BG000", "N5"), the family
/// prefix it belongs to, and the calibrated sRGB rendering of the ink.
/// </summary>
/// <remarks>
/// The hex values are the dialled-in set lifted verbatim from Quill's own
/// working web colour wheel — the source of truth — rather than eyeballed
/// swatch-chart guesses. Colours keep the reference's order inside each slice
/// (index 0 is the innermost ring), which is what the concentric layout relies
/// on: the blending number still runs light-to-dark down a family.
/// </remarks>
public readonly record struct CopicSwatch(string Code, string Family, byte R, byte G, byte B);

/// <summary>
/// One 10° angular column of the outer wheel: its colours stack radially,
/// index 0 innermost. <see cref="StartAngle"/>/<see cref="EndAngle"/> are the
/// reference's SVG-style degrees (0° = east, clockwise, -90° = top).
/// </summary>
public sealed record CopicSlice(double StartAngle, double EndAngle, CopicSwatch[] Colors);

/// A colour family holding a contiguous run of 10° slices (R spans 3, BG 4, E 5).
public sealed record CopicSector(string Id, string Name, CopicSlice[] Slices);

/// A grouped run of swatches on one of the two inner rings (accents/core, greys).
public sealed record CopicCategory(string Name, CopicSwatch[] Colors);

/// <summary>
/// The Copic marker table, as code. It is static reference data — a palette,
/// not user data — so it deliberately does not live in library.json.
///
/// The data mirrors the reference wheel's three tiers exactly, because the ring
/// geometry is driven by that structure:
///   • Tier 1 (inner arc)  — accents + core, a 144° arc split into 3 groups.
///   • Tier 2 (grey ring)  — the four Copic grey families, a full circle.
///   • Tier 3+ (outer)     — 11 colour families laid out as 36 contiguous 10°
///                           slices running -90°→270°, each slice a radial
///                           column whose depth is however many inks it holds.
///
/// Each run is authored as compact "CODE:RRGGBB" tokens so the source stays
/// readable; the parsed <see cref="CopicSwatch"/> arrays are what everything
/// else consumes.
/// </summary>
public static class CopicPalette
{
    // ── Tier 1 (inner arc): accents + core numbers/names ──
    private static readonly (string Name, string Data)[] Tier1Raw =
    {
        ("Accents",     "FV2:5c6ac4 FB2:0085cc FBG2:00a8c6 FYG2:8cb82b FYG1:a4cf2a FY1:ffcc00 FYR1:ff8800 FRV1:e35b96"),
        ("CoreNumbers", "0:ffffff 100:111315 110:0a0b0d"),
        ("CoreNames",   "White:ffffff Black:000000"),
    };

    // ── Tier 2 (grey ring): Toner, Warm, Neutral, Cool ──
    private static readonly (string Name, string Data)[] Tier2Raw =
    {
        ("Toner",   "T0:eaecee T1:dcdee1 T2:cdcfd3 T3:bebfc4 T4:acadb2 T5:98999e T6:818287 T7:696a6f T8:505155 T9:37383a T10:202022"),
        ("Warm",    "W00:f6f4ee W0:eae7de W1:ded9cd W2:d0cabc W3:c0b8aa W4:afa697 W5:9c9283 W6:877c6c W7:706556 W8:584d3e W9:403627 W10:2b2214"),
        ("Neutral", "N10:1d2126 N9:343940 N8:4f555e N7:6a707b N6:848b97 N5:9da4b0 N4:b3b9c2 N3:c5cad2 N2:d4d8de N1:e2e5e9 N0:eef0f2"),
        ("Cool",    "C10:0f1722 C9:202d3f C8:36465c C7:4f6178 C6:677b93 C5:8094ab C4:98abc1 C3:adbed0 C2:c1cfdc C1:d3dde6 C0:e2e9f0 C00:f0f4f8"),
    };

    // ── Tier 3+ (outer rings): 11 families as 36 contiguous 10° slices ──
    // Sequence R → RV → V → BV → B → BG → G → YG → Y → E → YR, -90°→270°.
    private static readonly (string Id, string Name, (double A0, double A1, string Data)[] Slices)[] SectorsRaw =
    {
        ("red", "Red", new (double, double, string)[]
        {
            (-90, -80, "R89:58101a R59:9d2238 R46:d91d3c R39:b3224b R29:e10619 R17:ee543c R08:f43333"),
            (-80, -70, "R85:aa4257 R56:b85c6c R43:e86e7a R37:d6484e R27:ee322b R14:f59683 R12:f7aa9a"),
            (-70, -60, "R83:c56b82 R81:e8a3b5 R35:e34e56 R32:f89a91 R24:f9685a R22:ff9f92 R21:ffb6ab R20:ffc9c2 R11:ffd7cf R01:ffb2b2 R00:ffd0d0 R000:ffe3e3 R0000:fff4f4"),
        }),
        ("red-violet", "Red Violet", new (double, double, string)[]
        {
            (-60, -50, "RV29:d72866 RV25:ef7da3 RV23:f8b4cb RV14:ee6ea9 RV13:f59cc6 RV11:f8c2db RV10:fadbe9"),
            (-50, -40, "RV19:ad2972 RV17:c5428a RV06:e55db1 RV04:ef87c8 RV02:f4b1dc RV00:f7d3ec RV000:fae6f4 RV0000:fdf2fa"),
            (-40, -30, "RV09:d2399a"),
        }),
        ("violet", "Violet", new (double, double, string)[]
        {
            (-30, -20, "V99:261b2a V95:775775 V93:a68ca2 V91:e0d3df"),
            (-20, -10, "V28:5b3c67 V25:7f598b V22:b395bd V20:e1d5e6"),
            (-10, 0, "V17:633d73 V15:8c5b9e V12:c7a3d1 V09:7e2d82 V06:ad67a6 V05:c48bbd V04:c78bb9 V01:e5c4de V000:f4e3f0 V0000:faeef7"),
        }),
        ("blue-violet", "Blue Violet", new (double, double, string)[]
        {
            (0, 10, "B45:4f7cc4 B41:b5cced B79:27386e B69:2a3b68 B66:4c5c8e B63:8797c4 B60:d3dded"),
            (10, 20, "B52:859ec9 BV39:36374f BV29:1b2c45 BV17:595eb4 BV08:6850aa BV04:8774c4 BV02:a998da BV01:c5b6e6 BV00:ded3f2 BV000:eae3f7 BV0000:f4effa"),
            (20, 30, "BV99:222838 BV97:3e485e BV95:63708a BV93:95a1b8 BV91:d2d9e6 BV34:8f93a8 BV31:cad2e3 BV25:7280a3 BV23:9aa5c4 BV20:d0d7e6"),
        }),
        ("blue", "Blue", new (double, double, string)[]
        {
            (30, 40, "B18:007bbd B16:00a3df B14:5bbfe6 B12:a6d8eb"),
            (40, 50, "B29:00438c B28:1759a1 B26:2b7ec0 B24:519fd6 B23:76b1dd B21:cbe4f4"),
            (50, 60, "B39:184768 B37:1c638a B34:63afd1 B32:bfe1ed"),
        }),
        ("blue-green", "Blue Green", new (double, double, string)[]
        {
            (60, 70, "BG34:66c4b8 BG49:009fae BG99:39694e B06:0085cc"),
            (70, 80, "BG32:97d6cd BG45:6ac9d6 BG57:3cb0c1 BG96:689c7f B05:1e9cd1"),
            (80, 90, "BG53:87cbd4 BG78:356a64 BG75:679b94 BG93:9dc2ab B04:4cb3dc"),
            (90, 100, "BG72:9bc2bc BG70:cfdedb BG90:d0ddd4 B02:7ec9e6 B01:a1d9ee B00:c1e7f4 B000:d9f0f7 B0000:eaf6fa"),
        }),
        ("green", "Green", new (double, double, string)[]
        {
            (100, 110, "G99:3b5c2a G97:52783d G95:77995c G93:a7c48c G91:d3e3be G46:67a950 G43:b8d6a4 G28:00793c G24:96ca9a G21:b9dbbc G20:eaf4e5"),
            (110, 120, "G19:009d43 G17:37b54a G16:1bb55c G14:8cd585 G12:cee8cb"),
            (120, 130, "G09:139828 G07:32b444 G05:61c86c G03:81d489 G02:a1dba7 G00:c5e8c9 G000:def2e0 G0000:eef8ef"),
        }),
        ("yellow-green", "Yellow Green", new (double, double, string)[]
        {
            (130, 140, "YG99:4c5c2d YG97:63783a YG95:88a04c YG93:b5c482 YG91:e0e8b8 YG67:779e3d YG63:a6c76e"),
            (140, 150, "YG45:8ec449 YG41:cee9d6 YG25:d6e969 YG23:e7f394 YG21:f5fbbf YG13:cde497 YG11:e0f0c7"),
            (150, 160, "YG17:95c635 YG09:81b835 YG07:9fcd34 YG05:b7da53 YG03:cae37c YG01:dbeca1 YG00:e8f3c4 YG0000:f7fbe6"),
        }),
        ("yellow", "Yellow", new (double, double, string)[]
        {
            (160, 170, "Y38:e69d37 Y28:dfb768 Y19:ffc125 Y18:ffcd00 Y17:ffd82c Y15:ffe763 Y13:fff397 Y11:fffac9"),
            (170, 180, "Y08:fde000 Y06:ffe91e Y04:ffee47 Y02:fff074 Y00:fff6a4 Y000:fffbca Y0000:fffde6"),
            (180, 190, "Y35:ffc125 Y26:e8c576 Y23:ffe590 Y21:fff2bb"),
        }),
        ("earth", "Earth", new (double, double, string)[]
        {
            (190, 200, "E99:5c310c E89:58101a E79:48203c E59:5a2512 E49:442216 E39:633215 E29:5f2710 E19:aa3110 E09:952a12 E08:ad4025 E07:bd533b E04:be8c89 E02:f2bd9d E01:f8d2b8 E00:fbe2cf E000:fceee2 E0000:fef7f1"),
            (200, 210, "E97:6b3c16 E87:634739 E77:634739 E57:6b4c38 E47:775a48 E37:986128 E27:c57849 E18:5a2512 E17:873c24 E15:cb8153 E13:e2ac85 E11:f5d3b8"),
            (210, 220, "E95:875324 E84:7a5e4b E74:806456 E55:967963 E44:8d7362 E35:bd8e57 E25:aa643a E23:e0a374 E21:fadbb8"),
            (220, 230, "E93:ad7648 E81:a68c78 E71:9e8983 E53:b59a84 E43:dfcdb1 E34:cbb08d E33:dfb787 E31:eedbbd"),
            (230, 240, "E70:dfd2cf E51:f5e8da E42:e8d7c3 E41:f4e7d7 E50:eee2e4 E40:f7ede2 E30:f3e1c6"),
        }),
        ("yellow-red", "Yellow Red", new (double, double, string)[]
        {
            (240, 250, "YR68:e85309 YR27:df621b YR18:f48800 YR09:f1640a"),
            (250, 260, "YR82:e0a068 YR31:f7d391 YR24:f7aa43 YR16:ffaa35 YR07:f98434"),
            (260, 270, "YR65:f27c24 YR30:f9e0b8 YR23:f4ba6d YR15:ffb54d YR04:ffa96b YR02:ffc69a YR01:ffd0aa YR00:ffdbbf YR000:ffeada YR0000:fff6ed"),
        }),
    };

    /// The inner accent/core arc, in reference order.
    public static readonly CopicCategory[] Tier1Categories =
        Tier1Raw.Select(t => new CopicCategory(t.Name, ParseRow(t.Data))).ToArray();

    /// The grey ring's four families, in reference order.
    public static readonly CopicCategory[] Tier2GrayCategories =
        Tier2Raw.Select(t => new CopicCategory(t.Name, ParseRow(t.Data))).ToArray();

    /// The 11 outer colour families, each a run of 10° slices, in wheel order.
    public static readonly CopicSector[] Sectors =
        SectorsRaw.Select(s => new CopicSector(s.Id, s.Name,
            s.Slices.Select(sl => new CopicSlice(sl.A0, sl.A1, ParseRow(sl.Data))).ToArray())).ToArray();

    /// Every swatch across all three tiers, flattened — for nearest-colour lookup.
    public static readonly CopicSwatch[] All = BuildAll();

    private static CopicSwatch[] BuildAll()
    {
        var list = new List<CopicSwatch>(384);
        foreach (var c in Tier1Categories) list.AddRange(c.Colors);
        foreach (var c in Tier2GrayCategories) list.AddRange(c.Colors);
        foreach (var s in Sectors)
            foreach (var sl in s.Slices)
                list.AddRange(sl.Colors);
        return list.ToArray();
    }

    private static CopicSwatch[] ParseRow(string data)
    {
        var list = new List<CopicSwatch>();
        foreach (var token in data.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');
            if (colon < 0) continue;   // a typo must not take the palette down
            string code = token[..colon];
            string hex = token[(colon + 1)..];
            if (hex.Length != 6) continue;
            list.Add(new CopicSwatch(
                code, FamilyOf(code),
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16)));
        }
        return list.ToArray();
    }

    // The family is the leading run of letters ("BV0000" → "BV", "R89" → "R").
    // Pure numbers (0, 100, 110) are core neutrals rather than a hue family.
    private static string FamilyOf(string code)
    {
        int i = 0;
        while (i < code.Length && char.IsLetter(code[i])) i++;
        return i == 0 ? "Core" : code[..i];
    }

    /// The closest swatch to an arbitrary colour, so the ring can show where a
    /// hand-mixed HSL/RGB colour lands. Plain squared distance in sRGB is good
    /// enough for "which chip do I outline".
    public static CopicSwatch Nearest(byte r, byte g, byte b)
    {
        var best = All[0];
        int bestD = int.MaxValue;
        foreach (var s in All)
        {
            int dr = s.R - r, dg = s.G - g, db = s.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }
}
