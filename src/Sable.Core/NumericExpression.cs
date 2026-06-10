using System;
using System.Globalization;

namespace Sable.Core;

/// <summary>
/// Tiny calculator for numeric input fields (Figma/Blender-style): plain numbers,
/// arithmetic ("512/2", "30+12*2"), values relative to the current one via a leading
/// operator ("+10" adds, "/2" halves), and percentages of the current value ("50%",
/// "+10%"). Left-to-right with the usual * / precedence. Culture-tolerant ("," = ".").
/// </summary>
public static class NumericExpression
{
    /// <summary>Evaluate <paramref name="text"/> against <paramref name="current"/>.
    /// False when the input isn't a valid expression.</summary>
    public static bool TryEval(string? text, double current, out double result)
    {
        result = current;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(',', '.');

        // leading operator = relative to the current value ("+10", "-5", "*2", "/2")
        char lead = s[0];
        bool relative = lead is '+' or '-' or '*' or '/';
        if (relative) s = s.Substring(1).TrimStart();
        if (s.Length == 0) return false;

        int pos = 0;
        if (!ParseExpr(s, ref pos, current, out var v)) return false;
        SkipWs(s, ref pos);
        if (pos != s.Length) return false;
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;

        result = relative
            ? lead switch
            {
                '+' => current + v,
                '-' => current - v,
                '*' => current * v,
                _ => Math.Abs(v) < 1e-12 ? current : current / v,
            }
            : v;
        return !double.IsNaN(result) && !double.IsInfinity(result);
    }

    private static bool ParseExpr(string s, ref int pos, double current, out double v)
    {
        if (!ParseTerm(s, ref pos, current, out v)) return false;
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length || (s[pos] != '+' && s[pos] != '-')) return true;
            char op = s[pos++];
            if (!ParseTerm(s, ref pos, current, out var rhs)) return false;
            v = op == '+' ? v + rhs : v - rhs;
        }
    }

    private static bool ParseTerm(string s, ref int pos, double current, out double v)
    {
        if (!ParseFactor(s, ref pos, current, out v)) return false;
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/')) return true;
            char op = s[pos++];
            if (!ParseFactor(s, ref pos, current, out var rhs)) return false;
            if (op == '/' && Math.Abs(rhs) < 1e-12) return false;
            v = op == '*' ? v * rhs : v / rhs;
        }
    }

    private static bool ParseFactor(string s, ref int pos, double current, out double v)
    {
        v = 0;
        SkipWs(s, ref pos);
        bool neg = false;
        if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) { neg = s[pos] == '-'; pos++; }
        SkipWs(s, ref pos);
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
        if (pos == start) return false;
        if (!double.TryParse(s.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return false;
        if (pos < s.Length && s[pos] == '%') { pos++; v = v / 100.0 * current; }
        if (neg) v = -v;
        return true;
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
