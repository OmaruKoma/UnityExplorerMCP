using System;
using System.Collections.Generic;
using System.Text.Json;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Shared limit/cursor/name_contains pagination. List tools return the
    /// legacy bare array when no paging params are given, otherwise an
    /// envelope { items, total, next_cursor }.
    /// </summary>
    public static class Paging
    {
        public sealed class Params
        {
            public int Limit;          // 0 = unlimited (legacy)
            public int Cursor;         // start offset
            public string NameContains;
            public bool Requested;     // any paging param explicitly present
        }

        public static Params Read(System.Text.Json.JsonElement el, int defaultLimit)
        {
            var p = new Params { Limit = 0, Cursor = 0, NameContains = null, Requested = false };
            JsonElement tmp;
            if (el.TryGetProperty("limit", out tmp) || el.TryGetProperty("Limit", out tmp))
            {
                try { p.Limit = Math.Max(0, tmp.GetInt32()); p.Requested = true; } catch { }
            }
            else if (defaultLimit > 0)
            {
                p.Limit = defaultLimit;
            }
            if (el.TryGetProperty("cursor", out tmp) || el.TryGetProperty("Cursor", out tmp))
            {
                try { p.Cursor = Math.Max(0, tmp.GetInt32()); p.Requested = true; } catch { }
            }
            if (el.TryGetProperty("name_contains", out tmp) || el.TryGetProperty("NameContains", out tmp))
            {
                try { p.NameContains = tmp.GetString(); p.Requested = !string.IsNullOrEmpty(p.NameContains); }
                catch { }
            }
            return p;
        }

        public sealed class Page<T>
        {
            public List<T> Items;
            public int Total;
            public int? NextCursor;
        }

        public static Page<T> Apply<T>(List<T> all, Params p, Predicate<T> match)
        {
            List<T> filtered = all;
            if (match != null)
            {
                filtered = new List<T>(all.Count);
                foreach (var item in all) { try { if (match(item)) filtered.Add(item); } catch { } }
            }
            int total = filtered.Count;
            int start = Math.Min(p.Cursor, total);
            List<T> items = filtered;
            int? next = null;
            if (p.Limit > 0 && start + p.Limit < total)
            {
                items = filtered.GetRange(start, p.Limit);
                next = start + p.Limit;
            }
            else if (start > 0)
            {
                items = filtered.GetRange(start, total - start);
            }
            return new Page<T> { Items = items, Total = total, NextCursor = next };
        }

        public static object Envelope<T>(Page<T> page)
        {
            return new Dictionary<string, object>
            {
                { "items", page.Items },
                { "total", page.Total },
                { "next_cursor", page.NextCursor.HasValue ? (object)page.NextCursor.Value : null }
            };
        }
    }
}
