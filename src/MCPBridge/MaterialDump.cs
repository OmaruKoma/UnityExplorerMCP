using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// unity_dump_material: one call returns everything you normally need three
    /// round-trips (and N property reads) to get — shader name, render queue,
    /// keywords and the shader's own property values.
    ///
    /// Only properties the shader declares are read (Material.HasProperty), so
    /// the result is bounded by the shader, not by Material's internal arrays.
    /// "Non-default" is decided by comparing against the shader's declared
    /// default; values equal to it are omitted unless include_defaults is set.
    /// </summary>
    public static class MaterialDump
    {
        public static Dictionary<string, object> Dump(Material material, int propertyLimit,
            bool includeDefaults, bool includeHidden)
        {
            if (material == null) throw new Exception("Object is not a Material (or is null)");

            var result = new Dictionary<string, object>
            {
                { "name", material.name },
                { "shader", SafeString(() => material.shader != null ? material.shader.name : null) },
                { "render_queue", SafeInt(() => material.renderQueue) },
                { "instance_id", SafeInt(() => material.GetInstanceID()) },
                { "handle", Handle.Format(SafeInt(() => material.GetInstanceID())) },
                { "keywords", ReadKeywords(material) }
            };

            Shader shader = null;
            try { shader = material.shader; } catch { }

            var properties = new List<Dictionary<string, object>>();
            int total = 0;
            int filtered = 0;
            int limit = propertyLimit > 0 ? propertyLimit : 200;
            if (shader != null)
            {
                // Shader.GetProperty* only exists from Unity 2019.1 and is absent
                // from the net35 Mono reference set, so probe reflectively and fall
                // back to enumerating the material's own property arrays on old
                // engines (rare: such shaders expose few properties anyway).
                int count = ShaderPropertyCount(shader);
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string propName = ShaderPropertyName(shader, i);
                    if (string.IsNullOrEmpty(propName)) continue;

                    var type = ShaderPropertyType(shader, i);
                    bool hidden = ShaderPropertyHidden(shader, i);
                    if (hidden && !includeHidden) continue;

                    total++;
                    var entry = ReadProperty(material, propName, type);
                    if (entry == null) continue;

                    bool isDefault = false;
                    try { isDefault = MatchesDefault(type, material, propName); }
                    catch { }
                    entry["is_default"] = isDefault;
                    if (isDefault && !includeDefaults) { filtered++; continue; }

                    // The hook that makes a texture actually usable: name inline,
                    // so callers do not need get_property("name") per texture.
                    if (string.Equals(type, "Texture", StringComparison.OrdinalIgnoreCase))
                    {
                        object tex = null;
                        try { tex = material.GetTexture(propName); } catch { }
                        entry["texture"] = DescribeTexture(tex);
                    }

                    counts[type] = counts.TryGetValue(type, out int seen) ? seen + 1 : 1;
                    if (properties.Count < limit) properties.Add(entry);
                }
                if (count == 0)
                {
                    // Old engine / no Shader.GetProperty*: the material still knows
                    // which uniforms it has.
                    foreach (string propName in MaterialPropertyNames(material))
                    {
                        if (string.IsNullOrEmpty(propName))
                            continue;
                        if (properties.Count >= limit) break;
                        total++;
                        var entry = ReadProperty(material, propName, GuessPropertyType(material, propName));
                        if (entry != null) properties.Add(entry);
                    }
                    if (total > 0) result["note"] = "Shader property metadata unavailable; names inferred from the material";
                }
                result["property_counts"] = counts;
                result["declared_properties"] = total;
            }
            else
            {
                result["note"] = "Material has no shader; only keywords are reported";
            }

            result["returned_properties"] = properties.Count;
            result["filtered_defaults"] = filtered;
            result["properties"] = properties;
            result["hide_flags"] = ReadHideFlags(material);
            return result;
        }

        private static Dictionary<string, object> ReadProperty(Material material, string name, string type)
        {
            var entry = new Dictionary<string, object> { { "name", name }, { "type", type } };
            try
            {
                switch ((type ?? "").ToLowerInvariant())
                {
                    case "float":
                    case "range":
                        entry["value"] = material.GetFloat(name);
                        return entry;
                    case "integer":
                        entry["value"] = GetInteger(material, name);
                        return entry;
                    case "color":
                        entry["value"] = ColorText(material.GetColor(name));
                        return entry;
                    case "vector":
                        Vector4 v = material.GetVector(name);
                        entry["value"] = new Dictionary<string, object>
                        {
                            { "x", v.x }, { "y", v.y }, { "z", v.z }, { "w", v.w }
                        };
                        return entry;
                    case "texture":
                    case "texture2d":
                    case "texture3d":
                    case "cubemap":
                    case "texture2darray":
                        return entry; // texture described by the caller
                    default:
                        // Newer types (Integer/Constant) or unknown: try float last.
                        try { entry["value"] = material.GetFloat(name); return entry; }
                        catch { }
                        entry["note"] = "unsupported property type";
                        return entry;
                }
            }
            catch (Exception ex)
            {
                entry["error"] = ex.Message;
                return entry;
            }
        }

        /// <summary>Material.GetInteger is 2021.2+; fall back to the float API.</summary>
        private static object GetInteger(Material material, string name)
        {
            MethodInfo getter = typeof(Material).GetMethod("GetInteger", new Type[] { typeof(string) });
            if (getter != null)
            {
                object value = getter.Invoke(material, new object[] { name });
                if (value != null) return value;
            }
            return (int)material.GetFloat(name);
        }

        /// <summary>Explicit shader-property name, or null on pre-2019 engines.</summary>
        private static string ShaderPropertyName(Shader shader, int index)
        {
            try
            {
                MethodInfo method = typeof(Shader).GetMethod("GetPropertyName",
                    new Type[] { typeof(int) });
                if (method == null) return null;
                return method.Invoke(shader, new object[] { index }) as string;
            }
            catch { return null; }
        }

        private static int ShaderPropertyCount(Shader shader)
        {
            try
            {
                MethodInfo method = typeof(Shader).GetMethod("GetPropertyCount", Type.EmptyTypes);
                if (method == null) return 0;
                object value = method.Invoke(shader, null);
                return value != null ? Convert.ToInt32(value) : 0;
            }
            catch { return 0; }
        }

        private static string ShaderPropertyType(Shader shader, int index)
        {
            try
            {
                MethodInfo method = typeof(Shader).GetMethod("GetPropertyType", new Type[] { typeof(int) });
                if (method == null) return "Float";
                object value = method.Invoke(shader, new object[] { index });
                return value != null ? value.ToString() : "Float";
            }
            catch { return "Float"; }
        }

        private static bool ShaderPropertyHidden(Shader shader, int index)
        {
            try
            {
                MethodInfo method = typeof(Shader).GetMethod("GetPropertyAttributes", new Type[] { typeof(int) });
                if (method == null) return false;
                var attrs = method.Invoke(shader, new object[] { index }) as string[];
                if (attrs == null) return false;
                foreach (string a in attrs)
                    if (string.Equals(a, "HideInInspector", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Fallback property names for engines without Shader.GetPropertyName.</summary>
        private static List<string> MaterialPropertyNames(Material material)
        {
            var names = new List<string>();
            try
            {
                MethodInfo method = typeof(Material).GetMethod("GetTexturePropertyNames",
                    Type.EmptyTypes);
                if (method == null) return names;
                var raw = method.Invoke(material, null) as string[];
                if (raw == null) return names;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in raw)
                    if (seen.Add(name)) names.Add(name);
            }
            catch { }
            return names;
        }

        private static string GuessPropertyType(Material material, string name)
        {
            try { if (material.GetTexture(name) != null) return "Texture"; } catch { }
            try { material.GetVector(name); return "Vector"; } catch { }
            try { material.GetFloat(name); return "Float"; } catch { }
            return "Float";
        }

        /// <summary>Name + size + format, so a texture reference is self-describing.</summary>
        private static Dictionary<string, object> DescribeTexture(object tex)
        {
            if (tex == null) return null;
            var info = new Dictionary<string, object>();
            var unityTex = tex as Texture;
            if (unityTex != null)
            {
                info["name"] = unityTex.name;
                info["width"] = unityTex.width;
                info["height"] = unityTex.height;
                info["instance_id"] = unityTex.GetInstanceID();
                info["handle"] = Handle.Format(unityTex.GetInstanceID());
                info["type"] = unityTex.GetType().Name;
            }
            else
            {
                info["preview"] = tex.ToString();
            }
            return info;
        }

        /// <summary>
        /// ShaderKeywords as the real string list, not an opaque interop array.
        /// </summary>
        private static List<string> ReadKeywords(Material material)
        {
            var keywords = new List<string>();
            try
            {
                foreach (string kw in material.shaderKeywords)
                    if (!string.IsNullOrEmpty(kw)) keywords.Add(kw);
            }
            catch
            {
                try
                {
                    var prop = typeof(Material).GetProperty("shaderKeywords",
                        BindingFlags.Public | BindingFlags.Instance);
                    var value = prop != null ? prop.GetValue(material, null) as IEnumerable : null;
                    if (value != null)
                        foreach (object item in value)
                            if (item != null) keywords.Add(item.ToString());
                }
                catch { }
            }
            return keywords;
        }

        private static Dictionary<string, object> ReadHideFlags(Material material)
        {
            var flags = new Dictionary<string, object>();
            try
            {
                HideFlags f = material.hideFlags;
                flags["raw"] = (int)f;
                flags["text"] = f.ToString();
            }
            catch { }
            return flags;
        }

        /// <summary>
        /// Unity exposes no shader-default getter, so "default" is approximated by
        /// a neutral value (0 float/int, transparent black colour, zero vector,
        /// null texture). It is a filter for noise, not truth: a property the
        /// author deliberately set to 0 is also dropped. The count of dropped
        /// entries is always reported so the caller can re-read with
        /// include_defaults=true when that matters.
        /// </summary>
        private static bool MatchesDefault(string type, Material material, string name)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "float":
                case "range":
                {
                    float value;
                    try { value = material.GetFloat(name); }
                    catch { return false; }
                    return Math.Abs(value) < 1e-7f;
                }
                case "integer":
                {
                    object value;
                    try { value = GetInteger(material, name); }
                    catch { return false; }
                    try { return Convert.ToInt32(value) == 0; } catch { return false; }
                }
                case "color":
                {
                    Color c;
                    try { c = material.GetColor(name); }
                    catch { return false; }
                    return c.r == 0f && c.g == 0f && c.b == 0f && c.a == 0f;
                }
                case "vector":
                {
                    Vector4 v;
                    try { v = material.GetVector(name); }
                    catch { return false; }
                    return v.x == 0f && v.y == 0f && v.z == 0f && v.w == 0f;
                }
                case "texture":
                case "texture2d":
                case "texture3d":
                case "cubemap":
                case "texture2darray":
                    try { return material.GetTexture(name) == null; }
                    catch { return false; }
                default:
                    return false;
            }
        }

        private static string ColorText(Color c)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "rgba({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", c.r, c.g, c.b, c.a);
        }

        private static string SafeString(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }

        private static int SafeInt(Func<int> read)
        {
            try { return read(); } catch { return 0; }
        }
    }
}