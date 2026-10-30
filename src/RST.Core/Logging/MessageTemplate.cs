// rst-c's own (not vendored): Serilog-style message templates for the in-house logger (decision #33).
// Renders what a Serilog file sink wrote for the output template's {Message:lj}, closely enough that the
// logs read the same:
//   {Name}            the argument; strings bare, numbers and dates in the invariant culture
//   {Name:format}     IFormattable.ToString(format, InvariantCulture), for example {Due:u} or {Hr:X8}
//   {Name,10} {N,-10} padded right- or left-aligned (with or without a format)
//   {@Name}           destructured: a scalar as above, anything else as JSON
//   {$Name}           stringified: ToString()
//   {0} {1}           positional, when every token in the template is a number
//   {{ and }}         literal braces
// A sequence or dictionary renders as JSON (["a","b"], {"k":1}), as Serilog's :j did. A template token
// with no argument stays as written; a malformed token is literal text. Rendering never throws.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Logging;

public static class MessageTemplate
{
    private enum Hint
    {
        Default,
        Destructure,
        Stringify,
    }

    /// <summary>A literal run (<see cref="Name"/> null) or a property token.</summary>
    private sealed record Token(string Text, string? Name, Hint Hint, int Alignment, string? Format);

    private sealed record Parsed(Token[] Tokens, bool Positional);

    private const int CacheLimit = 1000;
    private static readonly ConcurrentDictionary<string, Parsed> Cache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 10,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The template with <paramref name="values"/> filled in. Never throws.</summary>
    public static string Render(string? template, IReadOnlyList<object?>? values)
    {
        if (string.IsNullOrEmpty(template)) return "";
        try
        {
            values ??= Array.Empty<object?>();
            var parsed = Parse(template!);
            var sb = new StringBuilder(template!.Length + 32);
            var next = 0;
            foreach (var t in parsed.Tokens)
            {
                if (t.Name is null)
                {
                    sb.Append(t.Text);
                    continue;
                }
                int index;
                if (!parsed.Positional) index = next++;
                else if (!int.TryParse(t.Name, NumberStyles.None, CultureInfo.InvariantCulture, out index)) index = -1; // {99999999999}: unmatched
                if (index < 0 || index >= values.Count)
                {
                    sb.Append(t.Text);
                    continue;
                }
                Pad(sb, RenderValue(values[index], t.Hint, t.Format), t.Alignment);
            }
            return sb.ToString();
        }
        catch
        {
            return template!;
        }
    }

    private static void Pad(StringBuilder sb, string text, int alignment)
    {
        var width = Math.Abs(alignment);
        if (alignment > 0 && text.Length < width) sb.Append(' ', width - text.Length);
        sb.Append(text);
        if (alignment < 0 && text.Length < width) sb.Append(' ', width - text.Length);
    }

    private static Parsed Parse(string template)
    {
        if (Cache.TryGetValue(template, out var cached)) return cached;
        var parsed = ParseCore(template);
        if (Cache.Count < CacheLimit) Cache.TryAdd(template, parsed);
        return parsed;
    }

