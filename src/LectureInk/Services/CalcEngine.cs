using System.Globalization;

namespace LectureInk.Services;

/// <summary>
/// Recursive-descent scientific expression evaluator.
/// Supports + - * / ^ mod ! parentheses, constants pi/e, implicit
/// multiplication (2pi, 3(4+1)), trig/hyperbolic/inverse functions,
/// sqrt/cbrt/abs/log/ln/exp, with degree or radian trig mode.
/// </summary>
public static class CalcEngine
{
    private static string _s = "";
    private static int _i;
    private static bool _deg;
    private static bool _hasX;
    private static double _xv;

    public static bool TryEvaluate(string expr, bool degrees, out double result, out string error)
    {
        _hasX = false;
        return TryEvaluateCore(expr, degrees, out result, out error);
    }

    public static bool TryEvaluate(string expr, bool degrees, double x, out double result, out string error)
    {
        _hasX = true;
        _xv = x;
        return TryEvaluateCore(expr, degrees, out result, out error);
    }

    private static bool TryEvaluateCore(string expr, bool degrees, out double result, out string error)
    {
        result = 0;
        error = "";
        try
        {
            _s = expr.Replace("×", "*").Replace("÷", "/").Replace("−", "-")
                     .Replace("π", "pi").Replace("√", "sqrt").Replace("∛", "cbrt");
            _i = 0;
            _deg = degrees;
            double v = ParseExpr();
            SkipWs();
            if (_i < _s.Length) { error = "Unexpected input"; return false; }
            if (double.IsNaN(v) || double.IsInfinity(v)) { error = "Undefined"; return false; }
            result = v;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void SkipWs()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
    }

    private static bool Match(string kw)
    {
        SkipWs();
        if (_i + kw.Length <= _s.Length &&
            string.Compare(_s, _i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            // keywords made of letters must not be followed by another letter
            if (char.IsLetter(kw[^1]) && _i + kw.Length < _s.Length && char.IsLetter(_s[_i + kw.Length]))
                return false;
            _i += kw.Length;
            return true;
        }
        return false;
    }

    private static char Peek()
    {
        SkipWs();
        return _i < _s.Length ? _s[_i] : '\0';
    }

    private static double ParseExpr()
    {
        double v = ParseTerm();
        while (true)
        {
            char c = Peek();
            if (c == '+') { _i++; v += ParseTerm(); }
            else if (c == '-') { _i++; v -= ParseTerm(); }
            else break;
        }
        return v;
    }

    private static double ParseTerm()
    {
        double v = ParseFactor();
        while (true)
        {
            char c = Peek();
            if (c == '*') { _i++; v *= ParseFactor(); }
            else if (c == '/') { _i++; v /= ParseFactor(); }
            else if (Match("mod")) { v %= ParseFactor(); }
            else if (c == '(' || char.IsLetter(c) || char.IsDigit(c) || c == '.')
            {
                // implicit multiplication: 2pi, 2(3+1), 2sin(x)
                v *= ParseFactor();
            }
            else break;
        }
        return v;
    }

    private static double ParseFactor()
    {
        double v = ParseUnary();
        if (Peek() == '^')
        {
            _i++;
            double e = ParseFactor(); // right associative
            v = Math.Pow(v, e);
        }
        return v;
    }

    private static double ParseUnary()
    {
        char c = Peek();
        if (c == '-') { _i++; return -ParseUnary(); }
        if (c == '+') { _i++; return ParseUnary(); }
        return ParsePostfix();
    }

    private static double ParsePostfix()
    {
        double v = ParsePrimary();
        while (Peek() == '!')
        {
            _i++;
            v = Factorial(v);
        }
        return v;
    }

    private static double Factorial(double n)
    {
        if (n < 0 || Math.Abs(n - Math.Round(n)) > 1e-9 || n > 170)
            throw new Exception("n! needs a whole number 0–170");
        double r = 1;
        for (int k = 2; k <= (int)Math.Round(n); k++) r *= k;
        return r;
    }

    private static readonly string[] Funcs =
    {
        // longest first so 'asinh' wins over 'asin' over 'sin'
        "asinh","acosh","atanh","asech","acsch","acoth",
        "sinh","cosh","tanh","sech","csch","coth",
        "asin","acos","atan","asec","acsc","acot",
        "sqrt","cbrt","abs","exp","log","ln",
        "sin","cos","tan","sec","csc","cot"
    };

    private static double ParsePrimary()
    {
        SkipWs();
        if (_i >= _s.Length) throw new Exception("Unexpected end");

        char c = _s[_i];
        if (c == '(')
        {
            _i++;
            double v = ParseExpr();
            if (Peek() == ')') _i++;
            return v;
        }
        if (char.IsDigit(c) || c == '.')
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            return double.Parse(_s[start.._i], CultureInfo.InvariantCulture);
        }
        if (Match("pi")) return Math.PI;
        if (_hasX && Match("x")) return _xv;
        foreach (var f in Funcs)
        {
            int save = _i;
            if (Match(f))
            {
                if (Peek() == '(')
                {
                    _i++;
                    double a = ParseExpr();
                    if (Peek() == ')') _i++;
                    return ApplyFunc(f, a);
                }
                _i = save;
                break;
            }
        }
        if (Match("e")) return Math.E;
        throw new Exception($"Unexpected '{c}'");
    }

    private static double ToRad(double a) => _deg ? a * Math.PI / 180 : a;
    private static double FromRad(double a) => _deg ? a * 180 / Math.PI : a;

    private static double ApplyFunc(string f, double a) => f switch
    {
        "sin" => Math.Sin(ToRad(a)),
        "cos" => Math.Cos(ToRad(a)),
        "tan" => Math.Tan(ToRad(a)),
        "sec" => 1 / Math.Cos(ToRad(a)),
        "csc" => 1 / Math.Sin(ToRad(a)),
        "cot" => 1 / Math.Tan(ToRad(a)),
        "asin" => FromRad(Math.Asin(a)),
        "acos" => FromRad(Math.Acos(a)),
        "atan" => FromRad(Math.Atan(a)),
        "asec" => FromRad(Math.Acos(1 / a)),
        "acsc" => FromRad(Math.Asin(1 / a)),
        "acot" => FromRad(Math.Atan(1 / a)),
        "sinh" => Math.Sinh(a),
        "cosh" => Math.Cosh(a),
        "tanh" => Math.Tanh(a),
        "sech" => 1 / Math.Cosh(a),
        "csch" => 1 / Math.Sinh(a),
        "coth" => 1 / Math.Tanh(a),
        "asinh" => Math.Asinh(a),
        "acosh" => Math.Acosh(a),
        "atanh" => Math.Atanh(a),
        "asech" => Math.Acosh(1 / a),
        "acsch" => Math.Asinh(1 / a),
        "acoth" => Math.Atanh(1 / a),
        "sqrt" => Math.Sqrt(a),
        "cbrt" => Math.Cbrt(a),
        "abs" => Math.Abs(a),
        "exp" => Math.Exp(a),
        "log" => Math.Log10(a),
        "ln" => Math.Log(a),
        _ => throw new Exception("Unknown function")
    };
}

/// <summary>
/// Integer expression evaluator for programmer mode. Numbers are parsed in
/// the given base (2/8/10/16); supports and/or/xor/not, &amp; | ^ ~, shifts,
/// + - * / mod and parentheses. C-style precedence.
/// </summary>
public static class ProgCalc
{
    private static string _s = "";
    private static int _i;
    private static int _base;

