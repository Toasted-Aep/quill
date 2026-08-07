using System;
using System.Threading.Tasks;

namespace Quill.Controls;

/// <summary>The nine page backgrounds of the Concepts reference (§3.1 / §8).
/// This enum is the typed face of the string ids stored on a page.</summary>
public enum PaperKind
{
    PlainWhite,
    Transparent,
    Crumpled,
    Lightweight,
    Heavyweight,
    Rippled,
    Blueprint,
    BrownPaper,
    Darkprint,
}

/// <summary>
/// The paper synthesiser. Pure CPU, pure managed, ZERO dependencies on Win2D,
/// WinUI or Windows.UI — <see cref="PaperTextures"/> is the thin GPU wrapper on
/// top and this is where every pixel is actually decided.
///
/// <para><b>Why this is CPU code and not a chain of Win2D effects.</b> The two
/// previous rebuilds both died inside the effect graph and neither failure was
/// visible from the source. <c>BlendEffect</c> in Overlay mode has an output
/// range of about 8% on a near-white ground, so correct grain was computed and
/// then crushed; and <c>TurbulenceEffect</c> emits INDEPENDENT noise per channel,
/// so a luminance matrix over it averages three random variables and divides the
/// standard deviation by root three. Both bugs measure as "the page looks blank"
/// and neither is falsifiable by reading the source. Written as arithmetic on a
/// float array, the amplitude is stated in luminance LEVELS, there is no blend
/// mode to misread, and the whole file compiles into a plain console harness
/// (<c>tools/PaperProof</c>) that renders the tiles and reports per-pixel sigma
/// without a GPU. The acceptance numbers in §8 are measured against THIS file.</para>
///
/// <para><b>Everything is seamless.</b> The tile repeats every <see cref="Tile"/>
/// world units, so every field is periodic by construction: the gradient noise
/// wraps on an integer lattice whose period is the octave frequency, the
/// crumple's fold families are phase functions of <c>(a·u + b·v)</c> with integer
/// a and b, the ripple wave counts are integers, the blurs wrap, and the flecks
/// are stamped with wrapped indices.</para>
/// </summary>
public static class PaperGrain
{
    /// <summary>Tile edge in pixels AND in world units — the tile is baked at
    /// 96 DPI so one tile pixel is one DIP is one world unit, which is what glues
    /// the grain to the page instead of letting it swim under a zoom.</summary>
    public const int Tile = 512;

    private const float TAU = 6.28318530718f;
    private const int N = Tile * Tile;

    // =====================================================================
    // Grounds
    // =====================================================================

    /// <summary>The flat base colour of a paper — the ONLY thing the theme
    /// derivation reads (§6). A texture is a ground plus grain, and the grain is
    /// zero-mean by construction, so the ground is also the page's average
    /// colour. Blueprint, Brown Paper and Darkprint sit deliberately below the
    /// 0.5 relative-luminance line so the whole shell flips to dark chrome (§7).</summary>
    public static (byte R, byte G, byte B) GroundRgb(PaperKind k) => k switch
    {
        PaperKind.PlainWhite  => (0xFF, 0xFF, 0xFF),
        // the checkerboard averages to a light neutral, so a transparent page is a LIGHT page
        PaperKind.Transparent => (0xF2, 0xF2, 0xF2),
        PaperKind.Crumpled    => (0xF0, 0xEC, 0xE3),
        // luma 243, a hair off Plain White's 255 — enough headroom that the
        // grain's bright tail does not clip flat, and enough separation that the
        // two read as different papers in the swatch row
        PaperKind.Lightweight => (0xF5, 0xF3, 0xEE),
        PaperKind.Heavyweight => (0xE9, 0xE4, 0xD9),   // visibly greyer than Lightweight, per §8
        PaperKind.Rippled     => (0xF3, 0xF0, 0xE8),
        PaperKind.Blueprint   => (0x2E, 0x80, 0xC2),   // Y = 0.199  -> dark chrome
        PaperKind.BrownPaper  => (0xA9, 0x71, 0x3F),   // Y = 0.206  -> dark chrome
        PaperKind.Darkprint   => (0x26, 0x2B, 0x31),   // Y = 0.024  -> dark chrome
        _ => (0xFF, 0xFF, 0xFF),
    };

