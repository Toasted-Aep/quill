namespace Quill.Models;

/// <summary>
/// One Copic marker: the published code ("B21", "BG000", "N5"), the family
/// prefix it belongs to, and the calibrated sRGB rendering of the ink.
/// </summary>
/// <remarks>
/// The hex values are the dialled-in set lifted verbatim from Quill's own
/// working web colour wheel — the source of truth — rather than eyeballed
/// swatch-chart guesses. Colours keep the reference's order inside each column
/// (index 0 is the innermost ring), which is what the concentric layout relies
/// on: the blending number still runs dark-to-light outward down a column.
/// </remarks>
public readonly record struct CopicSwatch(string Code, string Family, byte R, byte G, byte B);

/// <summary>
/// One radial column of the outer wheel: every marker of a single Copic code
/// SERIES — the letter prefix plus the first digit of the blending number, so
/// `R0` holds `R08 R05 R02 R01 R00 R000 R0000`. Colours stack outward from
/// index 0, darkest first.
/// </summary>
/// <remarks>
/// This is the reference's own rule, measured rather than guessed. Decoding the
/// Concepts capture's flat fills against its extracted colour table gives 71
/// columns of 5.0704° (= 360/71), and generating the layout from that table by
/// this rule alone reproduces every one of the 252 cells the capture actually
/// shows on screen, with zero disagreements. See §26.2.
///
/// The column carries no angles. Where a column sits is <c>ColorWheel</c>'s
/// business and is derived from the count; the old per-slice `StartAngle` /
/// `EndAngle` pair asserted a 10° column, which is exactly twice the truth, and
/// nothing ever read it.
/// </remarks>
public sealed record CopicColumn(string Series, CopicSwatch[] Colors);