    public static bool TryEvaluate(string expr, int numBase, out long result, out string error)
    {
        result = 0;
        error = "";
        try
        {
            _s = expr;
            _i = 0;
            _base = numBase;
            result = ParseOr();
            SkipWs();
            if (_i < _s.Length) { error = "Unexpected input"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void SkipWs()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
    }

    private static bool MatchWord(string kw)
    {
        SkipWs();
        if (_i + kw.Length <= _s.Length &&
            string.Compare(_s, _i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            if (_i + kw.Length < _s.Length && char.IsLetterOrDigit(_s[_i + kw.Length])) return false;
            _i += kw.Length;
            return true;
        }
        return false;
    }

    private static bool MatchSym(string sym)
    {
        SkipWs();
        if (_i + sym.Length <= _s.Length && _s.Substring(_i, sym.Length) == sym)
        {
            _i += sym.Length;
            return true;
        }
        return false;
    }

    private static long ParseOr()
    {
        long v = ParseXor();
        while (true)
        {
            if (MatchWord("or") || MatchSym("|")) v |= ParseXor();
            else break;
        }
        return v;
    }

    private static long ParseXor()
    {
        long v = ParseAnd();
        while (true)
        {
            if (MatchWord("xor") || MatchSym("^")) v ^= ParseAnd();
            else break;
        }
        return v;
    }

    private static long ParseAnd()
    {
        long v = ParseShift();
        while (true)
        {
            if (MatchWord("and") || (!Ahead("&&") && MatchSym("&"))) v &= ParseShift();
            else break;
        }
        return v;
    }

    private static bool Ahead(string sym)
    {
        SkipWs();
        return _i + sym.Length <= _s.Length && _s.Substring(_i, sym.Length) == sym;
    }

    private static long ParseShift()
    {
        long v = ParseAdd();
        while (true)
        {
            if (MatchSym("<<")) v <<= (int)ParseAdd();
            else if (MatchSym(">>")) v >>= (int)ParseAdd();
            else break;
        }
        return v;
    }

    private static long ParseAdd()
    {
        long v = ParseMul();
        while (true)
        {
            SkipWs();
            if (MatchSym("+")) v += ParseMul();
            else if (Ahead(">>") || Ahead("<<")) break;
            else if (MatchSym("-") || MatchSym("−")) v -= ParseMul();
            else break;
        }
        return v;
    }

    private static long ParseMul()
    {
        long v = ParseUnary();
        while (true)
        {
            if (MatchSym("*") || MatchSym("×")) v *= ParseUnary();
            else if (MatchSym("/") || MatchSym("÷"))
            {
                long d = ParseUnary();
                if (d == 0) throw new Exception("Division by zero");
                v /= d;
            }
            else if (MatchWord("mod"))
            {
                long d = ParseUnary();
                if (d == 0) throw new Exception("Division by zero");
                v %= d;
            }
            else break;
        }
        return v;
    }

    private static long ParseUnary()
    {
        SkipWs();
        if (MatchSym("~")) return ~ParseUnary();
        if (MatchWord("not")) return ~ParseUnary();
        if (MatchSym("-") || MatchSym("−")) return -ParseUnary();
        return ParsePrimary();
    }

    private static long ParsePrimary()
    {
        SkipWs();
        if (_i >= _s.Length) throw new Exception("Unexpected end");
        if (_s[_i] == '(')
        {
            _i++;
            long v = ParseOr();
            SkipWs();
            if (_i < _s.Length && _s[_i] == ')') _i++;
            return v;
        }
        int start = _i;
        while (_i < _s.Length && IsBaseDigit(_s[_i])) _i++;
        if (_i == start) throw new Exception($"Unexpected '{_s[_i]}'");
        return Convert.ToInt64(_s[start.._i], _base);
    }

    private static bool IsBaseDigit(char c)
    {
        c = char.ToUpperInvariant(c);
        return _base switch
        {
            2 => c is '0' or '1',
            8 => c >= '0' && c <= '7',
            10 => char.IsDigit(c),
            _ => char.IsDigit(c) || (c >= 'A' && c <= 'F')
        };
    }
}