    /// <summary>True when the paper tints whatever colour the page carries rather
    /// than owning a fixed ground. These are the four white-stock papers: a user
    /// who sets a cream page and picks Heavyweight gets cream cartridge paper.</summary>
    public static bool TintsPageColor(PaperKind k) =>
        k is PaperKind.Crumpled or PaperKind.Lightweight or PaperKind.Heavyweight or PaperKind.Rippled;

    // =====================================================================
    // The bake
    // =====================================================================

    /// <summary>Bakes one seamless tile as straight BGRA bytes (alpha is always
    /// 255, so this doubles as premultiplied). Deterministic: the same kind and
    /// ground always produce byte-identical output.</summary>
    public static byte[] Bake(PaperKind kind, byte gr, byte gg, byte gb)
    {
        var bgra = new byte[N * 4];

        if (kind == PaperKind.PlainWhite)
        {
            Flat(bgra, gr, gg, gb);
            return bgra;
        }
        if (kind == PaperKind.Transparent)
        {
            Checkerboard(bgra);
            return bgra;
        }

        // Three signed offset fields, in LUMINANCE LEVELS on the 0..255 scale.
        // Neutral passes write the same value to all three; only the kraft
        // inclusions need to move the channels apart.
        var dr = new float[N];
        var dg = new float[N];
        var db = new float[N];

        switch (kind)
        {
            case PaperKind.Lightweight: Lightweight(dr, dg, db); break;
            case PaperKind.Heavyweight: Heavyweight(dr, dg, db); break;
            case PaperKind.Crumpled:    Crumpled(dr, dg, db);    break;
            case PaperKind.Rippled:     Rippled(dr, dg, db);     break;
            case PaperKind.Blueprint:   Blueprint(dr, dg, db);   break;
            case PaperKind.BrownPaper:  BrownPaper(dr, dg, db);  break;
            case PaperKind.Darkprint:   Darkprint(dr, dg, db);   break;
        }

        Compose(bgra, dr, dg, db, gr, gg, gb);
        return bgra;
    }

    private static void Flat(byte[] bgra, byte r, byte g, byte b)
    {
        for (int i = 0; i < N; i++)
        {
            int o = i * 4;
            bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255;
        }
    }