/// One colour family — a contiguous run of code-series columns (R spans 7, E 9).
public sealed record CopicSector(string Id, string Name, CopicColumn[] Columns);

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
///   • Tier 3+ (outer)     — 11 colour families laid out as 72 contiguous
///                           code-series columns, each column a radial stack of
///                           however many inks that series holds.
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

    // ── Tier 3+ (outer rings): 11 families, 72 code-series columns ──
    // Family sequence YR → E → Y → YG → G → BG → B → BV → V → RV → R, and
    // inside each family the series DESCEND (R8, R5, … R0) — see BuildColumns.
    //
    // 26.3 MIRRORS THE WHEEL, and this table is one of the two halves of that.
    // Read clockwise from the top the families used to run
    //
    //     R → RV → V → BV → B → BG → G → YG → Y → E → YR
    //
    // which is the reference's own cycle traversed backwards; the reference
    // (Concepts, not our output — §11.17) runs
    //
    //     RV → R → YR → E → Y → YG → G → BG → B → BV → V.
    //
    // Reversing this list turns the first into the second: as a CYCLE the
    // reversal YR, E, Y, YG, G, BG, B, BV, V, RV, R is the same sequence as
    // the reference's, started at a different family. ColorWheel lays column i
    // out clockwise at OuterStart + i*ColStep, so reversing the list is exactly
    // a reflection about the OuterStart diameter: the column whose centre sat
    // at +p degrees from the top now sits at −p. Nothing else had to move.
    //
    // The user asked for the whole mirror, not half of one — "yes I want it
    // mirrored as you said" — so the series sort in BuildColumns turns over in
    // the same breath. Reversing the families alone would leave every family's
    // internal run pointing the wrong way, which is worse than either state.
    //
    // One source line per column is a READING convenience, not the authority:
    // the columns are re-derived from the codes themselves in
    // <see cref="BuildColumns"/>, so a marker written on the wrong line still
    // lands in its own series' column and no line can silently go stale. The
    // (code, hex) pairs below are the shipped set, unmoved — §11.27's ruling
    // that the palette is never re-sourced still stands; only the grouping
    // changed, from 36 arbitrary 10° slices to the reference's own 72.
    private static readonly (string Id, string Name, string[] Columns)[] SectorsRaw =
    {
        ("yellow-red", "Yellow Red", new[]
        {
            "YR09:f1640a YR07:f98434 YR04:ffa96b YR02:ffc69a YR01:ffd0aa YR00:ffdbbf YR000:ffeada YR0000:fff6ed",
            "YR18:f48800 YR16:ffaa35 YR15:ffb54d YR14:ff9048 YR12:ffa457",
            "YR27:df621b YR24:f7aa43 YR23:f4ba6d YR21:ffc675 YR20:ffdcae",
            "YR31:f7d391 YR30:f9e0b8",
            "YR68:e85309 YR65:f27c24 YR61:ffc4a3",
            "YR82:e0a068",
        }),
        ("earth", "Earth", new[]
        {
            "E09:952a12 E08:ad4025 E07:bd533b E04:be8c89 E02:f2bd9d E01:f8d2b8 E00:fbe2cf E000:fceee2 E0000:fef7f1",
            "E19:aa3110 E18:5a2512 E17:873c24 E15:cb8153 E13:e2ac85 E11:f5d3b8",
            "E29:5f2710 E27:c57849 E25:aa643a E23:e0a374 E21:fadbb8",
            "E39:633215 E37:986128 E35:bd8e57 E34:cbb08d E33:dfb787 E31:eedbbd E30:f3e1c6",
            "E49:442216 E47:775a48 E44:8d7362 E43:dfcdb1 E42:e8d7c3 E41:f4e7d7 E40:f7ede2",
            "E59:5a2512 E57:6b4c38 E55:967963 E53:b59a84 E51:f5e8da E50:eee2e4",
            "E79:48203c E77:634739 E74:806456 E71:9e8983 E70:dfd2cf",
            "E89:58101a E87:634739 E84:7a5e4b E81:a68c78",
            "E99:5c310c E97:6b3c16 E95:875324 E93:ad7648",
        }),
        ("yellow", "Yellow", new[]
        {
            "Y08:fde000 Y06:ffe91e Y04:ffee47 Y02:fff074 Y00:fff6a4 Y000:fffbca Y0000:fffde6",
            "Y19:ffc125 Y18:ffcd00 Y17:ffd82c Y15:ffe763 Y13:fff397 Y11:fffac9",
            "Y28:dfb768 Y26:e8c576 Y23:ffe590 Y21:fff2bb",
            "Y38:e69d37 Y35:ffc125 Y32:ffd58f",
        }),
        ("yellow-green", "Yellow Green", new[]
        {
            "YG09:81b835 YG07:9fcd34 YG06:90d94b YG05:b7da53 YG03:cae37c YG01:dbeca1 YG00:e8f3c4 YG0000:f7fbe6",
            "YG17:95c635 YG13:cde497 YG11:e0f0c7",
            "YG25:d6e969 YG23:e7f394 YG21:f5fbbf",
            "YG45:8ec449 YG41:cee9d6",
            "YG67:779e3d YG63:a6c76e YG61:d6deb0",
            "YG99:4c5c2d YG97:63783a YG95:88a04c YG93:b5c482 YG91:e0e8b8",
        }),
        ("green", "Green", new[]
        {
            "G09:139828 G07:32b444 G05:61c86c G03:81d489 G02:a1dba7 G00:c5e8c9 G000:def2e0 G0000:eef8ef",
            "G19:009d43 G17:37b54a G16:1bb55c G14:8cd585 G12:cee8cb",
            "G29:456150 G28:00793c G24:96ca9a G21:b9dbbc G20:eaf4e5",
            "G46:67a950 G43:b8d6a4 G40:e8edbe",
            "G85:83926c G82:abbc7e",
            "G99:3b5c2a G97:52783d G95:77995c G94:83946a G93:a7c48c G91:d3e3be",
        }),
        ("blue-green", "Blue Green", new[]
        {
            "BG09:00878e BG07:00939f BG05:00bacb BG02:4cd9e8 BG01:85e6ea BG000:dbf7f1 BG0000:eaf9f0",
            "BG18:408784 BG15:00bfa4 BG13:3ed1b9 BG11:c2f2de BG10:d7f3e3",
            "BG23:7bdec1",
            "BG34:66c4b8 BG32:97d6cd",
            "BG49:009fae BG45:6ac9d6",
            "BG57:3cb0c1 BG53:87cbd4",
            "BG78:356a64 BG75:679b94 BG72:9bc2bc BG70:cfdedb",
            "BG99:39694e BG96:689c7f BG93:9dc2ab BG90:d0ddd4",
        }),
        ("blue", "Blue", new[]
        {
            "B06:0085cc B05:1e9cd1 B04:4cb3dc B02:7ec9e6 B01:a1d9ee B00:c1e7f4 B000:d9f0f7 B0000:eaf6fa",
            "B18:007bbd B16:00a3df B14:5bbfe6 B12:a6d8eb",
            "B29:00438c B28:1759a1 B26:2b7ec0 B24:519fd6 B23:76b1dd B21:cbe4f4",
            "B39:184768 B37:1c638a B34:63afd1 B32:bfe1ed",
            "B45:4f7cc4 B41:b5cced",
            "B52:859ec9",
            "B69:2a3b68 B66:4c5c8e B63:8797c4 B60:d3dded",
            "B79:27386e",
            "B99:445465 B97:49768f B95:64a6c2 B93:8bbfd3 B91:b3dae4",
        }),
        ("blue-violet", "Blue Violet", new[]
        {
            "BV08:6850aa BV04:8774c4 BV02:a998da BV01:c5b6e6 BV00:ded3f2 BV000:eae3f7 BV0000:f4effa",
            "BV17:595eb4 BV13:6a88c2 BV11:a4a2c3",
            "BV29:1b2c45 BV25:7280a3 BV23:9aa5c4 BV20:d0d7e6",
            "BV39:36374f BV34:8f93a8 BV31:cad2e3",
            "BV99:222838 BV97:3e485e BV95:63708a BV93:95a1b8 BV91:d2d9e6",
        }),
        ("violet", "Violet", new[]
        {
            "V09:7e2d82 V06:ad67a6 V05:c48bbd V04:c78bb9 V01:e5c4de V000:f4e3f0 V0000:faeef7",
            "V17:633d73 V15:8c5b9e V12:c7a3d1",
            "V28:5b3c67 V25:7f598b V22:b395bd V20:e1d5e6",
            "V99:261b2a V95:775775 V93:a68ca2 V91:e0d3df",
        }),
        ("red-violet", "Red Violet", new[]
        {
            "RV09:d2399a RV06:e55db1 RV04:ef87c8 RV02:f4b1dc RV00:f7d3ec RV000:fae6f4 RV0000:fdf2fa",
            "RV19:ad2972 RV17:c5428a RV14:ee6ea9 RV13:f59cc6 RV11:f8c2db RV10:fadbe9",
            "RV29:d72866 RV25:ef7da3 RV23:f8b4cb RV21:ffbcce",
            "RV34:dd7c9c RV32:f4abb4",
            "RV42:ffa79b",
            "RV55:e485b6 RV52:f9c9de",
            "RV69:81494a RV66:a95c8d RV63:e8afd8",
            "RV99:614d4f RV95:bc8797 RV93:e0b0bc RV91:f3d8db",
        }),
        ("red", "Red", new[]
        {
            "R08:f43333 R05:ed5d47 R02:ffac8f R01:ffb2b2 R00:ffd0d0 R000:ffe3e3 R0000:fff4f4",
            "R17:ee543c R14:f59683 R12:f7aa9a R11:ffd7cf",
            "R29:e10619 R27:ee322b R24:f9685a R22:ff9f92 R21:ffb6ab R20:ffc9c2",
            "R39:b3224b R37:d6484e R35:e34e56 R32:f89a91 R30:ffd7c9",
            "R46:d91d3c R43:e86e7a",
            "R59:9d2238 R56:b85c6c",
            "R89:58101a R85:aa4257 R83:c56b82 R81:e8a3b5",
        }),
    };

    /// The inner accent/core arc, in reference order.
    public static readonly CopicCategory[] Tier1Categories =
        Tier1Raw.Select(t => new CopicCategory(t.Name, ParseRow(t.Data))).ToArray();

    /// The grey ring's four families, in reference order.
    public static readonly CopicCategory[] Tier2GrayCategories =
        Tier2Raw.Select(t => new CopicCategory(t.Name, ParseRow(t.Data))).ToArray();

    /// The 11 outer colour families, each a contiguous run of code-series
    /// columns, in wheel order.
    public static readonly CopicSector[] Sectors =
        SectorsRaw.Select(s => new CopicSector(s.Id, s.Name, BuildColumns(s.Columns))).ToArray();

    /// Every swatch across all three tiers, flattened — for nearest-colour lookup.
    public static readonly CopicSwatch[] All = BuildAll();

    private static CopicSwatch[] BuildAll()
    {
        var list = new List<CopicSwatch>(384);
        foreach (var c in Tier1Categories) list.AddRange(c.Colors);
        foreach (var c in Tier2GrayCategories) list.AddRange(c.Colors);
        foreach (var s in Sectors)
            foreach (var col in s.Columns)
                list.AddRange(col.Colors);
        return list.ToArray();
    }

    /// <summary>Re-derives one family's columns from its markers: one column per
    /// code series, series DESCENDING, and inside a column the blending number
    /// descending so the darkest ink is the innermost ring.</summary>
    /// <remarks>
    /// 26.3: the series sort used to ascend. It descends because the wheel is
    /// mirrored, and a mirror reverses the ANGULAR order at every level it
    /// exists — the family run round the circle (the <c>SectorsRaw</c> table
    /// above) and the column run inside a family (this sort). Reversing only
    /// one of the two would not mirror the wheel; it would shuffle it.
    ///
    /// The RADIAL sort below is deliberately untouched. A reflection preserves
    /// radius, so the darkest ink stays innermost on either handedness, and
    /// flipping it here would be the third kind of half-mirror.
    /// </remarks>
    private static CopicColumn[] BuildColumns(string[] rows)
    {
        var by = new Dictionary<string, List<CopicSwatch>>();
        foreach (var row in rows)
            foreach (var sw in ParseRow(row))
            {
                string key = SeriesOf(sw.Code);
                if (!by.TryGetValue(key, out var list)) by[key] = list = new List<CopicSwatch>();
                list.Add(sw);
            }
        return by.OrderByDescending(kv => SeriesDigit(kv.Key))
                 .Select(kv => new CopicColumn(
                     kv.Key,
                     kv.Value.OrderByDescending(sw => BlendOf(sw.Code)).ToArray()))
                 .ToArray();
    }

    /// The column a marker belongs to: its letters plus the FIRST digit of the
    /// blending number ("RV09" → `RV0`, "E0000" → `E0`, "B79" → `B7`).
    private static string SeriesOf(string code)
    {
        int i = 0;
        while (i < code.Length && char.IsLetter(code[i])) i++;
        return i < code.Length ? code[..(i + 1)] : code;
    }

    private static int SeriesDigit(string series) =>
        char.IsDigit(series[^1]) ? series[^1] - '0' : 0;

    /// <summary>Where a marker sits inside its column: bigger is darker, hence
    /// further in. Two-digit codes give their second digit (`R29` → 9); the
    /// extra-pale `00`-prefixed ones run past zero by their length, so `R000`
    /// (−1) and `R0000` (−2) fall outside `R00` (0) in the right order.</summary>
    private static int BlendOf(string code)
    {
        int i = 0;
        while (i < code.Length && char.IsLetter(code[i])) i++;
        string digits = code[i..];
        if (digits.Length == 0) return 0;
        return digits.Length == 2 ? digits[1] - '0' : -(digits.Length - 2);
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

    /// <summary>The closest swatch to an arbitrary colour, so the ring can show
    /// where a hand-mixed HSL/RGB colour lands. Plain squared distance in sRGB
    /// is good enough for "which chip do I outline".</summary>
    /// <remarks>
    /// The comparison is strict, so a TIE is settled by whichever swatch comes
    /// first in <see cref="All"/> — which is wheel order, and which 26.3's
    /// mirror therefore reverses for the 311 outer swatches. Enumerating the
    /// whole 16 777 216-colour sRGB cube, 186 350 queries (1.11%) now name a
    /// different code: every one of them a genuine tie, the same distance away
    /// as the code it replaces, so the colour returned is as close as it ever
    /// was and only the label moves. Six pairs in the palette share a hex
    /// outright — FB2/B06, R89/E89, E18/E59, E77/E87, Y19/Y35, 0/White — and
    /// those tie at every query.
    ///
    /// This is deliberate rather than tolerated: the outline this picks has to
    /// land on a cell the wheel actually draws, so the tie-break following the
    /// wheel's own order is the property that keeps them agreeing.
    /// </remarks>
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
