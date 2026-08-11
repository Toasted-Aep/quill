using System.Numerics;

namespace Quill.Services;

/// <summary>One parallel family of lattice lines: the inclination it is drawn at
/// and the perpendicular distance between adjacent lines, in whatever units the
/// caller measured its spacing in.</summary>
public readonly record struct GridFamily(float AngleDeg, float Perp);

/// <summary>One drawn line, in the space the caller handed its frame corners
/// in.</summary>
public readonly record struct GridLine(Vector2 A, Vector2 B);

/// <summary>
/// The geometry of the two angled lattices - CONCEPTS-REF 12.10's isometric and
/// triangle grids - as pure arithmetic, shared by the canvas renderer
/// (<c>InkSurface.DrawGrid</c>) and the editor's preview strip and thumbnails
/// (<c>GridArt</c>). One definition rather than two, because the preview's whole
/// job is to promise what the canvas will do.
///
/// <para><b>12.11, the correction this file exists for.</b> The <c>Angle</c>
/// control is NOT a rotation. It sets the inclination of the DIAGONAL families
/// and leaves the straight family - the verticals of an isometric grid, the
/// horizontals of a triangle grid - exactly where it is, so what moves is the
/// SHAPE OF THE CELL and not the orientation of the field. The user's words:
/// <i>"angle does not work correctly it rotates whole grid not the grid parts
/// individually."</i> A rigid rotation is what <c>Orientation</c> already does
/// further down the same page, which is how the duplicate was spotted.</para>
/// </summary>
public static class GridLattice
{
    /// <summary>The angle range the page offers. Not a rendering limit but a
    /// legibility one: both lattices tie their diagonals' spacing to the
    /// straight family's, so an angle at either extreme packs the diagonals
    /// arbitrarily close together and the grid stops being readable well before
    /// it stops being drawable.</summary>
    public const double MinAngle = 5, MaxAngle = 85;

    /// <summary>The two kinds whose page carries an <c>Angle</c> row.</summary>
    public static bool Angled(GridKind k) => k is GridKind.Isometric or GridKind.Triangle;

    /// <summary>
    /// The three families the grid is made of, as inclinations and perpendicular
    /// spacings.
    ///
    /// <para>The diagonals' spacing is not a free parameter: it is whatever makes
    /// them meet the straight family at every node, and that is what forces the
    /// cell to change shape as the angle moves.</para>
    ///
    /// <para><b>Isometric</b> - verticals every <c>s</c>, which the angle never
    /// touches. A diagonal climbs <c>s.tan(t)</c> across one vertical gap, so
    /// consecutive parallels sit <c>2.s.sin(t)</c> apart and the rhombus comes
    /// out <c>2s</c> wide by <c>2.s.tan(t)</c> tall. The width is pinned by the
    /// verticals, so the whole of the shape change lands in the height: lower
    /// angles flatten the rhombus, higher ones stand it up.</para>
    ///
    /// <para><b>Triangle</b> - horizontals every <c>v</c>, which the angle never
    /// touches. The apex sits <c>v/tan(t)</c> along the base, so consecutive
    /// parallels sit <c>2.v.cos(t)</c> apart and the triangle comes out
    /// <c>2.v/tan(t)</c> across the base by <c>v</c> tall. The height is pinned
    /// by the horizontals, so the base carries the change: below 60 the triangle
    /// squats and widens, above it narrows.</para>
    ///
    /// <para>Both reduce to the regular case at their own default - all three
    /// families share one perpendicular spacing at 30 degrees for isometric and
    /// at 60 for the triangle grid - which is why <c>Spacing</c> can mean the
    /// same thing here as it does on a lined or a graph page: the distance
    /// between the straight lines.</para>
    /// </summary>
    /// <param name="portrait">12.2's Orientation, which IS the rigid 90 degree
    /// flip and is therefore applied to all three families together. It is the
    /// only thing on the page that turns the field, and it stays independent of
    /// the angle.</param>
    public static GridFamily[] Families(GridKind kind, double angleDeg, double spacing,
                                        bool portrait)
    {
        double t = Math.Clamp(angleDeg, MinAngle, MaxAngle);
        double r = t * Math.PI / 180.0;
        float s = (float)Math.Max(1e-3, spacing);
        float flip = portrait ? 90f : 0f;
        bool tri = kind == GridKind.Triangle;

        // The straight family: horizontal for the triangle grid, vertical for
        // isometric. Its spacing IS the page's Spacing and its inclination is a
        // constant - neither is a function of the angle, which is the whole of
        // 12.11's first check.
        float straight = tri ? 0f : 90f;
        float diag = (float)(tri ? 2 * s * Math.Cos(r) : 2 * s * Math.Sin(r));

        return new[]
        {
            new GridFamily(straight + flip, s),
            new GridFamily((float)t + flip, diag),
            new GridFamily((float)-t + flip, diag),
        };
    }

