using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using UnityEngine;

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
            try
            {
                texture = CaptureTexture();
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
                    { "base64", Convert.ToBase64String(png) }
                };
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
            }
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
