using System;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// Session-aware object handles: "session:id" (e.g. "Shop#1:-33814").
    /// Bare integer ids stay accepted for backward compatibility.
    /// </summary>
    public static class Handle
    {
        /// <summary>Format a fresh handle for an instance id in the current session.</summary>
        public static string Format(int instanceId)
        {
            return MCPBridge.SessionId + ":" + instanceId;
        }

        /// <summary>
        /// Parse "session:id" or a bare id. Returns false when the text is
        /// neither. Session is null for legacy bare ids.
        /// </summary>
        public static bool TryParse(string text, out string session, out int id)
        {
            session = null;
            id = 0;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                string maybeSession = text.Substring(0, colon);
                int parsed;
                if (int.TryParse(text.Substring(colon + 1), out parsed)
                    && maybeSession.IndexOf('#') >= 0)
                {
                    session = maybeSession;
                    id = parsed;
                    return true;
                }
            }
            int bare;
            if (int.TryParse(text, out bare))
            {
                id = bare;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Validate a session-bound handle against the live session.
        /// Returns null when usable; otherwise a STALE_HANDLE error payload.
        /// Bare ids (no session) always pass here — staleness surfaces
        /// through the normal lookup miss path.
        /// </summary>
        public static StaleHandleError Validate(string text)
        {
            string session;
            int id;
            if (!TryParse(text, out session, out id)) return null;
            if (session == null) return null;
            string current = MCPBridge.SessionId;
            if (string.Equals(session, current, StringComparison.Ordinal)) return null;
            return new StaleHandleError
            {
                HandleSession = session,
                CurrentSession = current,
                InstanceId = id
            };
        }
    }

    public sealed class StaleHandleError
    {
        public string HandleSession;
        public string CurrentSession;
        public int InstanceId;

        public string Message
        {
            get
            {
                return "Stale handle (session " + HandleSession + "): object belongs to an older session, current is "
                    + CurrentSession + ". Re-run find_gameobjects or resolve_path with the Hierarchy path instead of reusing IDs.";
            }
        }

        public object ToData()
        {
            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "code", "STALE_HANDLE" },
                { "handle_session", HandleSession },
                { "current_session", CurrentSession },
                { "suggest", "resolve_path" }
            };
        }
    }
}
