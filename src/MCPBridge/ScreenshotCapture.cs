using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// PNG capture through reflection so the same source compiles against
    /// Unity 5.x / 2019+ and IL2CPP interop, where module availability differs.
    /// </summary>
    public static class ScreenshotCapture
    {
        public static Dictionary<string, object> Capture(int maxBytes, string outputPath)
        {
            if (maxBytes <= 0) maxBytes = BridgeConfig.MaxScreenshotBytes;
            Texture2D texture = null;
            Texture2D cameraTexture = null;
            try
            {
                texture = CaptureTexture();

                // ScreenCapture only works at end-of-frame; called from a request
                // handler it returns a cleared buffer. When that happens, render the
                // live camera to a RenderTexture instead — synchronous, no frame
                // callback needed, and it includes whatever frame_camera aimed at.
                string flat = texture == null ? "no texture" : DetectFlat(texture);
                string source = "screen_capture";
                if (texture == null || flat != null)
                {
                    cameraTexture = CaptureMainCamera();
                    if (cameraTexture != null)
                    {
                        string cameraFlat = DetectFlat(cameraTexture);
                        if (cameraFlat == null)
                        {
                            if (texture != null) UnityEngine.Object.Destroy(texture);
                            texture = cameraTexture;
                            cameraTexture = null;
                            flat = null;
                            source = "camera_render";
                        }
                        else
                        {
                            if (texture == null)
                            {
                                texture = cameraTexture;
                                cameraTexture = null;
                            }
                            flat = cameraFlat;
                            source = "camera_render";
                        }
                    }
                }
                if (texture == null) throw new Exception("screen capture returned no texture");

                byte[] png = EncodePng(texture);
                int scale = 0;
                while (png.Length > maxBytes && scale < 4)
                {
                    scale++;
                    Texture2D smaller = Downscale(texture, scale);
                    if (smaller == null) break;
                    UnityEngine.Object.Destroy(texture);
                    texture = smaller;
                    png = EncodePng(texture);
                }

                var result = new Dictionary<string, object>
                {
                    { "mime_type", "image/png" },
                    { "width", texture.width },
                    { "height", texture.height },
                    { "bytes", png.Length },
                    { "scaled", scale },
                    { "source", source },
                    { "base64", Convert.ToBase64String(png) }
                };
                if (flat != null)
                {
                    result["flat_color"] = flat;
                    result["warning"] = "Every sampled pixel is identical. ScreenCapture only "
                        + "produces a frame inside the end-of-frame callback, and the camera "
                        + "render matched it. The scene may genuinely be blank: check that the "
                        + "camera sees the target (unity_frame_camera) or use unity_render_object.";
                }
                if (!string.IsNullOrEmpty(outputPath))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(outputPath, png);
                        result["path"] = outputPath;
                    }
                    catch (Exception ex)
                    {
                        result["path_error"] = ex.Message;
                    }
                }
                return result;
            }
            finally
            {
                if (texture != null)
                {
                    try { UnityEngine.Object.Destroy(texture); } catch { }
                }
                if (cameraTexture != null)
                {
                    try { UnityEngine.Object.Destroy(cameraTexture); } catch { }
                }
            }
        }

        /// <summary>
        /// Renders the active camera into a RenderTexture and reads it back. Works
        /// on the request thread, unlike ScreenCapture. Screen-space overlay UI is
        /// not part of the camera's render, so the result is the 3D view only.
        /// </summary>
        private static Texture2D CaptureMainCamera()
        {
            Camera camera = null;
            try { camera = Camera.main; } catch { }
            if (camera == null)
            {
                try
                {
                    foreach (Camera candidate in Camera.allCameras)
                    {
                        if (candidate != null && candidate.enabled && candidate.targetTexture == null)
                        {
                            camera = candidate;
                            break;
                        }
                    }
                }
                catch { }
            }
            if (camera == null) return null;

            int width = Math.Max(1, Screen.width);
            int height = Math.Max(1, Screen.height);
            RenderTexture render = null;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = null;
            try
            {
                previousTarget = camera.targetTexture;
                render = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = render;
                camera.Render();
                RenderTexture.active = render;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previousActive;
                try { camera.targetTexture = previousTarget; } catch { }
                if (render != null) RenderTexture.ReleaseTemporary(render);
            }
        }

        /// <summary>
        /// Points a live game camera at a target: position derived from the target's
        /// bounds, framed so the whole object fits. Unlike RenderObject this drives a
        /// real camera, so the player view and screen capture change too. The original
        /// transform is returned so the caller can undo it (restore=true does that
        /// immediately).
        /// </summary>
        public static Dictionary<string, object> FrameCamera(Camera camera,
            UnityEngine.Object target, float distanceScale, float offsetX, float offsetY,
            bool lookAt, bool restore, bool reverse)
        {
            if (camera == null) throw new Exception("Camera is null");
            if (target == null) throw new Exception("Target is null");
            if (distanceScale <= 0f) distanceScale = 1.15f;

            Bounds bounds = ComputeBounds(target);
            Transform camTransform = camera.transform;
            Vector3 oldPosition = camTransform.position;
            Quaternion oldRotation = camTransform.rotation;
            float oldFov = camera.fieldOfView;
            float oldNear = camera.nearClipPlane;
            float oldFar = camera.farClipPlane;

            float radius = Math.Max(bounds.extents.magnitude, 0.01f);
            float distance = radius / Mathf.Sin(Mathf.Max(camera.fieldOfView, 1f) * 0.5f * Mathf.Deg2Rad)
                * distanceScale;

            // Front view: target's own forward axis when it has one, world -Z otherwise.
            Vector3 forward = Vector3.back;
            try
            {
                Transform t = target as Transform
                    ?? (target as Component)?.transform;
                if (t != null) forward = -t.forward;
            }
            catch { }
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.back;
            forward.Normalize();
            // Which side of the target is its front is a modelling convention; reverse
            // flips the camera to the opposite side without moving the target.
            if (reverse) forward = -forward;
            Vector3 up = Vector3.up;

            Vector3 position = bounds.center + forward * distance
                + camTransform.right * (offsetX * radius)
                + camTransform.up * (offsetY * radius);

            camTransform.position = position;
            if (lookAt) camTransform.LookAt(bounds.center, up);

            camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 2f);
            camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 0.1f, distance + radius * 4f);

            // CinemachineBrain rewrites the camera transform in LateUpdate, so the
            // framing above is gone one frame later unless the driver is paused.
            List<Behaviour> paused = restore ? null : PauseCameraDrivers(camera);
            List<string> reenabled = null;
            if (restore) reenabled = ResumeCameraDrivers();

            var result = new Dictionary<string, object>
            {
                { "camera", camera.name },
                { "camera_handle", Handle.Format(camera.GetInstanceID()) },
                { "target_bounds_center", Vec(bounds.center) },
                { "target_bounds_size", Vec(bounds.size) },
                { "camera_position", Vec(camTransform.position) },
                { "camera_rotation", Vec(camTransform.eulerAngles) },
                { "distance", distance },
                { "near_clip", camera.nearClipPlane },
                { "far_clip", camera.farClipPlane },
                { "restored", false },
                { "previous", new Dictionary<string, object>
                    {
                        { "position", Vec(oldPosition) },
                        { "rotation", Vec(oldRotation.eulerAngles) },
                        { "field_of_view", oldFov },
                        { "near_clip", oldNear },
                        { "far_clip", oldFar }
                    } }
            };
            if (paused != null)
                result["camera_drivers_paused"] = DriverNames(paused);
            if (reenabled != null)
                result["camera_drivers_resumed"] = reenabled;

            if (restore)
            {
                camTransform.position = oldPosition;
                camTransform.rotation = oldRotation;
                camera.fieldOfView = oldFov;
                camera.nearClipPlane = oldNear;
                camera.farClipPlane = oldFar;
                result["restored"] = true;
            }
            else
            {
                result["hint"] = "Camera left in the new position with its transform drivers "
                    + "paused. Call unity_frame_camera again with restore=true to put it back "
                    + "(this also re-enables the drivers), or reuse the 'previous' block.";
            }
            return result;
        }

        private static readonly List<Behaviour> _pausedDrivers = new List<Behaviour>();

        /// <summary>
        /// Disables Cinemachine-style behaviours on the camera so a written
        /// transform survives past LateUpdate.
        /// </summary>
        private static List<Behaviour> PauseCameraDrivers(Camera camera)
        {
            var paused = new List<Behaviour>();
            try
            {
                foreach (var behaviour in camera.gameObject.GetComponents<Behaviour>())
                {
                    if (behaviour == null || !behaviour.enabled) continue;
                    string typeName = behaviour.GetType().Name;
                    if (typeName.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    behaviour.enabled = false;
                    paused.Add(behaviour);
                    if (!_pausedDrivers.Contains(behaviour)) _pausedDrivers.Add(behaviour);
                }
            }
            catch { }
            return paused;
        }

        private static List<string> ResumeCameraDrivers()
        {
            var resumed = new List<string>();
            for (int i = _pausedDrivers.Count - 1; i >= 0; i--)
            {
                Behaviour behaviour = _pausedDrivers[i];
                try
                {
                    if (behaviour == null) continue;
                    behaviour.enabled = true;
                    resumed.Add(behaviour.GetType().Name);
                }
                catch { }
                _pausedDrivers.RemoveAt(i);
            }
            return resumed;
        }

        private static List<string> DriverNames(List<Behaviour> behaviours)
        {
            var names = new List<string>();
            foreach (var behaviour in behaviours)
            {
                try { names.Add(behaviour.GetType().Name); }
                catch { names.Add("?"); }
            }
            return names;
        }

        private static Dictionary<string, object> Vec(Vector3 v)
        {
            return new Dictionary<string, object>
            {
                { "x", v.x }, { "y", v.y }, { "z", v.z }
            };
        }

        /// <summary>
        /// Returns the colour name when every sampled pixel matches, else null.
        /// Sampled on a grid so a 2560x1600 read stays cheap.
        /// </summary>
        private static string DetectFlat(Texture2D texture)
        {
            try
            {
                if (texture == null || texture.width < 2 || texture.height < 2) return null;
                Color32[] pixels = texture.GetPixels32();
                if (pixels == null || pixels.Length == 0) return null;
                Color32 first = pixels[0];
                int stepX = Math.Max(1, texture.width / 32);
                int stepY = Math.Max(1, texture.height / 32);
                for (int y = 0; y < texture.height; y += stepY)
                {
                    int row = y * texture.width;
                    for (int x = 0; x < texture.width; x += stepX)
                    {
                        Color32 c = pixels[row + x];
                        if (c.r != first.r || c.g != first.g || c.b != first.b || c.a != first.a)
                            return null;
                    }
                }
                return "rgba(" + first.r + ", " + first.g + ", " + first.b + ", " + first.a + ")";
            }
            catch { return null; }
        }

        private static Texture2D CaptureTexture()
        {
            Type screenCapture = FindType("UnityEngine.ScreenCapture");
            if (screenCapture != null)
            {
                try
                {
                    MethodInfo method = screenCapture.GetMethod("CaptureScreenshotAsTexture",
                        BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (method != null)
                    {
                        Texture2D captured = method.Invoke(null, null) as Texture2D;
                        if (captured != null) return captured;
                    }
                }
                catch { }
            }
            // Fallback: read the current render target (works at end of frame).
            var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// Renders a target object offscreen with a throwaway camera. Screen capture
        /// returns whatever the game composited last (UI overlays, pause menus, a
        /// blank frame while paused), which is useless for "what does this look
        /// like". Here we frame the object's bounds, render to a RenderTexture and
        /// read that back, so the result is independent of game state.
        /// </summary>
        public static Dictionary<string, object> RenderObject(UnityEngine.Object target,
            int width, int height, int maxBytes)
        {
            if (target == null) throw new Exception("Target is null");
            if (width <= 0) width = 512;
            if (height <= 0) height = 512;
            if (width > 2048) width = 2048;
            if (height > 2048) height = 2048;
            if (maxBytes <= 0) maxBytes = BridgeConfig.MaxScreenshotBytes;

            Bounds bounds = ComputeBounds(target);

            // An inactive renderer draws nothing, and SetActive cannot force a
            // child active under an inactive parent, so activation is not an
            // option: the object stays unusable. Instead the meshes are copied
            // onto a temporary active GameObject and the camera renders that.
            // The scene is left untouched.
            bool needsProxy = !IsRenderable(target);
            Transform proxyRoot = null;
            int proxyMeshCount = 0;

            GameObject cameraObject = null;
            Camera camera = null;
            RenderTexture render = null;
            Texture2D texture = null;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                if (needsProxy)
                    proxyRoot = BuildProxy(target, bounds, out proxyMeshCount);

                cameraObject = new GameObject("MCP Render Camera");
                camera = cameraObject.AddComponent<Camera>();
                // Place the camera in the object's scene so it sees that scene's
                // lights; a camera in another scene renders an unlit view.
                try
                {
                    GameObject targetGo = target as GameObject
                        ?? (target as Component)?.gameObject;
                    if (targetGo != null && targetGo.scene.IsValid())
                        SceneManager.MoveGameObjectToScene(cameraObject, targetGo.scene);
                }
                catch { }
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                camera.cullingMask = needsProxy && proxyRoot != null
                    ? (1 << proxyRoot.gameObject.layer)
                    : ~0;
                camera.fieldOfView = 35f;

                // Frame the whole object: radius from bounds, distance from the FOV.
                float radius = Math.Max(bounds.extents.magnitude, 0.01f);
                float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
                Vector3 dir = new Vector3(0.6f, 0.35f, 1f).normalized;
                cameraObject.transform.position = bounds.center + dir * distance;
                cameraObject.transform.LookAt(bounds.center);
                camera.nearClipPlane = Math.Max(0.01f, distance - radius * 2f);
                camera.farClipPlane = distance + radius * 2f;

                render = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = render;
                camera.Render();
                RenderTexture.active = render;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                RenderTexture.active = previousActive;

                byte[] png = EncodePng(texture);
                var result = new Dictionary<string, object>
                {
                    { "mime_type", "image/png" },
                    { "width", width },
                    { "height", height },
                    { "bytes", png.Length },
                    { "bounds_center", new Dictionary<string, object>
                        {
                            { "x", bounds.center.x }, { "y", bounds.center.y }, { "z", bounds.center.z }
                        } },
                    { "bounds_size", new Dictionary<string, object>
                        {
                            { "x", bounds.size.x }, { "y", bounds.size.y }, { "z", bounds.size.z }
                        } },
                    { "base64", png.Length <= maxBytes ? Convert.ToBase64String(png) : null }
                };
                if (needsProxy)
                {
                    // Say so explicitly: the caller is looking at a copy, not at
                    // the live object, and some effects (skinning pose, UI masks)
                    // will not match a real screenshot.
                    result["proxy_meshes"] = proxyMeshCount;
                    result["note"] = proxyMeshCount > 0
                        ? "Target is inactive; rendered its meshes through a temporary proxy. " +
                          "Skinned pose and material overrides may differ from the live object."
                        : "Target is inactive and exposes no meshes; image may be empty.";
                }
                if (png.Length > maxBytes)
                    result["note"] = "PNG exceeds max bytes; lower width/height or raise MCP_MAX_SCREENSHOT_BYTES";
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (camera != null)
                {
                    try { camera.targetTexture = null; } catch { }
                }
                if (texture != null) UnityEngine.Object.Destroy(texture);
                if (render != null) RenderTexture.ReleaseTemporary(render);
                if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
                if (proxyRoot != null) UnityEngine.Object.Destroy(proxyRoot.gameObject);
            }
        }

        /// <summary>
        /// True when the object would actually be drawn: active in hierarchy and,
        /// for renderers/behaviours, enabled.
        /// </summary>
        private static bool IsRenderable(UnityEngine.Object target)
        {
            try
            {
                if (target is GameObject go) return go.activeInHierarchy;
                var behaviour = target as Behaviour;
                if (behaviour != null) return behaviour.isActiveAndEnabled;
                var renderer = target as Renderer;
                if (renderer != null) return renderer.enabled && renderer.gameObject.activeInHierarchy;
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Copies the target's meshes into a hidden-in-hierarchy-free temporary
        /// object positioned where the camera expects, leaving the scene intact.
        /// </summary>
        private static Transform BuildProxy(UnityEngine.Object target, Bounds bounds,
            out int meshCount)
        {
            meshCount = 0;
            GameObject root = new GameObject("MCP Render Proxy");
            try
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                root.transform.position = bounds.center;

                Renderer[] renderers;
                Component component = target as Component;
                GameObject source = target as GameObject
                    ?? (component != null ? component.gameObject : null);
                if (source == null) return null;
                renderers = source.GetComponentsInChildren<Renderer>(true);

                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null) continue;
                    Mesh mesh = null;
                    var skinned = renderer as SkinnedMeshRenderer;
                    try { mesh = skinned != null ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh; }
                    catch { }
                    if (mesh == null) continue;

                    GameObject child = new GameObject("mesh");
                    child.hideFlags = HideFlags.HideAndDontSave;
                    child.transform.SetParent(root.transform, false);
                    // Renderer bounds are in world space; proxy root sits at the
                    // combined centre, so place each part relative to it.
                    child.transform.position = renderer.bounds.center;
                    child.transform.rotation = renderer.transform.rotation;
                    child.transform.localScale = renderer.transform.lossyScale;
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var copy = child.AddComponent<MeshRenderer>();
                    copy.sharedMaterials = renderer.sharedMaterials;
                    copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    copy.receiveShadows = false;
                    meshCount++;
                }
                return meshCount > 0 ? root.transform : null;
            }
            catch
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }
        }

        /// <summary>Renderer bounds when available, else transform + collider fallback.</summary>
        private static Bounds ComputeBounds(UnityEngine.Object target)
        {
            Component component = target as Component;
            GameObject gameObject = target as GameObject ?? (component != null ? component.gameObject : null);
            if (gameObject != null)
            {
                try
                {
                    var renderers = gameObject.GetComponentsInChildren<Renderer>(true);
                    bool first = true;
                    Bounds combined = default(Bounds);
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null) continue;
                        if (first) { combined = renderer.bounds; first = false; }
                        else combined.Encapsulate(renderer.bounds);
                    }
                    if (!first) return combined;

                    var colliders = gameObject.GetComponentsInChildren<Collider>(true);
                    foreach (var collider in colliders)
                    {
                        if (collider == null) continue;
                        if (first) { combined = collider.bounds; first = false; }
                        else combined.Encapsulate(collider.bounds);
                    }
                    if (!first) return combined;
                }
                catch { }
                var at = gameObject.transform.position;
                return new Bounds(at, Vector3.one);
            }
            return new Bounds(Vector3.zero, Vector3.one);
        }

        private static byte[] EncodePng(Texture2D texture)
        {
            Type conversion = FindType("UnityEngine.ImageConversion");
            if (conversion != null)
            {
                try
                {
                    MethodInfo method = conversion.GetMethod("EncodeToPNG",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(Texture2D) }, null);
                    if (method != null)
                    {
                        byte[] bytes = method.Invoke(null, new object[] { texture }) as byte[];
                        if (bytes != null) return bytes;
                    }
                }
                catch { }
            }
            MethodInfo legacy = typeof(Texture2D).GetMethod("EncodeToPNG",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (legacy != null)
            {
                byte[] bytes = legacy.Invoke(texture, null) as byte[];
                if (bytes != null) return bytes;
            }
            // No ImageConversion module (Unity 2022 IL2CPP interop, stripped
            // builds): encode the pixels ourselves. CoreModule only.
            return PngWriter.Encode(texture);
        }

        private static Texture2D Downscale(Texture2D source, int scale)
        {
            try
            {
                int divisor = 1 << scale;
                int width = Math.Max(1, source.width / divisor);
                int height = Math.Max(1, source.height / divisor);
                RenderTexture render = RenderTexture.GetTemporary(width, height, 0);
                RenderTexture previous = RenderTexture.active;
                Graphics.Blit(source, render);
                RenderTexture.active = render;
                var result = new Texture2D(width, height, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(render);
                return result;
            }
            catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            Type direct = Type.GetType(fullName);
            if (direct != null) return direct;
            // Optional Unity modules are loaded on demand in BepInEx.
            try
            {
                string assemblyName = fullName.Substring(fullName.LastIndexOf('.') + 1);
                if (assemblyName == "ImageConversion" || assemblyName == "ScreenCapture")
                    Assembly.Load("UnityEngine." + assemblyName + "Module");
            }
            catch { }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>Dependency-free PNG writer (RGBA8, deflate).</summary>
    internal static class PngWriter
    {
        public static byte[] Encode(Texture2D texture)
        {
            int width = texture.width;
            int height = texture.height;
            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length < width * height)
                throw new Exception("Texture pixels unavailable");

            int stride = width * 4 + 1;
            var raw = new byte[stride * height];
            int offset = 0;
            // GetPixels32 is bottom-up; PNG is top-down.
            for (int y = height - 1; y >= 0; y--)
            {
                raw[offset++] = 0; // filter: none
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 color = pixels[row + x];
                    raw[offset++] = color.r;
                    raw[offset++] = color.g;
                    raw[offset++] = color.b;
                    raw[offset++] = color.a;
                }
            }

            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var deflate = new DeflateStream(buffer, CompressionMode.Compress))
                {
                    deflate.Write(raw, 0, raw.Length);
                }
                compressed = buffer.ToArray();
            }

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, 0, 8);

                var ihdr = new byte[13];
                WriteInt32(ihdr, 0, width);
                WriteInt32(ihdr, 4, height);
                ihdr[8] = 8;   // bit depth
                ihdr[9] = 6;   // color type RGBA
                ihdr[10] = 0;  // deflate
                ihdr[11] = 0;  // adaptive filtering
                ihdr[12] = 0;  // no interlace
                WriteChunk(png, "IHDR", ihdr);

                var zlib = new byte[compressed.Length + 6];
                zlib[0] = 0x78;
                zlib[1] = 0x9c;
                Array.Copy(compressed, 0, zlib, 2, compressed.Length);
                WriteInt32(zlib, zlib.Length - 4, (int)Adler32(raw));
                WriteChunk(png, "IDAT", zlib);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteInt32(length, 0, data.Length);
            stream.Write(length, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            uint crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteInt32(crcBytes, 0, (int)crc);
            stream.Write(crcBytes, 0, 4);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] type, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint crc = 0xffffffffu;
            for (int i = 0; i < type.Length; i++)
                crc = _crcTable[(crc ^ type[i]) & 0xff] ^ (crc >> 8);
            for (int i = 0; i < data.Length; i++)
                crc = _crcTable[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffffffffu;
        }
    }
}
