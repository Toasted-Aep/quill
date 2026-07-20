using System.Reflection;
using System.Xml.Linq;

namespace Quill.Helpers;

// UI string lookup over the .resw files under Strings/<lang>/ (#C7).
//
// The .resw files stay the single source of truth and keep their MRT layout, but
// they are also embedded and parsed here rather than read through ResourceLoader:
// MRT resolves a language once per process, and Quill has to repaint every
// code-built surface the moment the picker changes — the same reason ApplyTheme
// rebuilds the tree, the pen strip and the gallery by hand.
public static class Loc
{
    // Tag -> the language's own name, which is what a picker should show.
    public static readonly (string Tag, string Name)[] Languages =
    {
        ("en-US", "English"),
        ("tr",    "Türkçe"),
        ("it",    "Italiano"),
    };

    private static readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    // "" means follow Windows; Current is always a concrete tag from Languages.
    public static string Current { get; private set; } = "en-US";

    static Loc() => SetLanguage("");

    // Empty/unknown tag falls back to the Windows UI language, then to en-US, so
    // a hand-edited settings file cannot leave the app without any strings.
    public static void SetLanguage(string? tag)
    {
        _fallback = Table("en-US");
        string resolved = Resolve(tag);
        Current = resolved;
        _active = Table(resolved);
    }

    private static string Resolve(string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag) && Languages.Any(l => l.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            return Languages.First(l => l.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)).Tag;
        try
        {
            string sys = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var hit = Languages.FirstOrDefault(l => l.Tag.StartsWith(sys, StringComparison.OrdinalIgnoreCase));
            if (hit.Tag != null) return hit.Tag;
        }
        catch { }
        return "en-US";
    }

    // Lookup. Missing keys fall through to English and finally to the key itself,
    // which makes an untranslated string visible without breaking the layout.
    public static string T(string key)
    {
        if (_active.TryGetValue(key, out var v) && v.Length > 0) return v;
        if (_fallback.TryGetValue(key, out var e) && e.Length > 0) return e;
        return key;
    }

    // Composed text always goes through one format string with placeholders —
    // never a sentence glued together from translated fragments.
    public static string T(string key, params object?[] args)
    {
        try { return string.Format(T(key), args); }
        catch (FormatException) { return T(key); }
    }

    private static Dictionary<string, string> Table(string tag)
    {
        if (_tables.TryGetValue(tag, out var cached)) return cached;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("Quill.Strings." + tag + ".resw");
            if (s != null)
                foreach (var d in XDocument.Load(s).Root!.Elements("data"))
                {
                    var name = (string?)d.Attribute("name");
                    if (name != null) map[name] = (string?)d.Element("value") ?? "";
                }
        }
        catch { }
        _tables[tag] = map;
        return map;
    }
}
