#if MONO
// Minimal System.Text.Json-compatible layer for net35 (BepInEx Mono) builds.
// Real System.Text.Json does not exist on .NET 3.5, so this file provides the
// exact API surface the bridge uses: JsonValueKind, JsonElement, JsonSerializer
// and JsonSerializerOptions. Call sites need no #if; only this file is Mono-only.
// Keep behaviour close to STJ defaults: property names as-is (PascalCase),
// invariant number formatting, standard JSON string escaping.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace System.Text.Json
{
    public enum JsonValueKind
    {
        Undefined = 0,
        Object = 1,
        Array = 2,
        String = 3,
        Number = 4,
        True = 5,
        False = 6,
        Null = 7
    }

    public sealed class JsonSerializerOptions
    {
        public bool PropertyNameCaseInsensitive { get; set; }
    }

    public struct JsonElement
    {
        private readonly JsonValueKind _kind;
        private readonly object _value; // Dictionary<string,JsonElement> | List<JsonElement> | string | double | bool | null
        private readonly string _raw;

        internal JsonElement(JsonValueKind kind, object value, string raw)
        {
            _kind = kind;
            _value = value;
            _raw = raw;
        }

        public JsonValueKind ValueKind { get { return _kind; } }

        public static JsonElement Null() { return new JsonElement(JsonValueKind.Null, null, "null"); }

        public bool TryGetProperty(string name, out JsonElement value)
        {
            value = Null();
            if (_kind != JsonValueKind.Object || _value == null) return false;
            var dict = (Dictionary<string, JsonElement>)_value;
            if (dict.TryGetValue(name, out value)) return true;
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
            return false;
        }

        private void Require(JsonValueKind kind)
        {
            if (_kind != kind)
                throw new InvalidOperationException("Expected " + kind + " but was " + _kind + ".");
        }

        public string GetString()
        {
            Require(JsonValueKind.String);
            return (string)_value;
        }

        public bool GetBoolean()
        {
            if (_kind == JsonValueKind.True) return true;
            if (_kind == JsonValueKind.False) return false;
            throw new InvalidOperationException("Expected boolean but was " + _kind + ".");
        }

        public int GetInt32() { return checked((int)GetDouble()); }

        public long GetInt64()
        {
            if (_kind != JsonValueKind.Number) throw new InvalidOperationException("Expected number.");
            string raw = (_raw ?? "").Trim();
            long l;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return checked((long)double.Parse(raw, CultureInfo.InvariantCulture));
        }

        public double GetDouble()
        {
            if (_kind != JsonValueKind.Number) throw new InvalidOperationException("Expected number.");
            return double.Parse((_raw ?? "0").Trim(), CultureInfo.InvariantCulture);
        }

        public bool TryGetInt32(out int value)
        {
            value = 0;
            if (_kind != JsonValueKind.Number) return false;
            string raw = (_raw ?? "").Trim();
            int i;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) { value = i; return true; }
            double d;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                && d >= int.MinValue && d <= int.MaxValue && d == Math.Truncate(d))
            { value = (int)d; return true; }
            return false;
        }

        public bool TryGetInt64(out long value)
        {
            value = 0;
            if (_kind != JsonValueKind.Number) return false;
            string raw = (_raw ?? "").Trim();
            long l;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) { value = l; return true; }
            return false;
        }

        public bool TryGetByte(out byte value)
        {
            value = 0;
            int i;
            if (TryGetInt32(out i) && i >= 0 && i <= 255) { value = (byte)i; return true; }
            return false;
        }

        public IEnumerable<JsonElement> EnumerateArray()
        {
            Require(JsonValueKind.Array);
            return (List<JsonElement>)_value;
        }

        public string GetRawText() { return _raw ?? ""; }

        internal static JsonElement FromObject(Dictionary<string, JsonElement> dict, string raw)
        {
            return new JsonElement(JsonValueKind.Object, dict, raw);
        }

        internal static JsonElement FromArray(List<JsonElement> list, string raw)
        {
            return new JsonElement(JsonValueKind.Array, list, raw);
        }

        internal static JsonElement FromString(string s, string raw)
        {
            return new JsonElement(JsonValueKind.String, s, raw);
        }

        internal static JsonElement FromNumber(double d, string raw)
        {
            return new JsonElement(JsonValueKind.Number, d, raw);
        }

        internal static JsonElement FromBool(bool b)
        {
            return b ? new JsonElement(JsonValueKind.True, true, "true")
                     : new JsonElement(JsonValueKind.False, false, "false");
        }
    }

    internal sealed class JsonParser
    {
        private readonly string _s;
        private int _pos;

        internal JsonParser(string s) { _s = s ?? ""; _pos = 0; }

        internal static JsonElement Parse(string s)
        {
            var p = new JsonParser(s);
            p.SkipWs();
            JsonElement el = p.ParseValue();
            p.SkipWs();
            if (p._pos != p._s.Length) throw new FormatException("Trailing characters after JSON value.");
            return el;
        }

        private void SkipWs()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
        }

        private char Peek() { return _pos < _s.Length ? _s[_pos] : '\0'; }

        private JsonElement ParseValue()
        {
            char c = Peek();
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') { int start = _pos; string s = ParseString(); return JsonElement.FromString(s, _s.Substring(start, _pos - start)); }
            if (c == 't' || c == 'f') return ParseBool();
            if (c == 'n') return ParseNull();
            return ParseNumber();
        }

        private JsonElement ParseObject()
        {
            int start = _pos;
            _pos++; // {
            var dict = new Dictionary<string, JsonElement>();
            SkipWs();
            if (Peek() == '}') { _pos++; return JsonElement.FromObject(dict, _s.Substring(start, _pos - start)); }
            while (true)
            {
                SkipWs();
                if (Peek() != '"') throw new FormatException("Expected string key.");
                string key = ParseString();
                SkipWs();
                if (Peek() != ':') throw new FormatException("Expected ':'.");
                _pos++;
                SkipWs();
                dict[key] = ParseValue();
                SkipWs();
                char c = Peek();
                if (c == ',') { _pos++; continue; }
                if (c == '}') { _pos++; break; }
                throw new FormatException("Expected ',' or '}'.");
            }
            return JsonElement.FromObject(dict, _s.Substring(start, _pos - start));
        }

        private JsonElement ParseArray()
        {
            int start = _pos;
            _pos++; // [
            var list = new List<JsonElement>();
            SkipWs();
            if (Peek() == ']') { _pos++; return JsonElement.FromArray(list, _s.Substring(start, _pos - start)); }
            while (true)
            {
                SkipWs();
                list.Add(ParseValue());
                SkipWs();
                char c = Peek();
                if (c == ',') { _pos++; continue; }
                if (c == ']') { _pos++; break; }
                throw new FormatException("Expected ',' or ']'.");
            }
            return JsonElement.FromArray(list, _s.Substring(start, _pos - start));
        }

        private string ParseString()
        {
            _pos++; // opening "
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _s.Length) throw new FormatException("Unterminated string.");
                char c = _s[_pos++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (_pos >= _s.Length) throw new FormatException("Bad escape.");
                    char e = _s[_pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _s.Length) throw new FormatException("Bad \\u escape.");
                            sb.Append((char)Convert.ToInt32(_s.Substring(_pos, 4), 16));
                            _pos += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private JsonElement ParseBool()
        {
            if (_s.Substring(_pos).StartsWith("true")) { int s = _pos; _pos += 4; return JsonElement.FromBool(true); }
            if (_s.Substring(_pos).StartsWith("false")) { int s = _pos; _pos += 5; return JsonElement.FromBool(false); }
            throw new FormatException("Bad literal.");
        }

        private JsonElement ParseNull()
        {
            if (_s.Substring(_pos).StartsWith("null")) { _pos += 4; return JsonElement.Null(); }
            throw new FormatException("Bad literal.");
        }

        private JsonElement ParseNumber()
        {
            int start = _pos;
            if (Peek() == '-') _pos++;
            while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.' || _s[_pos] == 'e' || _s[_pos] == 'E' || _s[_pos] == '+' || _s[_pos] == '-')) _pos++;
            string raw = _s.Substring(start, _pos - start);
            double d;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("Bad number: " + raw);
            return JsonElement.FromNumber(d, raw);
        }
    }

    public static class JsonSerializer
    {
        public static T Deserialize<T>(string json, JsonSerializerOptions options)
        {
            JsonElement root = JsonParser.Parse(json);
            object obj = DeserializeTo(typeof(T), root);
            return (T)obj;
        }

        private static object DeserializeTo(Type t, JsonElement el)
        {
            if (t == typeof(object))
            {
                // Keep objects/arrays as JsonElement (matches bridge's `Params is JsonElement` use).
                return (object)el;
            }
            if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
                return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t == typeof(string)) return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if (t == typeof(bool)) return el.GetBoolean();
            if (t == typeof(int)) return el.GetInt32();
            if (t == typeof(long)) return el.GetInt64();
            if (t == typeof(double)) return el.GetDouble();
            if (t.IsEnum) return Enum.Parse(t, el.GetString(), true);

            object inst = Activator.CreateInstance(t);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;
                JsonElement child;
                if (!el.TryGetProperty(prop.Name, out child)) continue;
                try { prop.SetValue(inst, DeserializeTo(prop.PropertyType, child), null); }
                catch { }
            }
            return inst;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            Type t = value.GetType();
            if (t == typeof(string)) { WriteString(sb, (string)value); return; }
            if (t == typeof(bool)) { sb.Append(((bool)value) ? "true" : "false"); return; }
            if (t == typeof(char)) { WriteString(sb, value.ToString()); return; }
            if (t.IsEnum) { WriteNumber(sb, Convert.ToInt64(value)); return; }
            if (t == typeof(float)) { WriteNumber(sb, (float)value); return; }
            if (t == typeof(double)) { WriteNumber(sb, (double)value); return; }
            if (t == typeof(decimal)) { WriteNumber(sb, (decimal)value); return; }
            if (t.IsPrimitive)
            {
                if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                    || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong))
                { WriteNumber(sb, Convert.ToInt64(value)); return; }
            }
            if (value is IDictionary)
            {
                var dict = (IDictionary)value;
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key != null ? kv.Key.ToString() : "null");
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (value is IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            sb.Append('{');
            bool f2 = true;
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                object pv;
                try { pv = prop.GetValue(value, null); }
                catch { continue; }
                if (!f2) sb.Append(',');
                f2 = false;
                WriteString(sb, prop.Name);
                sb.Append(':');
                WriteValue(sb, pv);
            }
            sb.Append('}');
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteNumber(StringBuilder sb, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
            sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteNumber(StringBuilder sb, long l)
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteNumber(StringBuilder sb, decimal d)
        {
            sb.Append(d.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) { sb.Append("\\u"); sb.Append(((int)c).ToString("x4")); }
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
#endif
