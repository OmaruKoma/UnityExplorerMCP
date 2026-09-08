using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityExplorer.MCPBridge
{
    /// <summary>P1: assembly/type introspection, static invocation, path resolution.</summary>
    public static class AssemblyInspector
    {
        public static List<string> ListAssemblies(string filter)
        {
            var names = new List<string>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string n = asm.GetName().Name;
                    if (string.IsNullOrEmpty(filter) || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add(n);
                }
                catch { }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public static Type ResolveType(string assemblyName, string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName)) return null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            if (!string.IsNullOrEmpty(assemblyName))
            {
                foreach (Assembly asm in assemblies)
                {
                    try
                    {
                        if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        Type t = asm.GetType(typeFullName);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            foreach (Assembly asm in assemblies)
            {
                try
                {
                    Type t = asm.GetType(typeFullName);
                    if (t != null) return t;
                }
                catch { }
            }
            // Slow path: scan exported types (handles nested/forwarded edge cases).
            foreach (Assembly asm in assemblies)
            {
                try
                {
                    foreach (Type t in asm.GetTypes())
                    {
                        if (t.FullName == typeFullName || t.Name == typeFullName) return t;
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (Type t in ex.Types)
                    {
                        if (t != null && (t.FullName == typeFullName || t.Name == typeFullName)) return t;
                    }
                }
                catch { }
            }
            return null;
        }

        public static Dictionary<string, object> InspectType(string assemblyName, string typeFullName)
        {
            Type type = ResolveType(assemblyName, typeFullName);
            if (type == null)
                throw new Exception("Type not found: " + typeFullName +
                    (string.IsNullOrEmpty(assemblyName) ? "" : " in assembly " + assemblyName));

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var fields = new List<Dictionary<string, object>>();
            foreach (FieldInfo f in type.GetFields(flags))
            {
                object val;
                try { val = f.GetValue(null); if (!f.IsStatic) val = "<instance member; use unity_get_field with instance_id>"; }
                catch (Exception ex) { val = "<error: " + ex.GetType().Name + ">"; }
                fields.Add(new Dictionary<string, object>
                {
                    { "name", f.Name },
                    { "type", f.FieldType.FullName },
                    { "isStatic", f.IsStatic },
                    { "canWrite", !(f.IsLiteral && !f.IsInitOnly) },
                    { "value", val != null ? val.ToString() : "null" }
                });
            }
            var props = new List<Dictionary<string, object>>();
            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                MethodInfo accessor = p.GetGetMethod(true) ?? p.GetSetMethod(true);
                props.Add(new Dictionary<string, object>
                {
                    { "name", p.Name },
                    { "type", p.PropertyType.FullName },
                    { "isStatic", accessor != null ? accessor.IsStatic : false },
                    { "canRead", p.CanRead },
                    { "canWrite", p.CanWrite }
                });
            }
            var methods = new List<Dictionary<string, object>>();
            foreach (MethodInfo m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                var ps = new List<Dictionary<string, object>>();
                foreach (ParameterInfo pi in m.GetParameters())
                {
                    ps.Add(new Dictionary<string, object>
                    {
                        { "name", pi.Name },
                        { "type", (pi.ParameterType.IsByRef ? pi.ParameterType.GetElementType() : pi.ParameterType).FullName },
                        { "isOut", pi.IsOut || pi.ParameterType.IsByRef }
                    });
                }
                methods.Add(new Dictionary<string, object>
                {
                    { "name", m.Name },
                    { "returnType", m.ReturnType.FullName },
                    { "isStatic", m.IsStatic },
                    { "parameters", ps }
                });
            }
            return new Dictionary<string, object>
            {
                { "typeName", type.FullName },
                { "assembly", type.Assembly.GetName().Name },
                { "isEnum", type.IsEnum },
                { "fields", fields },
                { "properties", props },
                { "methods", methods }
            };
        }

        public class StaticCall
        {
            public MethodInfo Method;
            public object[] ConvertedArgs;
            public List<Dictionary<string, object>> OutArgs;
        }

        /// <summary>
        /// Resolve a static method overload. Supports two arg styles:
        /// (a) full positional incl. null placeholders for out/ref params;
        /// (b) compact: only the non-out args in order (out/ref allocated + returned).
        /// </summary>
        public static StaticCall ResolveStaticCall(Type type, string methodName, List<JsonElement> rawArgs)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == methodName).ToList();
            if (candidates.Count == 0)
                throw new Exception("Static method not found: " + type.FullName + "." + methodName);

            List<string> tried = new List<string>();
            // Prefer exact positional match first, then compact match.
            foreach (bool compact in new[] { false, true })
            {
                foreach (MethodInfo m in candidates)
                {
                    ParameterInfo[] ps = m.GetParameters();
                    object[] converted;
                    if (TryConvert(ps, rawArgs, compact, out converted))
                    {
                        var outArgs = new List<Dictionary<string, object>>();
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (ps[i].ParameterType.IsByRef)
                            {
                                Type el = ps[i].ParameterType.GetElementType();
                                outArgs.Add(new Dictionary<string, object>
                                {
                                    { "index", i },
                                    { "name", ps[i].Name },
                                    { "type", el.FullName }
                                });
                            }
                        }
                        return new StaticCall { Method = m, ConvertedArgs = converted, OutArgs = outArgs };
                    }
                    tried.Add(Signature(m));
                }
            }
            throw new Exception("No overload of " + type.FullName + "." + methodName +
                " matches " + rawArgs.Count + " arg(s). Tried: " + string.Join(" | ", tried));
        }

        private static bool TryConvert(ParameterInfo[] ps, List<JsonElement> rawArgs, bool compact, out object[] converted)
        {
            converted = null;
            var byRefIdx = new List<int>();
            var valueIdx = new List<int>();
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType.IsByRef) byRefIdx.Add(i);
                else valueIdx.Add(i);
            }

            // Pure-out params (out X, no in) may be omitted in compact mode.
            var pureOut = new HashSet<int>();
            foreach (int i in byRefIdx)
            {
                if (ps[i].IsOut && !ps[i].IsIn) pureOut.Add(i);
            }

            if (!compact)
            {
                if (rawArgs.Count != ps.Length) return false;
                converted = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    try { converted[i] = ValueSerializer.ConvertJsonElement(rawArgs[i], ps[i].ParameterType); }
                    catch { return false; }
                }
                return true;
            }

            // Compact: rawArgs map to non-pure-out params in order.
            var targets = new List<int>();
            for (int i = 0; i < ps.Length; i++) if (!pureOut.Contains(i)) targets.Add(i);
            if (rawArgs.Count != targets.Count) return false;
            converted = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type el = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                converted[i] = el.IsValueType ? Activator.CreateInstance(el) : null;
            }
            for (int k = 0; k < targets.Count; k++)
            {
                try { converted[targets[k]] = ValueSerializer.ConvertJsonElement(rawArgs[k], ps[targets[k]].ParameterType); }
                catch { return false; }
            }
            return true;
        }

        /// <summary>Overload-aware resolution for instance or static managed methods.</summary>
        public static StaticCall ResolveInstanceCall(Type type, string methodName, List<JsonElement> rawArgs)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == methodName && !m.IsSpecialName).ToList();
            if (candidates.Count == 0)
                throw new Exception("Method not found: " + type.FullName + "." + methodName);

            List<string> tried = new List<string>();
            foreach (bool compact in new[] { false, true })
            {
                foreach (MethodInfo m in candidates)
                {
                    ParameterInfo[] ps = m.GetParameters();
                    object[] converted;
                    if (TryConvert(ps, rawArgs, compact, out converted))
                        return new StaticCall { Method = m, ConvertedArgs = converted, OutArgs = new List<Dictionary<string, object>>() };
                    tried.Add(Signature(m));
                }
            }
            throw new Exception("No overload of " + type.FullName + "." + methodName +
                " matches " + rawArgs.Count + " arg(s). Tried: " + string.Join(" | ", tried));
        }

        /// <summary>Read back out/ref params after Invoke; values serialized per P3.</summary>
        public static List<object> CollectOutArgs(MethodInfo m, object[] converted, string encoding)
        {
            var outArgs = new List<object>();
            ParameterInfo[] ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType.IsByRef)
                {
                    Type el = ps[i].ParameterType.GetElementType();
                    outArgs.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "name", ps[i].Name },
                        { "type", el.FullName },
                        { "value", ValueSerializer.Serialize(converted[i], encoding) }
                    });
                }
            }
            return outArgs;
        }

        private static string Signature(MethodInfo m)
        {
            var ps = new List<string>();
            foreach (var p in m.GetParameters())
                ps.Add((p.ParameterType.IsByRef ? "out/ref " : "") + p.ParameterType.Name + " " + p.Name);
            return m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps) + ")";
        }

        /// <summary>Re-resolve a Hierarchy path like "Root/Child" to a live GameObject.</summary>
        public static GameObject ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string clean = path.Trim().Trim('/');
            int slash = clean.IndexOf('/');
            string rootName = slash < 0 ? clean : clean.Substring(0, slash);
            string rest = slash < 0 ? null : clean.Substring(slash + 1);

            foreach (GameObject root in AllRoots())
            {
                if (root.name != rootName) continue;
                if (rest == null) return root;
                Transform found = root.transform.Find(rest);
                if (found != null) return found.gameObject;
            }
            return null;
        }

        public static string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.gameObject.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static List<GameObject> AllRoots()
        {
            var roots = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                roots.AddRange(scene.GetRootGameObjects());
            }
            // DontDestroyOnLoad objects live outside normal scenes.
            try
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || go.transform.parent != null) continue;
                    try
                    {
                        if (!go.scene.isLoaded && go.scene.name == "DontDestroyOnLoad")
                            roots.Add(go);
                    }
                    catch { }
                }
            }
            catch { }
            return roots;
        }
    }
}