    private static Parsed ParseCore(string template)
    {
        var tokens = new List<Token>();
        var text = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    text.Append('{');
                    i += 2;
                    continue;
                }
                var close = template.IndexOf('}', i + 1);
                var open = template.IndexOf('{', i + 1);
                if (close < 0 || (open >= 0 && open < close))
                {
                    // An unclosed brace (or one reopened before it closes) is literal text.
                    var end = open >= 0 && (close < 0 || open < close) ? open : template.Length;
                    text.Append(template, i, end - i);
                    i = end;
                    continue;
                }
                var raw = template.Substring(i, close - i + 1);
                var token = ParseProperty(raw);
                if (token is null)
                {
                    text.Append(raw);
                }
                else
                {
                    if (text.Length > 0) tokens.Add(new Token(text.ToString(), null, Hint.Default, 0, null));
                    text.Clear();
                    tokens.Add(token);
                }
                i = close + 1;
                continue;
            }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                text.Append('}');
                i += 2;
                continue;
            }
            text.Append(c);
            i++;
        }
        if (text.Length > 0) tokens.Add(new Token(text.ToString(), null, Hint.Default, 0, null));

        var any = false;
        var positional = true;
        foreach (var t in tokens)
        {
            if (t.Name is null) continue;
            any = true;
            if (!IsDigits(t.Name)) positional = false;
        }
        return new Parsed(tokens.ToArray(), any && positional);
    }

    /// <summary><c>{[@$]Name[,alignment][:format]}</c>, or null when it is not a valid token.</summary>
    private static Token? ParseProperty(string raw)
    {
        var inner = raw.Substring(1, raw.Length - 2);
        if (inner.Length == 0) return null;
        var hint = Hint.Default;
        if (inner[0] == '@') hint = Hint.Destructure;
        else if (inner[0] == '$') hint = Hint.Stringify;
        if (hint != Hint.Default) inner = inner.Substring(1);

        string? format = null;
        var colon = inner.IndexOf(':');
        if (colon >= 0)
        {
            format = inner.Substring(colon + 1);
            inner = inner.Substring(0, colon);
            if (format.Length == 0) return null;
        }
        var alignment = 0;
        var comma = inner.IndexOf(',');
        if (comma >= 0)
        {
            var a = inner.Substring(comma + 1);
            inner = inner.Substring(0, comma);
            if (!int.TryParse(a, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out alignment) || alignment == 0) return null;
        }
        if (inner.Length == 0) return null;
        foreach (var ch in inner)
            if (!(char.IsLetterOrDigit(ch) || ch == '_')) return null;
        return new Token(raw, inner, hint, alignment, format);
    }

    private static bool IsDigits(string s)
    {
        foreach (var ch in s)
            if (ch < '0' || ch > '9') return false;
        return s.Length > 0;
    }

    private static string RenderValue(object? value, Hint hint, string? format)
    {
        try
        {
            if (value is null) return "null";
            if (hint == Hint.Stringify) return value.ToString() ?? "";
            if (value is string s) return s;
            if (IsScalar(value)) return Scalar(value, format);
            if (value is IDictionary || value is IEnumerable)
            {
                var sb = new StringBuilder();
                Json(sb, value, hint == Hint.Destructure, 0);
                return sb.ToString();
            }
            if (hint == Hint.Destructure) return JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
            return value.ToString() ?? "";
        }
        catch
        {
            try { return value?.ToString() ?? "null"; }
            catch { return "?"; }
        }
    }

    private static string Scalar(object value, string? format) =>
        value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value.ToString() ?? "";

    /// <summary>Serilog's scalar set: primitives, enums and the common value types.</summary>
    private static bool IsScalar(object value) =>
        value is bool or char or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or nint or nuint
            or DateTime or DateTimeOffset or TimeSpan or Guid or Uri or DateOnly or TimeOnly
            or Enum or System.Numerics.BigInteger;

    /// <summary>Serilog's JsonValueFormatter, for a sequence or dictionary (its elements: scalars, nested sequences, else their text).</summary>
    private static void Json(StringBuilder sb, object? value, bool destructure, int depth)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                JsonString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or System.Numerics.BigInteger:
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append(float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "\"" + f.ToString(CultureInfo.InvariantCulture) + "\"");
                return;
            case double d:
                sb.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "\"" + d.ToString(CultureInfo.InvariantCulture) + "\"");
                return;
            case DateTime dt:
                JsonString(sb, dt.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dto:
                JsonString(sb, dto.ToString("O", CultureInfo.InvariantCulture));
                return;
        }
        if (depth >= 10)
        {
            JsonString(sb, value.ToString() ?? "");
            return;
        }
        if (value is IDictionary dict)
        {
            sb.Append('{');
            var first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                JsonString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "");
                sb.Append(':');
                Json(sb, e.Value, destructure, depth + 1);
            }
            sb.Append('}');
            return;
        }
        if (value is IEnumerable seq)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in seq)
            {
                if (!first) sb.Append(',');
                first = false;
                Json(sb, item, destructure, depth + 1);
            }
            sb.Append(']');
            return;
        }
        if (IsScalar(value))
        {
            JsonString(sb, Scalar(value, null));
            return;
        }
        if (destructure)
        {
            try
            {
                sb.Append(JsonSerializer.Serialize(value, value.GetType(), JsonOptions));
                return;
            }
            catch
            {
                // Falls through to the text.
            }
        }
        JsonString(sb, value.ToString() ?? "");
    }

    private static void JsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\f': sb.Append("\\f"); break;
                case '\b': sb.Append("\\b"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
