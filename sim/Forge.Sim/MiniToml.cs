using System.Globalization;
using System.Text;

namespace Forge.Sim;

public sealed class TomlParseException(string message) : Exception(message);

/// <summary>
/// A deliberately small, strict TOML reader covering exactly the subset recipes-v0.toml uses:
/// comments, [table], [[array-of-tables]], and values of type integer, string, bool, array, and
/// inline table.
///
/// WHY NOT A NUGET PARSER: D14 named Tomlyn. Tomlyn's restorable API no longer exposes the
/// document-model entry point (`Toml.ToModel` / `Toml.Parse`) that the model types are reached
/// through -- only a source-generator/reflection serializer that binds by naming convention. That
/// binding is the wrong trade here: a renamed or misspelled TOML key would silently arrive as a
/// default 0, and a zero `speed_base` is a machine that never crafts -- a content typo becoming a
/// silent sim bug. This reader instead THROWS on anything it does not understand, which is the
/// property §2.4 actually needs. It is also zero-dependency on the sim core's load path, which is
/// determinism-relevant surface.
///
/// It is NOT a general TOML implementation and must not be advertised as one. It rejects valid
/// TOML it does not cover (multi-line values, dotted keys, dates, floats) rather than guessing.
/// If content ever needs those, revisit -- see `minitoml-not-general-toml`.
///
/// Values are returned as: long | string | bool | List&lt;object&gt; | Dictionary&lt;string, object&gt;.
/// </summary>
public static class MiniToml
{
    public static Dictionary<string, object> Parse(string text, string sourceName = "<toml>")
    {
        var root = new Dictionary<string, object>();
        Dictionary<string, object> current = root;

        var lines = text.Split('\n');
        for (int ln = 0; ln < lines.Length; ln++)
        {
            var line = StripComment(lines[ln]).Trim();
            if (line.Length == 0) continue;

            string Where(string msg) => $"{sourceName}:{ln + 1}: {msg}";

            if (line.StartsWith("[["))
            {
                if (!line.EndsWith("]]")) throw new TomlParseException(Where($"unterminated [[array-of-tables]] header: '{line}'"));
                var key = line[2..^2].Trim();
                RequireBareKey(key, Where);
                if (!root.TryGetValue(key, out var existing))
                    root[key] = existing = new List<object>();
                if (existing is not List<object> list)
                    throw new TomlParseException(Where($"'{key}' is already a non-array value"));
                current = new Dictionary<string, object>();
                list.Add(current);
            }
            else if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new TomlParseException(Where($"unterminated [table] header: '{line}'"));
                var key = line[1..^1].Trim();
                RequireBareKey(key, Where);
                if (root.ContainsKey(key)) throw new TomlParseException(Where($"duplicate table '{key}'"));
                current = new Dictionary<string, object>();
                root[key] = current;
            }
            else
            {
                int eq = IndexOfTopLevelEquals(line);
                if (eq < 0) throw new TomlParseException(Where($"expected 'key = value', got '{line}'"));
                var key = line[..eq].Trim();
                RequireBareKey(key, Where);
                var rest = line[(eq + 1)..];
                int pos = 0;
                object val;
                try
                {
                    val = ParseValue(rest, ref pos);
                    SkipWs(rest, ref pos);
                }
                catch (TomlParseException e)
                {
                    throw new TomlParseException(Where(e.Message));
                }
                if (pos != rest.Length)
                    throw new TomlParseException(Where($"trailing content after value for '{key}': '{rest[pos..]}'"));
                if (current.ContainsKey(key)) throw new TomlParseException(Where($"duplicate key '{key}'"));
                current[key] = val;
            }
        }
        return root;
    }

    private static void RequireBareKey(string key, Func<string, string> where)
    {
        if (key.Length == 0) throw new TomlParseException(where("empty key"));
        foreach (var ch in key)
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-')
                throw new TomlParseException(where($"unsupported key '{key}' (this reader handles bare keys only, not dotted or quoted keys)"));
    }

    /// <summary>Strip a # comment, respecting quoted strings so a '#' inside a string survives.</summary>
    private static string StripComment(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
            else if (c == '#' && !inStr) return line[..i];
        }
        return line;
    }

    private static int IndexOfTopLevelEquals(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
            else if (c == '=' && !inStr) return i;
        }
        return -1;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r')) i++;
    }

    private static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new TomlParseException("expected a value, found end of line");

        var c = s[i];
        if (c == '"') return ParseString(s, ref i);
        if (c == '[') return ParseArray(s, ref i);
        if (c == '{') return ParseInlineTable(s, ref i);
        if (Match(s, ref i, "true")) return true;
        if (Match(s, ref i, "false")) return false;
        return ParseInteger(s, ref i);
    }

    private static bool Match(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        int end = i + word.Length;
        if (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_')) return false;
        i = end;
        return true;
    }

    private static string ParseString(string s, ref int i)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new TomlParseException("unterminated string");
            var c = s[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (i >= s.Length) throw new TomlParseException("unterminated escape");
                var e = s[i++];
                sb.Append(e switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => throw new TomlParseException($"unsupported escape '\\{e}'"),
                });
            }
            else sb.Append(c);
        }
    }

    private static long ParseInteger(string s, ref int i)
    {
        int start = i;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        int digits = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '_'))
        {
            if (s[i] != '_') digits++;
            i++;
        }
        if (digits == 0) throw new TomlParseException($"expected an integer at '{s[start..Math.Min(s.Length, start + 12)]}'");
        // Reject floats explicitly rather than silently truncating: the sim is integer-only (§1.2),
        // so a float in content is a content bug, not something to round away.
        if (i < s.Length && (s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            throw new TomlParseException($"floating-point values are not supported; content must be integer (§1.2). At '{s[start..]}'");
        var raw = s[start..i].Replace("_", "");
        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
            throw new TomlParseException($"malformed integer '{raw}'");
        return v;
    }

    private static List<object> ParseArray(string s, ref int i)
    {
        i++; // [
        var list = new List<object>();
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return list; }
        while (true)
        {
            list.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (i >= s.Length) throw new TomlParseException("unterminated array");
            if (s[i] == ',') { i++; SkipWs(s, ref i); if (i < s.Length && s[i] == ']') { i++; return list; } continue; }
            if (s[i] == ']') { i++; return list; }
            throw new TomlParseException($"expected ',' or ']' in array at '{s[i..]}'");
        }
    }

    private static Dictionary<string, object> ParseInlineTable(string s, ref int i)
    {
        i++; // {
        var t = new Dictionary<string, object>();
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return t; }
        while (true)
        {
            SkipWs(s, ref i);
            int ks = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-')) i++;
            if (i == ks) throw new TomlParseException($"expected a key in inline table at '{s[i..]}'");
            var key = s[ks..i];
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '=') throw new TomlParseException($"expected '=' after inline-table key '{key}'");
            i++;
            t[key] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length) throw new TomlParseException("unterminated inline table");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return t; }
            throw new TomlParseException($"expected ',' or '}}' in inline table at '{s[i..]}'");
        }
    }
}
