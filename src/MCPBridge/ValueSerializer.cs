using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Keeps unknown/reference objects alive and addressable across MCP calls.
    /// Unity GetInstanceID() handles are unstable across scene loads/restarts,
    /// so non-Unity objects get a bridge-side registry id instead.
    /// </summary>
    public static class ObjectRegistry
    {
        private static int _nextId = 1000000;
        private static readonly Dictionary<int, object> _refs = new Dictionary<int, object>();
        private static readonly object _lock = new object();

        public static int Register(object obj)
        {
            if (obj == null) return 0;
            lock (_lock)
            {
                // Reuse id if the same reference is already registered.
                foreach (var kv in _refs)
                {
                    if (ReferenceEquals(kv.Value, obj)) return kv.Key;
                }
                int id = System.Threading.Interlocked.Increment(ref _nextId);
                _refs[id] = obj;
                // Bound growth: drop oldest entries past 512.
                if (_refs.Count > 512)
                {
                    int oldest = int.MaxValue;
                    foreach (var k in _refs.Keys) if (k < oldest) oldest = k;
                    _refs.Remove(oldest);
                }
                return id;
            }
        }

        public static bool TryGet(int id, out object obj)
        {
            lock (_lock) return _refs.TryGetValue(id, out obj);
        }
    }

    /// <summary>
    /// Ensures IL2CPP background threads are attached before touching managed memory.
    /// Main-thread requests (the normal MCP path) are already attached; this is a
    /// no-op cache for any future background execution. Never throws.
    /// </summary>
    public static class Il2CppThreadAttacher
    {
        private static bool _attached;

        public static void AttachOnce()
        {
            if (_attached) return;
#if CPP
            try
            {
                IntPtr domain = Il2CppInterop.Runtime.IL2CPP.il2cpp_domain_get();
                if (domain != IntPtr.Zero)
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_thread_attach(domain);
                _attached = true;
            }
            catch { _attached = true; }
#else
            // Mono backend: no il2cpp domain exists; managed threads need no attach.
            _attached = true;
#endif
        }
    }

    /// <summary>
    /// P3 marshalling rules shared by invoke_static / invoke_method / execute_csharp:
    ///  - strings + primitives  -> native JSON values
    ///  - byte[] / Il2CppStructArray&lt;byte&gt; -> { encoding, data } (hex default, base64 optional)
    ///  - unknown objects -> { type, instance_id } handle (+ registry for non-Unity refs)
    /// </summary>
    public static class ValueSerializer
    {
        public static object Serialize(object value, string encoding = "hex")
        {
            if (value == null) return null;
            Type t = value.GetType();

            if (t == typeof(string))
            {
                string s = (string)value;
                if (s.Length > BridgeConfig.StringLimit) return TruncateText(s);
                return s;
            }
            if (t.IsPrimitive || t == typeof(decimal))
                return value;
            if (t.IsEnum)
                return value.ToString();
            if (t == typeof(byte[]))
                return EncodeBytes((byte[])value, encoding);

            // Arrays / lists inline as JSON arrays (capped) so reflection
            // listings and similar snippets come back usable, not as handles.
            if (value is System.Collections.IList list)
            {
                int total = list.Count;
                int take = Math.Min(total, BridgeConfig.ArrayLimit);
                var items = new List<object>(take);
                for (int i = 0; i < take; i++)
                {
                    try { items.Add(Serialize(list[i], encoding)); }
                    catch { items.Add("<unserializable>"); }
                }
                return new Dictionary<string, object>
                {
                    { "length", total },
                    { "items", items }
                };
            }

            string full = t.FullName ?? t.Name;

            // Il2Cpp arrays (salt etc.): read via Length + indexer, no raw layout hacks.
            // Byte struct arrays encode as hex/base64; all others inline element-wise.
            if (full.StartsWith("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2Cpp"))
            {
                try
                {
                    var lenProp = t.GetProperty("Length") ?? t.GetProperty("Count");
                    int len = Convert.ToInt32(lenProp.GetValue(value, null));
                    var indexer = t.GetProperty("Item");
                    if (indexer != null && len >= 0)
                    {
                        Type elemType = indexer.PropertyType;
                        if (elemType == typeof(byte))
                        {
                            int n = Math.Min(len, BridgeConfig.ByteLimit);
                            var bytes = new byte[n];
                            for (int i = 0; i < n; i++)
                                bytes[i] = Convert.ToByte(indexer.GetValue(value, new object[] { i }));
                            var encoded = EncodeBytes(bytes, encoding);
                            encoded["length"] = len;
                            if (len > n) encoded["truncated"] = true;
                            return encoded;
                        }
                        int m = Math.Min(len, BridgeConfig.ArrayLimit);
                        var items = new List<object>(m);
                        for (int i = 0; i < m; i++)
                        {
                            try { items.Add(Serialize(indexer.GetValue(value, new object[] { i }), encoding)); }
                            catch { items.Add("<unserializable>"); }
                        }
                        return new Dictionary<string, object> { { "length", len }, { "items", items } };
                    }
                }
                catch { }
            }

            // Unity objects keep their GetInstanceID() handle convention.
            if (value is UnityEngine.Object uobj)
            {
                return new Dictionary<string, object>
                {
                    { "type", full },
                    { "instance_id", uobj.GetInstanceID() },
                    { "preview", SafePreview(value) }
                };
            }

            // Unknown reference type -> registry handle + preview string.
            int rid = ObjectRegistry.Register(value);
            return new Dictionary<string, object>
            {
                { "type", full },
                { "instance_id", rid },
                { "preview", SafePreview(value) }
            };
        }

        public static Dictionary<string, object> EncodeBytes(byte[] bytes, string encoding)
        {
            bytes = bytes ?? new byte[0];
            bool b64 = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase);
            if (bytes.Length > BridgeConfig.ByteLimit)
            {
                int headLen = Math.Min(2048, bytes.Length);
                int tailLen = Math.Min(512, bytes.Length - headLen);
                var head = new byte[headLen];
                var tail = new byte[tailLen];
                Array.Copy(bytes, 0, head, 0, headLen);
                Array.Copy(bytes, bytes.Length - tailLen, tail, 0, tailLen);
                return new Dictionary<string, object>
                {
                    { "encoding", b64 ? "base64" : "hex" },
                    { "truncated", true },
                    { "length", bytes.Length },
                    { "sha256", Sha256Hex(bytes) },
                    { "head", b64 ? Convert.ToBase64String(head) : ToHex(head) },
                    { "tail", b64 ? Convert.ToBase64String(tail) : ToHex(tail) }
                };
            }
            return new Dictionary<string, object>
            {
                { "encoding", b64 ? "base64" : "hex" },
                { "data", b64 ? Convert.ToBase64String(bytes) : ToHex(bytes) }
            };
        }

        public static Dictionary<string, object> TruncateText(string s)
        {
            int headLen = Math.Min(4000, s.Length);
            int tailLen = Math.Min(1000, s.Length - headLen);
            return new Dictionary<string, object>
            {
                { "truncated", true },
                { "length", s.Length },
                { "sha256", Sha256Hex(Encoding.UTF8.GetBytes(s)) },
                { "head", s.Substring(0, headLen) },
                { "tail", tailLen > 0 ? s.Substring(s.Length - tailLen, tailLen) : "" }
            };
        }

        public static string Sha256Hex(byte[] bytes)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(bytes ?? new byte[0]);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] DecodeBytes(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (IsHex(s)) // hex
            {
                if (s.Length % 2 == 1) s = "0" + s;
                var out_ = new byte[s.Length / 2];
                for (int i = 0; i < out_.Length; i++)
                    out_[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
                return out_;
            }
            try { return Convert.FromBase64String(s); } // base64
            catch { return Encoding.UTF8.GetBytes(s); }
        }

        public static bool IsHex(string s)
        {
            if (s.Length == 0 || s.Length % 2 == 1) return false;
            foreach (char c in s)
            {
                bool h = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!h) return false;
            }
            return true;
        }

        /// <summary>Convert one JSON arg to the target managed parameter type.</summary>
        public static object ConvertJsonElement(JsonElement el, Type target)
        {
            Type t = target.IsByRef ? target.GetElementType() : target;
            if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
                return t.IsValueType ? Activator.CreateInstance(t) : null;

            try
            {
                if (t == typeof(string)) return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                if (t == typeof(bool)) return el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.String && bool.Parse(el.GetString()));
                if (t == typeof(int)) return el.TryGetInt32(out int i) ? i : (int)el.GetDouble();
                if (t == typeof(long)) return el.TryGetInt64(out long l) ? l : (long)el.GetDouble();
                if (t == typeof(float)) return el.ValueKind == JsonValueKind.String ? float.Parse(el.GetString()) : (float)el.GetDouble();
                if (t == typeof(double)) return el.ValueKind == JsonValueKind.String ? double.Parse(el.GetString()) : el.GetDouble();
                if (t == typeof(byte)) return el.TryGetByte(out byte b) ? b : (byte)el.GetInt32();
                if (t == typeof(byte[]))
                {
                    if (el.ValueKind == JsonValueKind.String) return DecodeBytes(el.GetString());
                    if (el.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<byte>();
                        foreach (var item in el.EnumerateArray()) list.Add((byte)item.GetInt32());
                        return list.ToArray();
                    }
                    return null;
                }
                if (t.IsEnum)
                {
                    string s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                    return Enum.Parse(t, s);
                }
                // { instance_id } handle -> registry object
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("instance_id", out var idProp))
                {
                    int id = idProp.GetInt32();
                    if (ObjectRegistry.TryGet(id, out object reg) && t.IsInstanceOfType(reg)) return reg;
                }
                if (t == typeof(object))
                {
                    switch (el.ValueKind)
                    {
                        case JsonValueKind.String: return el.GetString();
                        case JsonValueKind.Number:
                            if (el.TryGetInt32(out int oi)) return oi;
                            if (el.TryGetInt64(out long ol)) return ol;
                            return el.GetDouble();
                        case JsonValueKind.True: return true;
                        case JsonValueKind.False: return false;
                        default: return null;
                    }
                }
                // Last resort: raw text conversion (keeps old ConvertValue behaviour for compat).
                string raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                return Convert.ChangeType(raw, t);
            }
            catch
            {
                return t.IsValueType ? Activator.CreateInstance(t) : null;
            }
        }

        public static List<JsonElement> GetArgsArray(JsonElement paramsEl)
        {
            var list = new List<JsonElement>();
            if (paramsEl.TryGetProperty("args", out var a) || paramsEl.TryGetProperty("Args", out a))
            {
                if (a.ValueKind == JsonValueKind.Array)
                    foreach (var item in a.EnumerateArray()) list.Add(item);
            }
            return list;
        }

        private static string SafePreview(object value)
        {
            try
            {
                string s = value.ToString();
                return s != null && s.Length > 256 ? s.Substring(0, 256) + "..." : (s ?? "null");
            }
            catch { return "<unprintable>"; }
        }
    }
}