    /// <summary>The closest spacing any of this configuration's families will
    /// have. A caller's "too dense to draw" guard has to be applied to the family
    /// that actually gets densest, not to the nominal spacing: at the ends of the
    /// angle range the diagonals are several times finer than the straight
    /// family. Linear in <paramref name="spacing"/>, so a guard can double both
    /// together.</summary>
    public static double FinestPerp(GridKind kind, double angleDeg, double spacing)
    {
        double r = Math.Clamp(angleDeg, MinAngle, MaxAngle) * Math.PI / 180.0;
        double s = Math.Max(1e-3, spacing);
        return Math.Min(s, kind == GridKind.Triangle ? 2 * s * Math.Cos(r)
                                                     : 2 * s * Math.Sin(r));
    }

    /// <summary>Every line of one family that crosses the frame
    /// <paramref name="tl"/>..<paramref name="br"/>, generated from the frame's
    /// own corners so the count is right at any inclination. Walked as a struct
    /// enumerator: this runs three times per grid draw on the canvas, and a
    /// per-frame allocation there is a per-frame allocation during a pan.</summary>
    public static LineWalk Lines(GridFamily f, Vector2 tl, Vector2 br) => new(f, tl, br);

    /// <summary>The walk <see cref="Lines"/> returns. Public only because
    /// <c>foreach</c> needs to see it.</summary>
    public struct LineWalk
    {
        // A frame is finite and so is the useful line count; this is the belt to
        // the caller's density braces, so no combination of spacing, angle and
        // zoom can spin here.
        private const int Cap = 40000;

        private readonly Vector2 _dir, _nrm;
        private readonly float _perp, _kMax, _tMin, _tMax;
        private float _k;
        private int _budget;
        private bool _walking;

        internal LineWalk(GridFamily f, Vector2 tl, Vector2 br)
        {
            float a = f.AngleDeg * MathF.PI / 180f;
            _dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            _nrm = new Vector2(-_dir.Y, _dir.X);
            _perp = MathF.Max(f.Perp, 1e-3f);

            Span<Vector2> c = stackalloc Vector2[4]
                { tl, new(br.X, tl.Y), br, new(tl.X, br.Y) };
            float kMin = float.MaxValue, kMax = float.MinValue;
            float tMin = float.MaxValue, tMax = float.MinValue;
            foreach (var p in c)
            {
                float k = Vector2.Dot(p, _nrm);
                kMin = MathF.Min(kMin, k); kMax = MathF.Max(kMax, k);
                float t = Vector2.Dot(p, _dir);
                tMin = MathF.Min(tMin, t); tMax = MathF.Max(tMax, t);
            }

            _kMax = kMax; _tMin = tMin; _tMax = tMax;
            _k = MathF.Floor(kMin / _perp) * _perp;
            _budget = Cap;
            _walking = false;
            Current = default;
        }

        public GridLine Current { get; private set; }

        public LineWalk GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_walking) _k += _perp; else _walking = true;
            if (_k > _kMax || --_budget < 0) return false;
            Current = new GridLine(_nrm * _k + _dir * _tMin, _nrm * _k + _dir * _tMax);
            return true;
        }
    }
}
