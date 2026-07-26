namespace Quill.Models;

/// <summary>
/// One Copic marker: the published code ("B21", "BG000", "N5"), the family
/// prefix it belongs to, and an approximate sRGB rendering of the ink.
/// </summary>
/// <remarks>
/// The hex values are eyeballed from published swatch charts rather than
/// measured, so they are close but not colour-managed matches for the real
/// markers. They are ordered correctly within each family — the blending
/// number still runs light-to-dark — which is what the ring layout relies on.
/// </remarks>
public readonly record struct CopicSwatch(string Code, string Family, byte R, byte G, byte B);

/// <summary>
/// The Copic marker table, as code. It is static reference data — a palette,
/// not user data — so it deliberately does not live in library.json.
///
/// The table is authored as one compact "CODE:RRGGBB" run per family so the
/// source stays readable; <see cref="All"/> is the parsed array everything
/// else consumes. Families appear in <see cref="Families"/> in wheel order
/// (warm → cool → neutral), which is the order the swatch ring lays them out.
/// </summary>
public static class CopicPalette
{
    // Family order around the ring: a hue sweep, with the four grey families
    // (fluorescents aside) parked together at the end so the neutrals read as
    // one block rather than interrupting the spectrum.
    private static readonly (string Family, string Data)[] Raw =
    {
        ("Y", "Y0000:FCF8DE Y000:FDF8D2 Y00:FCF6C4 Y02:FBF2AE Y04:F2EC72 Y06:FBF06A Y08:FCE84A " +
              "Y11:FCF4C8 Y13:FAF0A2 Y15:FBE470 Y17:F8DC4E Y18:F8E33E Y19:FBE23C " +
              "Y21:FBEBC4 Y23:F6E2B0 Y26:EED08A Y28:C6A05E Y32:F2DEB6 Y35:F8D874 Y38:F8DC8E"),

        ("YG", "YG0000:F0F6DC YG00:EAF0B8 YG01:E2EEC0 YG03:DCEEA8 YG05:CCE694 YG06:C4E484 YG07:96CE4E " +
               "YG09:86C84E YG11:E4F0CE YG13:DCEAA4 YG17:52B058 YG21:F2F0BE YG23:E4EC94 YG25:CAE07E " +
               "YG41:DCEEDC YG45:BEE0C0 YG61:C4D8B4 YG63:B0CE9E YG67:86B478 YG91:DCDCB4 YG93:D8D6A2 " +
               "YG95:C2C078 YG97:98944E YG99:5E6E38"),

        ("G", "G0000:EDF6EC G000:E6F4E4 G00:DCF0DC G02:C0E4C4 G03:A8DCA8 G05:4EB878 G07:46B06A " +
              "G09:4CBE72 G12:CCE8C0 G14:86C86A G16:3EB474 G17:009A62 G19:00A05E G20:E8F0D4 " +
              "G21:C8E4B4 G24:C0DCA0 G28:2E8452 G29:1E5E3C G40:E0EDD8 G43:C4DCAE G46:6E9A6A " +
              "G82:C6D2A8 G85:A0BC9E G94:7E8A5E G99:5E6E3A"),

        ("BG", "BG0000:E8F5F0 BG000:E3F3EE BG00:D9EEEC BG01:BCE4E8 BG02:B7E4E6 BG05:6FCBD6 BG07:22B3B8 " +
               "BG09:12B5B0 BG10:DCEDE7 BG11:CFE9E6 BG13:BFE5E1 BG15:93D8CE BG18:35B0A0 BG23:ADDDD2 " +
               "BG32:BFE0D6 BG34:A5DCCE BG45:B4E1DA BG49:00A6A0 BG53:4EBAB0 BG57:2C8F8C BG70:D8E5DE " +
               "BG72:4E8C8A BG75:3E7472 BG78:2F5C5C BG90:D5DCD6 BG93:B4C1BA BG96:6E8C82 BG99:4A7A72"),

        ("B", "B0000:E8F5F8 B000:E4F2F6 B00:DCEEF3 B01:D2EAF2 B02:A8D8EC B04:56B4DE B05:4BB1DC " +
              "B06:1FA6DC B12:C8E7F2 B14:61BCE3 B16:2CADE0 B18:2C6BA8 B21:DCEBF5 B23:9DC5E6 " +
              "B24:86C7E8 B26:6EA5D6 B28:2C5D96 B29:1F63A8 B32:DDEAF0 B34:7FBEE0 B37:2A5C86 " +
              "B39:35558F B41:DCEDF5 B45:7FB6DA B52:9FB8C8 B60:DCDDEC B63:A9B4D8 B66:6D7CB0 " +
              "B69:3A5E97 B79:3B4B96 B91:D5E0E8 B93:A9C6D8 B95:7FA3BC B97:3E6E92 B99:274B69"),

        ("BV", "BV0000:EDEAF2 BV000:EAE5F0 BV00:DCD8EC BV01:C4C0E0 BV02:B7B4DA BV04:8E8FC4 BV08:9A7FBE " +
               "BV11:DCD8EA BV13:7C7FB6 BV17:6E6FAA BV20:DDE0EE BV23:C3C6DA BV25:8E8CA4 BV29:3E4360 " +
               "BV31:E2E3EE BV34:8189A6"),

        ("V", "V0000:F4EEF6 V000:F2E8F4 V01:EEDCEC V04:E4B8DA V05:DEA6D2 V06:CE8FC8 V09:8E4EA8 " +
              "V12:EFDCEE V15:D0A6D8 V17:A87EC4 V20:DED4E4 V22:B8A8CA V25:9C8AB4 V28:6E5C82 " +
              "V91:E8D2DE V93:DCB4CE V95:B47EA8 V99:4E3450"),

        ("RV", "RV0000:FBEFF4 RV000:F9EAF2 RV00:F6DCEA RV02:F8CEE0 RV04:F58AAE RV06:EE6C9E RV09:E24C8E " +
               "RV10:FCE8F0 RV11:F9CFE0 RV13:F6BAD4 RV14:F186B0 RV17:DC6EA8 RV19:D4459A RV21:FBDCDC " +
               "RV23:F5A8C0 RV25:EE79A8 RV29:E8386E RV32:F8CBD0 RV34:F09EB2 RV42:F6BEBE RV52:DC9AB8 " +
               "RV55:C86EA0 RV63:C08AAE RV66:A44272 RV69:93476E RV91:EDD4DE RV93:E0AEC4 RV95:C48AA6 " +
               "RV99:66435A"),

        ("R", "R0000:FCF0EC R000:FDEDEA R00:FCE6E2 R01:FBDDD8 R02:FBD6CE R05:F4715C R08:EE5442 " +
              "R11:FBDCD2 R12:F9C6B4 R14:EE7A72 R17:EE7256 R20:F8CEC6 R21:F4B4AC R22:F6BEBA " +
              "R24:EF7E7E R27:E8484E R29:E2003C R30:FCE0DA R32:F6B8AC R35:EC6E76 R37:D45464 " +
              "R39:C43E62 R43:DE6C82 R46:D4425C R56:C0728A R59:A8425E R81:EFA8B8 R83:E88EA4 " +
              "R85:D66284 R89:8E2340"),

        ("YR", "YR0000:FDF2E2 YR000:FDF0DC YR00:FCE6D2 YR01:FCDCC2 YR02:FCE0C4 YR04:FBBC5C YR07:F4834E " +
               "YR09:F26E32 YR12:FBDC9E YR14:FBC864 YR15:FAD48E YR16:FBB03E YR18:F26A2E YR20:FCE8CC " +
               "YR21:FAE0B6 YR23:F6D08E YR24:F4E2A0 YR27:C4562E YR30:FBEEDC YR31:FCE4BE YR61:FBD8C4 " +
               "YR65:F9AE5E YR68:EF6E28 YR82:FBC8A0"),

        ("E", "E0000:FBF3EA E000:FCEFE6 E00:FBE8DC E01:FADDD0 E02:F8D9C8 E04:D8A0AE E07:C4785C " +
              "E08:B4614A E09:C0603F E11:F6D8C0 E13:E0AE93 E15:DCA184 E17:A85F49 E18:7E4632 " +
              "E19:A44B34 E21:F8DCC0 E23:D5A184 E25:C89A76 E27:8C6A50 E29:6E432C E30:F0E4CE " +
              "E31:EAD9BE E33:E3C9A2 E34:E0C39A E35:DCBB99 E37:B98B54 E39:A8703E E40:EFE6D8 " +
              "E41:F7EDDF E42:EFE3CC E43:DDCFAE E44:C3B295 E47:8A6E4E E49:5E4830 E50:F4E9E6 " +
              "E51:F7E7D2 E53:EDDCB8 E55:E2CFA6 E57:A67F4E E59:7E6A55 E70:E6DCD4 E71:DCC9BC " +
              "E74:9A8070 E77:6E5847 E79:4E3B2E E81:EEDDB8 E84:A18F66 E87:6A5540 E89:56422F " +
              "E93:FBD3B4 E95:F5BE8F E97:E09A5E E99:B4592C"),

        ("F", "FY1:F5EE5A FYG1:C6E24A FYR1:FBA85A FRV1:F86FA8 FV2:7E6ED8 FB2:2AA8E0 FBG2:35C6D6"),

        ("C", "C00:F0F2F4 C0:E6EAEC C1:DCE0E3 C2:CBD1D6 C3:B7BFC5 C4:A0A9B0 " +
              "C5:8B959C C6:767F87 C7:5C666D C8:464E55 C9:333A40 C10:22282C"),

        ("N", "N0:F2F2F1 N1:E6E6E5 N2:DCDCDA N3:CBCBC9 N4:B8B8B6 N5:A3A3A0 " +
              "N6:8E8E8B N7:767674 N8:5C5C5A N9:464644 N10:2E2E2C"),

        ("T", "T0:F1F1EE T1:E7E6E2 T2:DBDAD5 T3:CBC9C3 T4:B9B7B0 T5:A5A29B " +
              "T6:8F8C85 T7:77746D T8:5D5A54 T9:47443F T10:302E2A"),

        ("W", "W00:F2F0EC W0:EBE8E2 W1:E2DED7 W2:D6D1C8 W3:C6C0B6 W4:B2ABA0 " +
              "W5:9C948A W6:857D73 W7:6C645B W8:544D46 W9:3E3831 W10:2A251F"),
    };

    /// Every swatch, grouped by family in wheel order.
    public static readonly CopicSwatch[] All = Parse();

    /// Family prefixes in wheel order.
    public static readonly string[] Families = Raw.Select(r => r.Family).ToArray();

    private static CopicSwatch[] Parse()
    {
        var list = new List<CopicSwatch>(384);
        foreach (var (family, data) in Raw)
        {
            foreach (var token in data.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                if (colon < 0) continue;   // a typo must not take the palette down
                string code = token[..colon];
                string hex = token[(colon + 1)..];
                if (hex.Length != 6) continue;
                list.Add(new CopicSwatch(
                    code, family,
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16)));
            }
        }
        return list.ToArray();
    }

    /// The swatches of one family, in published (light → dark) order.
    public static CopicSwatch[] Of(string family) =>
        All.Where(s => s.Family == family).ToArray();

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
