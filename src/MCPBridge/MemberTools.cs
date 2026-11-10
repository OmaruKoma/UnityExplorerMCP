using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace UnityExplorer.MCPBridge
{
    public sealed class MemberValue
    {
        public string Name;
        public string Alias;
        public string Label;
        public string Note;
        public Type Type;
        public object Value;
        public bool IsStatic;
        public bool IsProperty;
    }

    /// <summary>Shared field/property access for snapshots, arrays and value search.</summary>
    public static class MemberTools
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// Walks the base-type chain with DeclaredOnly so private members of base
        /// classes (e.g. TMP_Text.m_text for TextMeshProUGUI) are reachable.
        /// </summary>
        public static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                try
                {
                    FieldInfo field = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                    if (field != null) return field;
                }
                catch { }
            }
            return null;
        }

        public static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                try
                {
                    PropertyInfo property = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                    if (property != null) return property;
                }
                catch { }
            }
            return null;
        }

        public static bool TryResolveMember(Type type, string name, out FieldInfo field,
            out PropertyInfo property, out string error)
        {
            field = null;
            property = null;
            error = null;
            if (type == null || string.IsNullOrEmpty(name))
            {
                error = "Missing type or member name";
                return false;
            }
            string resolved = Labels.ResolveMemberName(type, name);
            if (!string.IsNullOrEmpty(resolved)) name = resolved;

            field = FindField(type, name);
            if (field != null) return true;
            property = FindProperty(type, name);
            if (property != null && property.GetIndexParameters().Length > 0)
            {
                error = "Member is an indexer and cannot be read without arguments: " + name;
                property = null;
                return false;
            }
            if (property == null)
            {
                error = "Field/property not found: " + type.FullName + "." + name;
                return false;
            }
            return true;
        }

        public static bool TryRead(Type type, object instance, string name,
            out object value, out Type valueType, out bool isStatic, out bool isProperty,
            out string error)
        {
            value = null;
            valueType = null;
            isStatic = false;
            isProperty = false;
            FieldInfo field;
            PropertyInfo property;
            if (!TryResolveMember(type, name, out field, out property, out error)) return false;

            try
            {
                if (field != null)
                {
                    isStatic = field.IsStatic;
                    valueType = field.FieldType;
                    value = field.GetValue(field.IsStatic ? null : instance);
                    return true;
                }
                if (!property.CanRead)
                {
                    error = "Property not readable: " + property.Name;
                    return false;
                }
                MethodInfo getter = property.GetGetMethod(true);
                isStatic = getter != null && getter.IsStatic;
                isProperty = true;
                valueType = property.PropertyType;
                value = property.GetValue(isStatic ? null : instance, null);
                return true;
            }
            catch (Exception ex)
            {
                error = "Read failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>Bounded, name-ordered member read used by snapshots and inspect.</summary>
        public static List<MemberValue> Enumerate(Type type, object instance,
            bool includeProperties, bool includeInherited, string nameContains, int limit,
            out int total)
        {
            total = 0;
            var result = new List<MemberValue>();
            if (type == null) return result;
            string filter = nameContains;
            bool staticOnly = instance == null;

            foreach (FieldInfo field in SafeFields(type))
            {
                try
                {
                    if (staticOnly && !field.IsStatic) continue;
                    if (!staticOnly && field.IsStatic) continue;
                    if (!Matches(field.Name, filter)) continue;
                    if (!includeInherited && IsInherited(field.DeclaringType, type)) continue;
                    total++;
                    if (limit > 0 && result.Count >= limit) continue;
                    MemberValue entry = NewEntry(type, field.Name, field.FieldType, field.IsStatic, false);
                    try { entry.Value = field.GetValue(field.IsStatic ? null : instance); }
                    catch (Exception ex) { entry.Value = "<error: " + ex.GetType().Name + ">"; }
                    result.Add(entry);
                }
                catch { }
            }

            if (!includeProperties) return result;
            foreach (PropertyInfo property in SafeProperties(type))
            {
                try
                {
                    bool propertyIsStatic = property.GetGetMethod(true) != null
                        && property.GetGetMethod(true).IsStatic;
                    if (staticOnly && !propertyIsStatic) continue;
                    if (!staticOnly && propertyIsStatic) continue;
                    if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
                    if (!Matches(property.Name, filter)) continue;
                    if (!includeInherited && IsInherited(property.DeclaringType, type)) continue;
                    total++;
                    if (limit > 0 && result.Count >= limit) continue;
                    MethodInfo getter = property.GetGetMethod(true);
                    bool isStatic = getter != null && getter.IsStatic;
                    MemberValue entry = NewEntry(type, property.Name, property.PropertyType, isStatic, true);
                    try { entry.Value = property.GetValue(isStatic ? null : instance, null); }
                    catch (Exception ex) { entry.Value = "<error: " + ex.GetType().Name + ">"; }
                    result.Add(entry);
                }
                catch { }
            }
            return result;
        }

        public static List<MemberValue> Enumerate(Type type, object instance,
            bool includeProperties, string nameContains, int limit, out int total)
        {
            return Enumerate(type, instance, includeProperties, true, nameContains, limit, out total);
        }

        public static object ConvertForWrite(object raw, Type target)
        {
            if (raw == null) return null;
            Type t = target.IsByRef ? target.GetElementType() : target;
            if (t == typeof(string)) return raw.ToString();
            if (t == typeof(bool)) return Convert.ToBoolean(raw);
            if (t == typeof(int)) return Convert.ToInt32(raw);
            if (t == typeof(long)) return Convert.ToInt64(raw);
            if (t == typeof(float)) return Convert.ToSingle(raw);
            if (t == typeof(double)) return Convert.ToDouble(raw);
            if (t == typeof(byte)) return Convert.ToByte(raw);
            if (t.IsEnum) return Enum.Parse(t, raw.ToString(), true);
            return Convert.ChangeType(raw, t);
        }

        public static bool IsScalar(Type t)
        {
            if (t == null) return false;
            return t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t.IsEnum;
        }

        public static bool IsPointerLike(Type t)
        {
            if (t == null) return false;
            string n = t.FullName ?? t.Name ?? "";
            return t == typeof(IntPtr) || t == typeof(UIntPtr)
                || n.EndsWith("CachedPtr") || n == "System.IntPtr";
        }

        public static string Canonical(object value)
        {
            try
            {
                return JsonSerializer.Serialize(ValueSerializer.Serialize(value, "hex", false));
            }
            catch (Exception ex)
            {
                // One exotic member must not fail the whole snapshot.
                return "\"<unserializable:" + ex.GetType().Name + ">\"";
            }
        }

        private static MemberValue NewEntry(Type owner, string name, Type type, bool isStatic, bool isProperty)
        {
            var entry = new MemberValue
            {
                Name = name,
                Type = type,
                IsStatic = isStatic,
                IsProperty = isProperty,
                Alias = Labels.AliasFor(owner, name)
            };
            Labels.Decorate(owner, name, entry);
            return entry;
        }

        private static bool Matches(string name, string contains)
        {
            return string.IsNullOrEmpty(contains)
                || (name != null && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsInherited(Type declaring, Type owner)
        {
            return declaring != null && owner != null && declaring != owner;
        }

        private static IEnumerable<FieldInfo> SafeFields(Type type)
        {
            var seen = new HashSet<string>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(Flags | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (FieldInfo field in fields)
                {
                    if (seen.Add(field.Name)) yield return field;
                }
            }
        }

        private static IEnumerable<PropertyInfo> SafeProperties(Type type)
        {
            var seen = new HashSet<string>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo[] properties;
                try { properties = t.GetProperties(Flags | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (PropertyInfo property in properties)
                {
                    if (seen.Add(property.Name)) yield return property;
                }
            }
        }
    }
}
