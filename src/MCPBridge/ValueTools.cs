using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>read_array / decode_byte_array / search_values helpers.</summary>
    public static class ValueTools
    {
        public static Dictionary<string, object> ReadArray(Type type, object instance,
            string member, int limit, int cursor)
        {
            if (limit <= 0) limit = 100;
            if (limit > 250) limit = 250;
            if (cursor < 0) cursor = 0;

            object value;
            Type valueType;
            bool isStatic;
            bool isProperty;
            string error;
            if (!MemberTools.TryRead(type, instance, member, out value, out valueType,
                out isStatic, out isProperty, out error))
                throw new Exception(error);
            if (value == null) throw new Exception("Member is null: " + member);

            int length;
            Func<int, object> get;
            if (!TryIndexer(value, out length, out get))
                throw new Exception("Member is not an array or list: " + member +
                    " (" + (valueType != null ? valueType.FullName : "?") + ")");

            var items = new List<object>();
            int start = Math.Min(cursor, length);
            for (int i = start; i < length && items.Count < limit; i++)
            {
                object item;
                try { item = get(i); }
                catch (Exception ex) { item = "<error: " + ex.Message + ">"; }
                items.Add(SerializeElement(item));
            }
            int consumed = start + items.Count;
            return new Dictionary<string, object>
            {
                { "member", member },
                { "element_type", ElementType(value).FullName },
                { "length", length },
                { "items", items },
                { "next_cursor", consumed < length ? (object)consumed : null }
            };
        }

        public static Dictionary<string, object> DecodeBytes(byte[] bytes, string format)
        {
            bytes = bytes ?? new byte[0];
            string fmt = (format ?? "auto").Trim().ToLowerInvariant();
            if (fmt == "auto")
                fmt = LooksLikeJson(bytes) ? "json" : (LooksLikeText(bytes) ? "text" : "hex");

            var result = new Dictionary<string, object>
            {
                { "format", fmt },
                { "length", bytes.Length },
                { "sha256", ValueSerializer.Sha256Hex(bytes) }
            };

            if (bytes.Length > BridgeConfig.ByteLimit && fmt != "hex" && fmt != "base64")
            {
                result["truncated"] = true;
                result["note"] = "Payload exceeds byte_limit; showing the head only";
                int keep = BridgeConfig.ByteLimit;
                byte[] head = new byte[keep];
                Array.Copy(bytes, head, keep);
                bytes = head;
            }

            switch (fmt)
            {
                case "text":
                    result["text"] = TrimText(Encoding.UTF8.GetString(bytes, 0, bytes.Length));
                    break;
                case "json":
                    try
                    {
                        string json = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                        JsonElement parsed = (JsonElement)JsonSerializer.Deserialize<object>(json,
                            new JsonSerializerOptions());
                        result["json"] = ToPlain(parsed);
                        result["text"] = TrimText(json);
                    }
                    catch (Exception ex)
                    {
                        result["format"] = "hex";
                        result["error"] = "Not valid JSON: " + ex.Message;
                        result["hex"] = TrimText(ValueSerializer.ToHex(bytes));
                    }
                    break;
                case "msgpack":
                    result["value"] = MsgPack.Decode(bytes);
                    break;
                case "base64":
                    result["base64"] = Convert.ToBase64String(bytes);
                    break;
                default:
                    result["format"] = "hex";
                    result["hex"] = TrimText(ValueSerializer.ToHex(bytes));
                    break;
            }
            return result;
        }

        public static bool TryGetByteArray(Type type, object instance, string member,
            out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            object value;
            Type valueType;
            bool isStatic;
            bool isProperty;
            if (!MemberTools.TryRead(type, instance, member, out value, out valueType,
                out isStatic, out isProperty, out error))
                return false;
            bytes = AsByteArray(value);
            if (bytes == null)
            {
                error = "Member is not a byte array: " + member;
                return false;
            }
            return true;
        }

        public static Dictionary<string, object> SearchValues(string needle, string fieldContains,
            string componentType, string scene, bool includeInactive, string matchMode,
            int limit, int cursor, out int scanned, out bool truncated)
        {
            if (limit <= 0) limit = 100;
            if (limit > 250) limit = 250;
            if (cursor < 0) cursor = 0;
            scanned = 0;
            truncated = false;

            var matches = new List<Dictionary<string, object>>();
            Component[] components;
            try { components = Resources.FindObjectsOfTypeAll<Component>(); }
            catch (Exception ex) { throw new Exception("Component scan failed: " + ex.Message); }

            double needleNumber;
            bool needleIsNumber = double.TryParse(needle, NumberStyles.Float,
                CultureInfo.InvariantCulture, out needleNumber);

            foreach (Component component in components)
            {
                if (component == null) continue;
                scanned++;
                if (scanned > BridgeConfig.MaxSearchComponents) { truncated = true; break; }
                try
                {
                    Type type = MCPBridge.ConcreteType(component);
                    string typeName = type != null ? (type.FullName ?? type.Name) : "?";
                    if (!string.IsNullOrEmpty(componentType)
                        && typeName.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    GameObject go = component.gameObject;
                    if (go == null) continue;
                    if (!includeInactive && !go.activeInHierarchy) continue;
                    if (!string.IsNullOrEmpty(scene)
                        && !string.Equals(go.scene.name, scene, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // IL2CPP hands out base-typed proxies and projects native
                    // fields as properties, so re-wrap and scan both members.
                    object target = MCPBridge.ConcreteProxy(component);

                    foreach (FieldInfo field in AllFields(type))
                    {
                        if (field.IsStatic) continue;
                        if (!string.IsNullOrEmpty(fieldContains)
                            && field.Name.IndexOf(fieldContains, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!MemberTools.IsScalar(field.FieldType)) continue;

                        object raw;
                        try { raw = field.GetValue(field.IsStatic ? null : target); }
                        catch { continue; }
                        if (raw == null) continue;

                        string display = raw.ToString();
                        if (!ValueMatches(raw, display, needle, needleNumber, needleIsNumber, matchMode))
                            continue;
                        AddMatch(matches, typeName, component, go, field.Name, field.FieldType, display);
                        if (matches.Count >= 1000) { truncated = true; break; }
                    }
                    if (matches.Count >= 1000) break;

                    foreach (PropertyInfo property in AllProperties(type))
                    {
                        if (!property.CanRead) continue;
                        if (property.GetIndexParameters().Length > 0) continue;
                        if (!IsFieldProjection(property.Name)) continue;
                        MethodInfo getter = property.GetGetMethod(true);
                        if (getter == null || getter.IsStatic) continue;
                        if (!string.IsNullOrEmpty(fieldContains)
                            && property.Name.IndexOf(fieldContains, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!MemberTools.IsScalar(property.PropertyType)) continue;

                        object raw;
                        try { raw = property.GetValue(target, null); }
                        catch { continue; }
                        if (raw == null) continue;

                        string display = raw.ToString();
                        if (!ValueMatches(raw, display, needle, needleNumber, needleIsNumber, matchMode))
                            continue;
                        AddMatch(matches, typeName, component, go, property.Name, property.PropertyType, display);
                        if (matches.Count >= 1000) { truncated = true; break; }
                    }
                }
                catch { }
                if (matches.Count >= 1000) break;
            }

            int start = Math.Min(cursor, matches.Count);
            var items = new List<Dictionary<string, object>>();
            for (int i = start; i < matches.Count && items.Count < limit; i++)
                items.Add(matches[i]);
            int consumed = start + items.Count;
            return new Dictionary<string, object>
            {
                { "items", items },
                { "total", matches.Count },
                { "next_cursor", consumed < matches.Count ? (object)consumed : null },
                { "scanned_components", scanned },
                { "truncated", truncated }
            };
        }

        private static void AddMatch(List<Dictionary<string, object>> matches, string typeName,
            Component component, GameObject go, string member, Type memberType, string display)
        {
            matches.Add(new Dictionary<string, object>
            {
                { "component_type", typeName },
                { "instance_id", component.GetInstanceID() },
                { "handle", Handle.Format(component.GetInstanceID()) },
                { "owner_path", AssemblyInspector.GetGameObjectPath(go) },
                { "scene", go.scene.name },
                { "member", member },
                { "member_type", memberType != null ? memberType.FullName : null },
                { "value", display.Length > 256 ? display.Substring(0, 256) + "..." : display }
            });
        }

        private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static IEnumerable<FieldInfo> AllFields(Type type)
        {
            var seen = new HashSet<string>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(AllFlags); }
                catch { continue; }
                foreach (FieldInfo field in fields)
                {
                    if (seen.Add(field.Name)) yield return field;
                }
            }
        }

        private static IEnumerable<PropertyInfo> AllProperties(Type type)
        {
            var seen = new HashSet<string>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo[] properties;
                try { properties = t.GetProperties(AllFlags); }
                catch { continue; }
                foreach (PropertyInfo property in properties)
                {
                    if (seen.Add(property.Name)) yield return property;
                }
            }
        }

        /// <summary>
        /// Il2CppInterop projects native fields as properties; m_/_ names are
        /// field accessors and safe to evaluate during a scene-wide scan.
        /// </summary>
        private static bool IsFieldProjection(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name[0] == '_') return true;
            return name.Length > 1 && name[0] == 'm' && name[1] == '_';
        }

        private static bool ValueMatches(object raw, string display, string needle,
            double needleNumber, bool needleIsNumber, string matchMode)
        {
            string mode = (matchMode ?? "contains").Trim().ToLowerInvariant();
            double rawNumber;
            if (needleIsNumber && SnapshotStore.TryNumber(raw, out rawNumber))
            {
                if (mode == "exact") return Math.Abs(rawNumber - needleNumber) < 1e-9;
                return display.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            switch (mode)
            {
                case "exact":
                    return string.Equals(display, needle, StringComparison.OrdinalIgnoreCase);
                case "starts_with":
                    return display.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
                case "ends_with":
                    return display.EndsWith(needle, StringComparison.OrdinalIgnoreCase);
                default:
                    return display.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static object SerializeElement(object item)
        {
            object serialized = ValueSerializer.Serialize(item, "hex");
            if (item is UnityEngine.Object)
            {
                var dict = serialized as Dictionary<string, object>;
                if (dict != null)
                    dict["handle"] = Handle.Format(((UnityEngine.Object)item).GetInstanceID());
            }
            return serialized;
        }

        private static Type ElementType(object value)
        {
            if (value == null) return typeof(object);
            Type t = value.GetType();
            if (t.IsArray) return t.GetElementType();
            try
            {
                PropertyInfo item = t.GetProperty("Item");
                if (item != null) return item.PropertyType;
            }
            catch { }
            return t;
        }

        private static bool TryIndexer(object value, out int length, out Func<int, object> get)
        {
            length = 0;
            get = null;
            if (value is string) return false;

            IList list = value as IList;
            if (list != null)
            {
                length = list.Count;
                get = i => list[i];
                return true;
            }

            Type t = value.GetType();
            string full = t.FullName ?? "";
            if (full.StartsWith("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2Cpp"))
            {
                PropertyInfo lengthProp = t.GetProperty("Length") ?? t.GetProperty("Count");
                PropertyInfo indexer = t.GetProperty("Item");
                if (lengthProp == null || indexer == null) return false;
                length = Convert.ToInt32(lengthProp.GetValue(value, null));
                get = i => indexer.GetValue(value, new object[] { i });
                return true;
            }
            return false;
        }

        private static byte[] AsByteArray(object value)
        {
            byte[] direct = value as byte[];
            if (direct != null) return direct;
            if (value == null) return null;
            Type t = value.GetType();
            if (!(t.FullName ?? "").StartsWith("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2Cpp"))
                return null;
            int length;
            Func<int, object> get;
            if (!TryIndexer(value, out length, out get)) return null;
            PropertyInfo indexer = t.GetProperty("Item");
            if (indexer == null || indexer.PropertyType != typeof(byte)) return null;
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = Convert.ToByte(get(i));
            return bytes;
        }

        /// <summary>
        /// Convert a JsonElement into plain CLR data. The Mono compat layer and
        /// real STJ both hand back JsonElement, which the bridge serializer
        /// would otherwise render as an object with ValueKind/GetRawText props.
        /// </summary>
        private static object ToPlain(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var map = new Dictionary<string, object>();
                    foreach (JsonProperty property in element.EnumerateObject())
                        map[property.Name] = ToPlain(property.Value);
                    return map;
                }
                case JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                        list.Add(ToPlain(item));
                    return list;
                }
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                {
                    long l;
                    if (element.TryGetInt64(out l)) return l;
                    return element.GetDouble();
                }
                default:
                    return null;
            }
        }

        private static string TrimText(string text)
        {
            if (text == null) return null;
            if (text.Length <= BridgeConfig.StringLimit) return text;
            return text.Substring(0, BridgeConfig.StringLimit - 1000) + "\n... [truncated " +
                text.Length + " chars]";
        }

        private static bool LooksLikeText(byte[] bytes)
        {
            int printable = 0;
            int sample = Math.Min(bytes.Length, 2048);
            for (int i = 0; i < sample; i++)
            {
                byte b = bytes[i];
                if (b == 9 || b == 10 || b == 13) { printable++; continue; }
                if (b >= 32 && b < 127) printable++;
                else if (b >= 0x80) printable++; // UTF-8 continuation/lead bytes
            }
            return sample > 0 && printable >= sample * 0.9;
        }

        private static bool LooksLikeJson(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b == ' ' || b == '\t' || b == '\r' || b == '\n') continue;
                return b == '{' || b == '[';
            }
            return false;
        }
    }

    /// <summary>Minimal MessagePack decoder (core types only, bounded).</summary>
    public static class MsgPack
    {
        private const int MaxDepth = 32;
        private const int MaxItems = 10000;

        public static object Decode(byte[] data)
        {
            int pos = 0;
            object value = Read(data, ref pos, 0);
            return value;
        }

        private static object Read(byte[] data, ref int pos, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("msgpack nesting too deep");
            if (pos >= data.Length) throw new FormatException("truncated msgpack");
            byte code = data[pos++];

            if (code <= 0x7f) return (long)code;
            if (code >= 0xe0) return (long)(sbyte)code;
            if ((code & 0xf0) == 0x80) return ReadMap(data, ref pos, depth, code & 0x0f);
            if ((code & 0xf0) == 0x90) return ReadArray(data, ref pos, depth, code & 0x0f);
            if ((code & 0xe0) == 0xa0) return ReadString(data, ref pos, code & 0x1f);

            switch (code)
            {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xc4: { int n = data[pos++]; return ReadBin(data, ref pos, n); }
                case 0xc5: { int n = ReadU16(data, ref pos); return ReadBin(data, ref pos, n); }
                case 0xc6: { int n = ReadI32(data, ref pos); return ReadBin(data, ref pos, n); }
                case 0xca: { float f = BitConverter.ToSingle(Reverse(data, ref pos, 4), 0); return (double)f; }
                case 0xcb: { double d = BitConverter.ToDouble(Reverse(data, ref pos, 8), 0); return d; }
                case 0xcc: return (long)data[pos++];
                case 0xcd: return (long)ReadU16(data, ref pos);
                case 0xce: return (long)(uint)ReadI32(data, ref pos);
                case 0xcf: { ulong u = BitConverter.ToUInt64(Reverse(data, ref pos, 8), 0); return u <= long.MaxValue ? (object)(long)u : u; }
                case 0xd0: return (long)(sbyte)data[pos++];
                case 0xd1: { short s = BitConverter.ToInt16(Reverse(data, ref pos, 2), 0); return (long)s; }
                case 0xd2: return (long)ReadI32(data, ref pos);
                case 0xd3: return BitConverter.ToInt64(Reverse(data, ref pos, 8), 0);
                case 0xd9: { int n = data[pos++]; return ReadString(data, ref pos, n); }
                case 0xda: { int n = ReadU16(data, ref pos); return ReadString(data, ref pos, n); }
                case 0xdb: { int n = ReadI32(data, ref pos); return ReadString(data, ref pos, n); }
                case 0xdc: { int n = ReadU16(data, ref pos); return ReadArray(data, ref pos, depth, n); }
                case 0xdd: { int n = ReadI32(data, ref pos); return ReadArray(data, ref pos, depth, n); }
                case 0xde: { int n = ReadU16(data, ref pos); return ReadMap(data, ref pos, depth, n); }
                case 0xdf: { int n = ReadI32(data, ref pos); return ReadMap(data, ref pos, depth, n); }
                default:
                    throw new FormatException("unsupported msgpack type 0x" + code.ToString("x2"));
            }
        }

        private static Dictionary<string, object> ReadMap(byte[] data, ref int pos, int depth, int count)
        {
            if (count > MaxItems) throw new FormatException("msgpack map too large");
            var map = new Dictionary<string, object>(count);
            for (int i = 0; i < count; i++)
            {
                object key = Read(data, ref pos, depth + 1);
                object value = Read(data, ref pos, depth + 1);
                string text = key == null ? "null" : key.ToString();
                map[text] = value;
            }
            return map;
        }

        private static List<object> ReadArray(byte[] data, ref int pos, int depth, int count)
        {
            if (count > MaxItems) throw new FormatException("msgpack array too large");
            var list = new List<object>(count);
            for (int i = 0; i < count; i++) list.Add(Read(data, ref pos, depth + 1));
            return list;
        }

        private static string ReadString(byte[] data, ref int pos, int length)
        {
            Check(data, pos, length);
            string text = Encoding.UTF8.GetString(data, pos, length);
            pos += length;
            return text;
        }

        private static string ReadBin(byte[] data, ref int pos, int length)
        {
            Check(data, pos, length);
            string hex = ValueSerializer.ToHex(Slice(data, pos, length));
            pos += length;
            return hex;
        }

        private static int ReadU16(byte[] data, ref int pos)
        {
            Check(data, pos, 2);
            int value = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            return value;
        }

        private static int ReadI32(byte[] data, ref int pos)
        {
            Check(data, pos, 4);
            int value = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return value;
        }

        private static byte[] Reverse(byte[] data, ref int pos, int length)
        {
            Check(data, pos, length);
            var bytes = Slice(data, pos, length);
            Array.Reverse(bytes);
            pos += length;
            return bytes;
        }

        private static byte[] Slice(byte[] data, int pos, int length)
        {
            var bytes = new byte[length];
            Array.Copy(data, pos, bytes, 0, length);
            return bytes;
        }

        private static void Check(byte[] data, int pos, int length)
        {
            if (length < 0 || pos + length > data.Length)
                throw new FormatException("truncated msgpack payload");
        }
    }
}