    private static void Compose(byte[] bgra, float[] dr, float[] dg, float[] db,
                                byte gr, byte gg, byte gb)
    {
        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            for (int x = 0; x < Tile; x++)
            {
                int i = row + x, o = i * 4;
                bgra[o]     = Clamp8(gb + db[i]);
                bgra[o + 1] = Clamp8(gg + dg[i]);
                bgra[o + 2] = Clamp8(gr + dr[i]);
                bgra[o + 3] = 255;
            }
        });
    }

    private static byte Clamp8(float v) =>
        v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    // =====================================================================
    // Papers
    // =====================================================================

    // Thin stock. Fine, TIGHT fibre — the highest-frequency grain of the set —
    // plus one slow cloud so the sheet is not uniformly busy. Two scales is the
    // minimum for paper; a single frequency reads as television static.
    private static void Lightweight(float[] dr, float[] dg, float[] db)
    {
        var d = new float[N];
        Fbm(d, 64, 64, 4, 0.52f, 32.0f, 1301);    // fibre: 8px down to 1px
        Fbm(d, 20, 20, 3, 0.55f, 10.0f, 1303);    // mid: breaks the fibre up so it
                                                  // does not read as even static
        Fbm(d, 8, 8, 3, 0.55f, 12.0f, 1307);      // cloud: gentle 64px shading
        Fbm(d, 6, 96, 2, 0.50f, 6.0f, 1319);      // faint machine direction
        Flecks(d, d, d, 260, 2.1f, 1.0f, -7.0f, 0.0f, 1327);   // sparse inclusions
        Spread(d, dr, dg, db);
    }

    // Cartridge paper. Coarser and cloudier, and a stretched pass that lays the
    // pulp down in one direction the way a paper machine does.
    private static void Heavyweight(float[] dr, float[] dg, float[] db)
    {
        var d = new float[N];
        // Nearly all the energy sits in the TOOTH — 16px down to 1px. That is
        // what "coarse" means on cartridge paper: a bumpy surface, not a patchy
        // one.
        Fbm(d, 32, 32, 5, 0.55f, 62.0f, 2203);
        Fbm(d, 128, 128, 2, 0.50f, 14.0f, 2221);  // finest tooth, 1-4px
        Fbm(d, 12, 12, 3, 0.55f, 16.0f, 2211);    // mild cloudiness, ~40px
        Fbm(d, 6, 90, 3, 0.52f, 12.0f, 2207);     // machine direction streak
        // The 170px octave is kept DELIBERATELY tiny. The eye reads large-area
        // luminance differences far more readily than it reads fine grain, so
        // even a couple of levels here is enough to make the sheet look damp-
        // stained rather than thick — it was the whole reason the first pass of
        // this paper looked dirty, at an amplitude that barely moved sigma.
        Fbm(d, 3, 3, 2, 0.55f, 5.0f, 2213);
        Flecks(d, d, d, 520, 2.6f, 1.3f, -13.0f, 0.55f, 2237);
        Spread(d, dr, dg, db);
    }

    // The strongest of the set, and the one the reference reads at thumbnail size.
    //
    // A crease is not a stain: it is a DERIVATIVE discontinuity in the sheet's
    // height, so the two faces either side of a fold catch the light differently,
    // and that is the only reason a crumple looks crumpled. So this builds a real
    // height field h — long fold families plus broad undulation — softens its
    // apexes, and lights it from the upper left through the surface gradient. The
    // fold profile (1 - d/w)^2 gives shading that is strongest AT the crease and
    // fades smoothly to nothing at distance w on BOTH sides, which is the "soft
    // shading either side of each crease" the reference calls for.
    private static void Crumpled(float[] dr, float[] dg, float[] db)
    {
        var h = new float[N];
        var warpU = new float[N];
        var warpV = new float[N];
        Fbm(warpU, 2, 2, 3, 0.5f, 0.040f, 3301);
        Fbm(warpV, 2, 2, 3, 0.5f, 0.040f, 3307);

        // Fold families. A family is the set of parallel lines where
        // (a·u + b·v) is a whole number: integer a and b make it exactly periodic
        // on the tile, and the family's line spacing is Tile/sqrt(a² + b²), i.e.
        // 121..512 px — long creases that cross the whole sheet, not a rash.
        var rng = new Rng(90210);
        const int families = 15;
        var fa = new int[families];
        var fb = new int[families];
        var fw = new float[families];
        var fp = new float[families];
        var fs = new float[families];
        for (int k = 0; k < families; k++)
        {
            int a, b;
            do { a = rng.Int(-3, 3); b = rng.Int(-3, 3); } while (a == 0 && b == 0);
            fa[k] = a; fb[k] = b;
            float norm = MathF.Sqrt(a * a + b * b);
            // half the creases are sharp and narrow, half are soft rolled folds
            float wide = k % 2 == 0 ? rng.Range(11f, 22f) : rng.Range(26f, 52f);
            fw[k] = wide / (Tile / norm);          // width re-expressed as a phase fraction
            fp[k] = rng.Range(0f, 1f);
            fs[k] = rng.Range(0.55f, 1.25f) * (rng.Next01() < 0.5f ? -1f : 1f);
        }

        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            for (int x = 0; x < Tile; x++)
            {
                int i = row + x;
                float u = (x + 0.5f) / Tile + warpU[i];
                float v = (y + 0.5f) / Tile + warpV[i];
                float acc = 0f;
                for (int k = 0; k < families; k++)
                {
                    float t = fa[k] * u + fb[k] * v - fp[k];
                    t -= MathF.Floor(t);                  // [0,1)
                    float dist = t > 0.5f ? 1f - t : t;   // phase distance to the nearest line
                    float w = fw[k];
                    if (dist >= w) continue;
                    float e = 1f - dist / w;
                    acc += fs[k] * e * e;
                }
                h[i] = acc;
            }
        });

        // Broad undulation under the creases: the sheet is bowed as well as folded.
        Fbm(h, 3, 3, 4, 0.55f, 0.85f, 3313);
        Fbm(h, 7, 7, 3, 0.55f, 0.30f, 3319);

        // Round the fold apexes. Without this a crease is a one-pixel step and
        // reads as a drawn line rather than as paper.
        BoxBlurWrap(h, 2);
        BoxBlurWrap(h, 1);

        var d = new float[N];
        Shade(h, d, -0.62f, -0.68f, 190f);       // lit from the upper left
        Ambient(h, d, 11.0f);                    // deep folds sit in shadow overall
        Fbm(d, 96, 96, 3, 0.52f, 11.0f, 3323);   // fine grain over the relief
        Fbm(d, 24, 24, 3, 0.55f, 8.0f, 3329);
        Spread(d, dr, dg, db);
    }

    // Directional laid paper: horizontal ripples that undulate along their length,
    // built as a height field and lit from above so each ripple gets a bright
    // crest and a shadowed trough instead of a pair of grey outlines.
    private static void Rippled(float[] dr, float[] dg, float[] db)
    {
        var h = new float[N];
        var wob = new float[N];
        Fbm(wob, 3, 2, 3, 0.55f, 1.30f, 4401);   // the wave in the ripple

        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            float v = (y + 0.5f) / Tile;
            for (int x = 0; x < Tile; x++)
            {
                int i = row + x;
                float w = wob[i];
                // 7 major ripples across the tile (period ~73px) with 21 fine laid
                // lines riding on them. Integer wave counts keep the wrap exact.
                h[i] = MathF.Sin(TAU * (7f * v) + w)
                     + 0.34f * MathF.Sin(TAU * (21f * v) + w * 1.7f + 1.1f);
            }
        });
        Fbm(h, 5, 5, 3, 0.55f, 0.35f, 4409);     // chain-line irregularity
        BoxBlurWrap(h, 1);

        var d = new float[N];
        // Deliberately gentler than Crumpled: a ripple is a standing wave in the
        // sheet, not a fold, and at Crumpled's amplitude it stops reading as
        // paper and starts reading as corrugated iron.
        Shade(h, d, -0.22f, -0.86f, 96f);        // light from above: crest lit, trough dark
        Ambient(h, d, 3.4f);
        Fbm(d, 72, 72, 3, 0.52f, 9.0f, 4421);
        Fbm(d, 12, 12, 3, 0.55f, 6.0f, 4423);
        Spread(d, dr, dg, db);
    }

    // Flat saturated blue with a faint fibre grain. §8 gives Blueprint NO grid —
    // the grid is the orthogonal "Grid Type" setting, not part of the paper.
    private static void Blueprint(float[] dr, float[] dg, float[] db)
    {
        var d = new float[N];
        Fbm(d, 48, 48, 4, 0.52f, 30.0f, 5501);
        Fbm(d, 4, 4, 3, 0.55f, 14.0f, 5507);     // the uneven wash of a diazo print
        Fbm(d, 8, 110, 2, 0.50f, 7.0f, 5519);
        Spread(d, dr, dg, db);
    }

    // Near-black slate with a faint grain. The ground is dark enough that a few
    // levels of movement is plenty; more would read as sensor noise.
    private static void Darkprint(float[] dr, float[] dg, float[] db)
    {
        var d = new float[N];
        Fbm(d, 56, 56, 4, 0.52f, 21.0f, 6601);
        Fbm(d, 5, 5, 3, 0.55f, 10.0f, 6607);
        Spread(d, dr, dg, db);
    }

    // Kraft. Three things make brown paper read as kraft rather than as a brown
    // rectangle: a coarse tooth, a strong machine-direction pulp streak, and the
    // dark wood-fibre SHIVES — short slivers of unbleached fibre, aligned with the
    // machine direction and browner than the sheet, not neutral grey. All three
    // are here, and the shives are the one pass in the file that moves the
    // channels apart.
    private static void BrownPaper(float[] dr, float[] dg, float[] db)
    {
        var d = new float[N];
        Fbm(d, 18, 18, 5, 0.55f, 46.0f, 7701);    // tooth — kraft is coarse
        // Kraft IS machine-directional, but this pass has to stay well under the
        // isotropic tooth. Run it any louder and the long axis wins outright: the
        // sheet stops looking like paper made of pressed fibre and starts looking
        // like wood veneer, which is the failure this number is guarding.
        Fbm(d, 4, 72, 3, 0.52f, 14.0f, 7703);
        Fbm(d, 3, 3, 3, 0.55f, 14.0f, 7717);      // cloud
        Fbm(d, 110, 110, 2, 0.50f, 13.0f, 7723);  // surface fleck
        Spread(d, dr, dg, db);

        // Dark shives: long, thin, near-horizontal and warm — they take the blue
        // channel down hardest, which is what makes them look like wood rather
        // than like dirt.
        Flecks(dr, dg, db, 420, 5.5f, 0.85f, -22f, 0.20f, 7741, elongate: true,
               tintG: 0.78f, tintB: 0.55f);
        // Pale flecks: filler, and the odd bleached fibre.
        Flecks(dr, dg, db, 200, 2.4f, 1.1f, 18f, 0.35f, 7757, elongate: true,
               tintG: 0.95f, tintB: 0.90f);
    }

    // =====================================================================
    // Fields
    // =====================================================================

    /// <summary>Adds fractal Brownian motion to <paramref name="dst"/>.
    /// <paramref name="fx"/> and <paramref name="fy"/> are the base lattice
    /// frequencies ACROSS THE WHOLE TILE and are integers, which is what makes
    /// every octave wrap exactly; unequal values stretch the noise into a
    /// direction. <paramref name="amp"/> is in the caller's units — for a delta
    /// field that is luminance levels, and a 4-octave call at gain 0.52 has a
    /// standard deviation of roughly 0.2 × amp.</summary>
    private static void Fbm(float[] dst, int fx, int fy, int octaves, float gain, float amp, int seed)
    {
        // Normalise by the octave sum so amp means the same thing whatever the
        // octave count is.
        float norm = 0f, a = 1f;
        for (int o = 0; o < octaves; o++) { norm += a; a *= gain; }
        float scale = amp / norm;

        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            float v = (y + 0.5f) / Tile;
            for (int x = 0; x < Tile; x++)
            {
                float u = (x + 0.5f) / Tile;
                float sum = 0f, amp2 = scale;
                int ax = fx, ay = fy;
                for (int o = 0; o < octaves; o++)
                {
                    sum += amp2 * Perlin(u * ax, v * ay, ax, ay, seed + o * 7919);
                    amp2 *= gain;
                    ax <<= 1; ay <<= 1;
                }
                dst[row + x] += sum;
            }
        });
    }

    /// <summary>Turns a height field into a lit surface. What is written is the
    /// deviation of Lambert shading from flat — the dot of the surface gradient
    /// with the light's screen-space direction — so a ridge is bright on the side
    /// facing the light and dark on the far side, and a flat region is exactly
    /// zero. That "exactly zero" is why the grain never shifts the ground colour
    /// the theme derivation reads.</summary>
    private static void Shade(float[] h, float[] dst, float lx, float ly, float gain)
    {
        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            int up = ((y - 1 + Tile) % Tile) * Tile;
            int dn = ((y + 1) % Tile) * Tile;
            for (int x = 0; x < Tile; x++)
            {
                int xl = (x - 1 + Tile) % Tile;
                int xr = (x + 1) % Tile;
                float gx = (h[row + xr] - h[row + xl]) * 0.5f;
                float gy = (h[dn + x] - h[up + x]) * 0.5f;
                dst[row + x] += gain * -(gx * lx + gy * ly);
            }
        });
    }

    /// <summary>Broad shading from height alone — the deep parts of a crumple are
    /// in shadow whichever way they happen to face. Mean-subtracted, so it does
    /// not shift the ground colour.</summary>
    private static void Ambient(float[] h, float[] dst, float gain)
    {
        double sum = 0;
        for (int i = 0; i < N; i++) sum += h[i];
        float mean = (float)(sum / N);
        for (int i = 0; i < N; i++) dst[i] += gain * (h[i] - mean);
    }

    /// <summary>Stamps elongated Gaussian inclusions with WRAPPED indices, so a
    /// fleck that runs off one edge continues on the other and the seam stays
    /// invisible. <paramref name="tintG"/> and <paramref name="tintB"/> scale the
    /// green and blue offsets, which is how an inclusion is made warmer than the
    /// sheet it sits in.</summary>
    private static void Flecks(float[] dr, float[] dg, float[] db, int count,
                               float length, float width, float amp, float vary, int seed,
                               bool elongate = false, float tintG = 1f, float tintB = 1f)
    {
        bool shared = ReferenceEquals(dr, dg);
        var rng = new Rng(seed);
        for (int f = 0; f < count; f++)
        {
            float cx = rng.Range(0f, Tile);
            float cy = rng.Range(0f, Tile);
            // aligned flecks sit near the machine direction (horizontal); the rest
            // point anywhere
            float ang = elongate ? rng.Range(-0.42f, 0.42f) : rng.Range(0f, TAU);
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            float len = length * rng.Range(0.6f, 1.6f);
            float wid = width * rng.Range(0.7f, 1.4f);
            float a = amp * (1f + vary * rng.Range(-1f, 1f));
            int reach = (int)MathF.Ceiling(MathF.Max(len, wid) * 2.5f);
            for (int oy = -reach; oy <= reach; oy++)
            {
                int py = ((int)cy + oy) % Tile; if (py < 0) py += Tile;
                int row = py * Tile;
                for (int ox = -reach; ox <= reach; ox++)
                {
                    float t = ox * ca + oy * sa;
                    float n = -ox * sa + oy * ca;
                    float e = (t * t) / (len * len) + (n * n) / (wid * wid);
                    if (e > 6f) continue;
                    float w = MathF.Exp(-e) * a;
                    int px = ((int)cx + ox) % Tile; if (px < 0) px += Tile;
                    int i = row + px;
                    dr[i] += w;
                    if (!shared) { dg[i] += w * tintG; db[i] += w * tintB; }
                }
            }
        }
    }

    /// <summary>Wrapping box blur, separable, in place.</summary>
    private static void BoxBlurWrap(float[] src, int r)
    {
        if (r <= 0) return;
        int w = 2 * r + 1;
        float inv = 1f / w;
        var tmp = new float[N];

        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            for (int x = 0; x < Tile; x++)
            {
                float s = 0f;
                for (int k = -r; k <= r; k++)
                {
                    int xx = (x + k) % Tile; if (xx < 0) xx += Tile;
                    s += src[row + xx];
                }
                tmp[row + x] = s * inv;
            }
        });
        Parallel.For(0, Tile, y =>
        {
            int row = y * Tile;
            for (int x = 0; x < Tile; x++)
            {
                float s = 0f;
                for (int k = -r; k <= r; k++)
                {
                    int yy = (y + k) % Tile; if (yy < 0) yy += Tile;
                    s += tmp[yy * Tile + x];
                }
                src[row + x] = s * inv;
            }
        });
    }

    private static void Spread(float[] d, float[] dr, float[] dg, float[] db)
    {
        for (int i = 0; i < N; i++) { float v = d[i]; dr[i] += v; dg[i] += v; db[i] += v; }
    }

    // =====================================================================
    // Periodic gradient noise
    // =====================================================================

    // Unit gradients on a 256-entry table: one hash and two array reads per
    // corner, instead of a sin/cos pair eight million times per tile.
    private static readonly float[] GradX = new float[256];
    private static readonly float[] GradY = new float[256];

    static PaperGrain()
    {
        for (int i = 0; i < 256; i++)
        {
            float a = i * (TAU / 256f);
            GradX[i] = MathF.Cos(a);
            GradY[i] = MathF.Sin(a);
        }
    }

    private static uint Hash(int x, int y, int seed)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    /// <summary>Perlin gradient noise that is exactly periodic with a period of
    /// (<paramref name="px"/>, <paramref name="py"/>) lattice cells. Output runs
    /// roughly over [-0.7, 0.7] with a standard deviation near 0.22.</summary>
    private static float Perlin(float x, float y, int px, int py, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        float v = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);

        int xa = Mod(x0, px), xb = Mod(x0 + 1, px);
        int ya = Mod(y0, py), yb = Mod(y0 + 1, py);

        float n00 = Dot(xa, ya, seed, fx, fy);
        float n10 = Dot(xb, ya, seed, fx - 1f, fy);
        float n01 = Dot(xa, yb, seed, fx, fy - 1f);
        float n11 = Dot(xb, yb, seed, fx - 1f, fy - 1f);

        float a = n00 + u * (n10 - n00);
        float b = n01 + u * (n11 - n01);
        return a + v * (b - a);
    }

    private static float Dot(int gx, int gy, int seed, float dx, float dy)
    {
        int g = (int)(Hash(gx, gy, seed) & 255u);
        return GradX[g] * dx + GradY[g] * dy;
    }

    private static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

    /// <summary>A tiny deterministic PRNG. <see cref="System.Random"/>'s sequence
    /// is not contractually stable across runtimes, and a paper tile that changes
    /// between .NET versions is a paper tile that cannot be regression-tested.</summary>
    private struct Rng
    {
        private uint _s;
        public Rng(int seed) { _s = (uint)seed | 1u; }
        private uint NextU()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return _s;
        }
        public float Next01() => (NextU() >> 8) * (1f / 16777216f);
        public float Range(float a, float b) => a + (b - a) * Next01();
        public int Int(int a, int b) => a + (int)(Next01() * (b - a + 1)) % (b - a + 1);
    }

    // =====================================================================
    // Checkerboard
    // =====================================================================

    // ~8 DIP squares (§8). The count per axis is even, so the pattern is
    // continuous across the wrap.
    private static void Checkerboard(byte[] bgra)
    {
        const int s = 8;
        for (int y = 0; y < Tile; y++)
        {
            for (int x = 0; x < Tile; x++)
            {
                bool dark = (((x / s) + (y / s)) & 1) == 1;
                byte c = dark ? (byte)0xCF : (byte)0xFF;
                int o = (y * Tile + x) * 4;
                bgra[o] = c; bgra[o + 1] = c; bgra[o + 2] = c; bgra[o + 3] = 255;
            }
        }
    }

    // =====================================================================
    // Ids
    // =====================================================================

    /// <summary>The short id stored in <c>NotePage.Paper</c>. Plain White stores
    /// the EMPTY string, which is what every pre-existing page already holds, so
    /// there is no migration and no old page changes appearance.</summary>
    public static string IdOf(PaperKind k) => k switch
    {
        PaperKind.PlainWhite  => "",
        PaperKind.Transparent => "transparent",
        PaperKind.Crumpled    => "crumpled",
        PaperKind.Lightweight => "lightweight",
        PaperKind.Heavyweight => "heavyweight",
        PaperKind.Rippled     => "rippled",
        PaperKind.Blueprint   => "blueprint",
        PaperKind.BrownPaper  => "brown",
        PaperKind.Darkprint   => "darkprint",
        _ => "",
    };

    /// <summary>Parses a stored id. Null for "no texture" (the plain-colour page)
    /// and null for anything unrecognised, so a library written by a future build
    /// degrades to a flat page instead of throwing.</summary>
    public static PaperKind? FromId(string? id) => id switch
    {
        "transparent" => PaperKind.Transparent,
        "crumpled"    => PaperKind.Crumpled,
        "lightweight" => PaperKind.Lightweight,
        "heavyweight" => PaperKind.Heavyweight,
        "rippled"     => PaperKind.Rippled,
        "blueprint"   => PaperKind.Blueprint,
        "brown"       => PaperKind.BrownPaper,
        "darkprint"   => PaperKind.Darkprint,
        _ => null,
    };
}
