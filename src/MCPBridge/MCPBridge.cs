using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
#if CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime;
#endif
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UnityExplorer.MCPBridge
{
    public class MCPBridge : MonoBehaviour
    {
        public static MCPBridge Instance { get; private set; }
        
        private TcpListener _tcpListener;
        private Thread _listenerThread;
        private bool _isRunning;
        private readonly Queue<string> _requestQueue = new Queue<string>();
        private readonly Queue<Action<string>> _responseCallbacks = new Queue<Action<string>>();
        private readonly object _queueLock = new object();
        
        public int Port { get; private set; } = 12345;

        // Authoritative bridge-side tool list (mirrored in unity_capabilities).
        // Keep in sync with the dispatch switch in ProcessRequest.
        internal static readonly string[] EnabledTools = new string[]
        {
            "ping", "scene_info", "find_gameobjects", "get_gameobject",
            "get_components", "inspect", "get_field", "set_field",
            "get_property", "set_property", "invoke_method", "hierarchy",
            "execute_csharp", "list_assemblies", "inspect_type", "invoke_static",
            "resolve_path", "capabilities", "search_members", "find_objects_of_type",
            "get_static", "set_static", "list_hooks"
        };

        // Handle stabilization: instance_ids are process-local and die on
        // scene change/restart. SessionId lets callers detect staleness.
        private static int _sceneLoadCount = 0;
        private static bool _sceneHooked = false;
        public static string SessionId
        {
            get
            {
                try { return SceneManager.GetActiveScene().name + "#" + _sceneLoadCount; }
                catch { return "unknown#" + _sceneLoadCount; }
            }
        }

        private static void HookSceneCounter()
        {
            if (_sceneHooked) return;
            _sceneHooked = true;
            // Runtime hook (no compile-time dependency on the interop event shape).
            try
            {
                var ev = typeof(SceneManager).GetEvent("sceneLoaded");
                if (ev != null)
                {
                    Action<Scene, LoadSceneMode> handler = (s, m) => _sceneLoadCount++;
                    var d = Delegate.CreateDelegate(ev.EventHandlerType, handler.Target, handler.Method);
                    ev.AddEventHandler(null, d);
                }
            }
            catch { }
        }
        
#if CPP
        public MCPBridge(IntPtr ptr) : base(ptr) { }
#endif
        
        internal static void Setup()
        {
#if CPP
            ClassInjector.RegisterTypeInIl2Cpp<MCPBridge>();
#endif
            
            GameObject obj = new GameObject("MCPBridge");
            DontDestroyOnLoad(obj);
            obj.hideFlags = HideFlags.HideAndDontSave;
            Instance = obj.AddComponent<MCPBridge>();
            HookSceneCounter();
        }
        
        internal void Awake()
        {
            StartServer();
        }
        
        internal void OnDestroy()
        {
            StopServer();
        }
        
        internal void Update()
        {
            ProcessQueue();
        }
        
        public void StartServer()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, Port);
                _tcpListener.Start();
                
                _isRunning = true;
                _listenerThread = new Thread(ListenLoop);
                _listenerThread.IsBackground = true;
                _listenerThread.Start();
                
                Debug.Log("[MCPBridge] Started on port " + Port);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MCPBridge] Failed to start: " + ex.Message);
            }
        }
        
        public void StopServer()
        {
            _isRunning = false;
            
            try
            {
                if (_tcpListener != null)
                    _tcpListener.Stop();
            }
            catch { }
            
            Debug.Log("[MCPBridge] Stopped");
        }
        
        private void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!_tcpListener.Pending())
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    
                    TcpClient client = _tcpListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogError("[MCPBridge] Listener error: " + ex.Message);
                }
            }
        }
        
        private void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                
                NetworkStream stream = client.GetStream();
                
                // Read Content-Length header
                string requestLine = ReadLine(stream);
                var headers = new Dictionary<string, string>();
                while (true)
                {
                    string line = ReadLine(stream);
                    if (string.IsNullOrEmpty(line)) break;
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line.Substring(0, colon).Trim().ToLower()] = line.Substring(colon + 1).Trim();
                    }
                }
                
                // Read body
                int contentLength = 0;
                if (headers.ContainsKey("content-length"))
                    int.TryParse(headers["content-length"], out contentLength);
                
                byte[] bodyBytes = new byte[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int read = stream.Read(bodyBytes, totalRead, contentLength - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                
                string requestBody = Encoding.UTF8.GetString(bodyBytes);
                
                // Queue request for main thread
                var doneEvent = new ManualResetEvent(false);
                string responseBody = "";
                
                lock (_queueLock)
                {
                    _requestQueue.Enqueue(requestBody);
                    _responseCallbacks.Enqueue(resp =>
                    {
                        responseBody = resp;
                        doneEvent.Set();
                    });
                }
                
                // Wait for response (with timeout)
                doneEvent.WaitOne(30000);
                
                // Send HTTP response
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseBody);
                string responseHeader = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + responseBytes.Length + "\r\nConnection: close\r\n\r\n";
                byte[] headerBytes = Encoding.UTF8.GetBytes(responseHeader);
                
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogError("[MCPBridge] Client error: " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }
        
        private string ReadLine(NetworkStream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                if (b == '\r') continue;
                if (b == '\n') break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
        
        private void ProcessQueue()
        {
            lock (_queueLock)
            {
                while (_requestQueue.Count > 0)
                {
                    string requestBody = _requestQueue.Dequeue();
                    Action<string> callback = _responseCallbacks.Dequeue();
                    
                    try
                    {
                        var response = ProcessRequest(requestBody);
                        callback(response);
                    }
                    catch (Exception ex)
                    {
                        callback(System.Text.Json.JsonSerializer.Serialize(new MCPResponse
                        {
                            Success = false,
                            Error = "Processing error: " + ex.Message
                        }));
                    }
                }
            }
        }
        
        private string ProcessRequest(string requestBody)
        {
            try
            {
                var request = System.Text.Json.JsonSerializer.Deserialize<MCPRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (request == null)
                {
                    return System.Text.Json.JsonSerializer.Serialize(new MCPResponse { Success = false, Error = "Invalid request" });
                }
                
                MCPResponse response;
                string method = request.Method != null ? request.Method.ToLower() : "";
                
                switch (method)
                {
                    case "ping": response = HandlePing(request); break;
                    case "scene_info": response = HandleSceneInfo(request); break;
                    case "find_gameobjects": response = HandleFindGameObjects(request); break;
                    case "get_gameobject": response = HandleGetGameObject(request); break;
                    case "get_components": response = HandleGetComponents(request); break;
                    case "inspect": response = HandleInspect(request); break;
                    case "get_field": response = HandleGetField(request); break;
                    case "set_field": response = HandleSetField(request); break;
                    case "get_property": response = HandleGetProperty(request); break;
                    case "set_property": response = HandleSetProperty(request); break;
                    case "invoke_method": response = HandleInvokeMethod(request); break;
                    case "hierarchy": response = HandleHierarchy(request); break;
                    case "execute_csharp": response = HandleExecuteCSharp(request); break;
                    case "list_assemblies": response = HandleListAssemblies(request); break;
                    case "inspect_type": response = HandleInspectType(request); break;
                    case "invoke_static": response = HandleInvokeStatic(request); break;
                    case "resolve_path": response = HandleResolvePath(request); break;
                    case "capabilities": response = HandleCapabilities(request); break;
                    case "search_members": response = HandleSearchMembers(request); break;
                    case "find_objects_of_type": response = HandleFindObjectsOfType(request); break;
                    case "get_static": response = HandleGetStatic(request); break;
                    case "set_static": response = HandleSetStatic(request); break;
                    case "list_hooks": response = HandleListHooks(request); break;
                    default: response = new MCPResponse { Success = false, Error = "Unknown: " + request.Method }; break;
                }
                
                return System.Text.Json.JsonSerializer.Serialize(response);
            }
            catch (Exception ex)
            {
                return System.Text.Json.JsonSerializer.Serialize(new MCPResponse { Success = false, Error = ex.Message });
            }
        }
        
        #region Request Handlers
        
        private string StaleHandleError(int instanceId)
        {
            return "Stale handle " + instanceId + ": object destroyed or scene changed (session " + SessionId + "). Re-run find_gameobjects or resolve_path with the Hierarchy path instead of guessing IDs.";
        }

        private MCPResponse HandlePing(MCPRequest request)
        {
            return new MCPResponse
            {
                Success = true,
                Data = new PingResponse
                {
                    Status = "ok",
                    UnityVersion = Application.unityVersion,
                    BridgeVersion = "1.2.0",
                    SessionId = SessionId
                }
            };
        }
        
        private MCPResponse HandleSceneInfo(MCPRequest request)
        {
            var activeScene = SceneManager.GetActiveScene();
            var loadedScenes = new List<SceneInfo>();
            
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    loadedScenes.Add(new SceneInfo
                    {
                        Name = scene.name,
                        BuildIndex = scene.buildIndex,
                        Path = scene.path,
                        RootCount = scene.rootCount
                    });
                }
            }
            
            return new MCPResponse
            {
                Success = true,
                Data = new SceneInfoResponse
                {
                    ActiveScene = new SceneInfo
                    {
                        Name = activeScene.name,
                        BuildIndex = activeScene.buildIndex,
                        Path = activeScene.path,
                        RootCount = activeScene.rootCount
                    },
                    LoadedScenes = loadedScenes,
                    SessionId = SessionId
                }
            };
        }
        
        private MCPResponse HandleFindGameObjects(MCPRequest request)
        {
            string name = null;
            bool includeInactive = true;
            
            if (request.Params is FindGameObjectsParams p)
            {
                name = p.Name;
                includeInactive = p.IncludeInactive;
            }
            else if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("name", out var nameProp) || j.TryGetProperty("Name", out nameProp))
                    name = nameProp.GetString();
                if (j.TryGetProperty("include_inactive", out var inactiveProp) || j.TryGetProperty("IncludeInactive", out inactiveProp))
                    includeInactive = inactiveProp.GetBoolean();
            }
            
            var results = new List<GameObjectInfo>();
            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();

            Paging.Params paging = null;
            string contains = null;
            if (request.Params is System.Text.Json.JsonElement pj)
            {
                paging = Paging.Read(pj, 0);
                contains = paging.NameContains;
            }

            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                try
                {
                    if (obj.transform != null && obj.transform.root != null &&
                        obj.transform.root.name == "UniverseLibCanvas") continue;

                    if (!string.IsNullOrEmpty(name) && !obj.name.Contains(name)) continue;
                    if (!string.IsNullOrEmpty(contains) && obj.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (!includeInactive && !obj.activeInHierarchy) continue;

                    int id = obj.GetInstanceID();
                    results.Add(new GameObjectInfo
                    {
                        Name = obj.name,
                        InstanceId = id,
                        Handle = Handle.Format(id),
                        Scene = obj.scene.name,
                        Active = obj.activeInHierarchy,
                        Path = GetGameObjectPath(obj),
                        Tag = obj.tag,
                        Layer = obj.layer
                    });
                }
                catch { }
            }

            if (paging != null && paging.Requested)
            {
                var page = Paging.Apply(results, paging, null);
                return new MCPResponse { Success = true, Data = Paging.Envelope(page) };
            }
            return new MCPResponse { Success = true, Data = results };
        }
        
        private MCPResponse HandleGetGameObject(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            
            var obj = FindObjectById(instanceId) as GameObject;
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) };
            
            var info = new GameObjectDetailInfo
            {
                Name = obj.name,
                InstanceId = obj.GetInstanceID(),
                Handle = Handle.Format(obj.GetInstanceID()),
                Scene = obj.scene.name,
                Active = obj.activeInHierarchy,
                Path = GetGameObjectPath(obj),
                Tag = obj.tag,
                Layer = obj.layer,
                Position = new Vector3Info(obj.transform.position),
                Rotation = new Vector3Info(obj.transform.eulerAngles),
                Scale = new Vector3Info(obj.transform.localScale),
                Parent = null,
                Children = new List<GameObjectInfo>()
            };
            
            if (obj.transform.parent != null)
            {
                int pid = obj.transform.parent.gameObject.GetInstanceID();
                info.Parent = new GameObjectInfo
                {
                    Name = obj.transform.parent.gameObject.name,
                    InstanceId = pid,
                    Handle = Handle.Format(pid)
                };
            }

            for (int i = 0; i < obj.transform.childCount; i++)
            {
                var child = obj.transform.GetChild(i).gameObject;
                int cid = child.GetInstanceID();
                info.Children.Add(new GameObjectInfo
                {
                    Name = child.name,
                    InstanceId = cid,
                    Handle = Handle.Format(cid),
                    Active = child.activeInHierarchy
                });
            }
            
            return new MCPResponse { Success = true, Data = info };
        }
        
        private MCPResponse HandleGetComponents(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            
            var obj = FindObjectById(instanceId) as GameObject;
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) };
            
            var components = new List<ComponentInfo>();
            foreach (var comp in obj.GetComponents<Component>())
            {
                if (comp == null) continue;
                bool? enabled = null;
                if (comp is Behaviour) enabled = ((Behaviour)comp).enabled;
components.Add(new ComponentInfo
                {
#if CPP
                    TypeName = GetIl2CppTypeName(comp),
#else
                    TypeName = comp.GetType().FullName,
#endif
                    InstanceId = comp.GetInstanceID(),
                    Enabled = enabled
                });
            }
            
            return new MCPResponse { Success = true, Data = components };
        }
        
        private MCPResponse HandleInspect(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            int memberLimit = 200;
            if (request.Params is System.Text.Json.JsonElement pj)
            {
                System.Text.Json.JsonElement tmp;
                if (pj.TryGetProperty("member_limit", out tmp) || pj.TryGetProperty("MemberLimit", out tmp))
                { try { memberLimit = Math.Max(1, tmp.GetInt32()); } catch { } }
            }

            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) + " (inspect)" };
            
            var info = new InspectResponse
            {
                TypeName = "",
                InstanceId = obj.GetInstanceID(),
                Fields = new List<MemberInfoItem>(),
                Properties = new List<MemberInfoItem>(),
                Methods = new List<MethodInfoItem>()
            };
            
            try
            {
#if CPP
                if (obj is Component comp)
                {
                    IntPtr klass = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                    info.TypeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
                    
                    IntPtr iter = IntPtr.Zero;
                    IntPtr field;
                    while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
                    {
                        try
                        {
                            string fieldName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                            IntPtr fieldType = IL2CPP.il2cpp_field_get_type(field);
                            string fieldTypeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(fieldType));
                            
                            string value = "N/A";
                            bool isStatic = (IL2CPP.il2cpp_field_get_flags(field) & 0x0010) != 0;
                            
                            // Only read values for safe types to avoid crashes
                            bool isSafeType = fieldTypeName.StartsWith("System.") && 
                                (fieldTypeName == "System.Int32" || fieldTypeName == "System.Single" || 
                                 fieldTypeName == "System.Boolean" || fieldTypeName == "System.String" ||
                                 fieldTypeName == "System.Int64" || fieldTypeName == "System.Double");
                            
                            if (isSafeType)
                            {
                                try
                                {
                                    IntPtr valuePtr = Marshal.AllocHGlobal(8);
                                    unsafe
                                    {
                                        if (isStatic)
                                            IL2CPP.il2cpp_field_static_get_value(field, (void*)valuePtr);
                                        else
                                            IL2CPP.il2cpp_field_get_value(comp.Pointer, field, (void*)valuePtr);
                                    }
                                    
                                    value = FormatFieldValue(valuePtr, fieldTypeName);
                                    Marshal.FreeHGlobal(valuePtr);
                                }
                                catch { }
                            }
                            
                            info.Fields.Add(new MemberInfoItem
                            {
                                Name = fieldName,
                                TypeName = fieldTypeName,
                                Value = value,
                                IsStatic = isStatic,
                                CanWrite = true
                            });
                        }
                        catch { }
                    }
                    
                    iter = IntPtr.Zero;
                    IntPtr method;
                    while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                    {
                        try
                        {
                            string methodName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method));
                            if (string.IsNullOrEmpty(methodName) || methodName.StartsWith(".")) continue;
                            
                            uint paramCount = IL2CPP.il2cpp_method_get_param_count(method);
                            var paramTypes = new List<string>();
                            for (uint i = 0; i < paramCount; i++)
                            {
                                IntPtr paramType = IL2CPP.il2cpp_method_get_param(method, i);
                                paramTypes.Add(Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(paramType)));
                            }
                            
                            IntPtr returnType = IL2CPP.il2cpp_method_get_return_type(method);
                            string returnTypeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(returnType));
                            
                            info.Methods.Add(new MethodInfoItem
                            {
                                Name = methodName,
                                ReturnType = returnTypeName,
                                Parameters = paramTypes.Select(t => new ParameterInfoItem { Name = "", TypeName = t }).ToList(),
                                IsStatic = false
                            });
                        }
                        catch { }
                    }
                }
#endif
#if CPP
                else
#endif
                {
                    // Mono backend (or non-Component on IL2CPP): managed reflection.
                    var type = obj.GetType();
                    info.TypeName = type.FullName;
                    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                    
                    foreach (var field in type.GetFields(flags))
                    {
                        try
                        {
                            object value = field.GetValue(field.IsStatic ? null : obj);
                            info.Fields.Add(new MemberInfoItem
                            {
                                Name = field.Name,
                                TypeName = field.FieldType.FullName,
                                Value = value != null ? value.ToString() : "null",
                                IsStatic = field.IsStatic,
                                CanWrite = !(field.IsLiteral && !field.IsInitOnly)
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Fields.Add(new MemberInfoItem { Name = "Error", TypeName = "string", Value = ex.Message, IsStatic = false, CanWrite = false });
            }

            info.TotalFields = info.Fields.Count;
            info.TotalMethods = info.Methods.Count;
            if (info.Fields.Count > memberLimit)
                info.Fields = info.Fields.GetRange(0, memberLimit);
            if (info.Methods.Count > memberLimit)
                info.Methods = info.Methods.GetRange(0, memberLimit);

            return new MCPResponse { Success = true, Data = info };
        }
        
#if CPP
        private string FormatFieldValue(IntPtr valuePtr, string typeName)
        {
            if (valuePtr == IntPtr.Zero) return "null";
            
            try
            {
                switch (typeName.ToLower())
                {
                    case "system.int32":
                    case "int":
                        return Marshal.ReadInt32(valuePtr).ToString();
                    case "system.int64":
                    case "long":
                        return Marshal.ReadInt64(valuePtr).ToString();
                    case "system.single":
                    case "float":
                        return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(valuePtr)), 0).ToString();
                    case "system.double":
                        return BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(valuePtr)), 0).ToString();
                    case "system.boolean":
                    case "bool":
                        return (Marshal.ReadByte(valuePtr) != 0).ToString();
                    case "system.string":
                        IntPtr strPtr = Marshal.ReadIntPtr(valuePtr);
                        if (strPtr == IntPtr.Zero) return "null";
                        return Marshal.PtrToStringAnsi(strPtr);
                    default:
                        IntPtr objPtr = Marshal.ReadIntPtr(valuePtr);
                        if (objPtr == IntPtr.Zero) return "null";
                        return $"[Object @ {objPtr}]";
                }
            }
            catch { return "N/A"; }
        }
#endif
        private MCPResponse HandleGetField(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            string fieldName = ExtractString(request, "field") ?? ExtractString(request, "Field");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) };
            
            var type = obj.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null)
                return new MCPResponse { Success = false, Error = "Field not found: " + fieldName };
            
            try
            {
                object value = field.GetValue(field.IsStatic ? null : obj);
                return new MCPResponse { Success = true, Data = new FieldValueResponse { FieldName = field.Name, TypeName = field.FieldType.FullName, Value = value != null ? value.ToString() : "null" } };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.Message }; }
        }
        
        private MCPResponse HandleSetField(MCPRequest request)
        {
            MCPResponse stale = ValidateWriteHandle(request);
            if (stale != null) return stale;
            int instanceId = ExtractInstanceId(request);
            string fieldName = ExtractString(request, "field") ?? ExtractString(request, "Field");
            object valueObj = ExtractValue(request, "value") ?? ExtractValue(request, "Value");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) };
            
            var type = obj.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null)
                return new MCPResponse { Success = false, Error = "Field not found: " + fieldName };
            
            try
            {
                object value = ConvertValue(valueObj, field.FieldType);
                field.SetValue(field.IsStatic ? null : obj, value);
                return new MCPResponse { Success = true, Data = new { FieldName = field.Name, NewValue = value != null ? value.ToString() : "null" } };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.Message }; }
        }
        
        private MCPResponse HandleGetProperty(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            string propertyName = ExtractString(request, "property") ?? ExtractString(request, "Property");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) };
            
            var type = obj.GetType();
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (prop == null)
                return new MCPResponse { Success = false, Error = "Property not found: " + propertyName };
            if (!prop.CanRead)
                return new MCPResponse { Success = false, Error = "Property not readable: " + propertyName };
            
            try
            {
                var getter = prop.GetGetMethod(true);
                object value = getter != null ? prop.GetValue(getter.IsStatic ? null : obj, null) : null;
                return new MCPResponse { Success = true, Data = new PropertyValueResponse { PropertyName = prop.Name, TypeName = prop.PropertyType.FullName, Value = value != null ? value.ToString() : "null" } };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.Message }; }
        }
        
        private MCPResponse HandleSetProperty(MCPRequest request)
        {
            MCPResponse stale = ValidateWriteHandle(request);
            if (stale != null) return stale;
            int instanceId = ExtractInstanceId(request);
            string propertyName = ExtractString(request, "property") ?? ExtractString(request, "Property");
            object valueObj = ExtractValue(request, "value") ?? ExtractValue(request, "Value");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = StaleHandleError(instanceId) };
            
            var type = obj.GetType();
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (prop == null)
                return new MCPResponse { Success = false, Error = "Property not found: " + propertyName };
            if (!prop.CanWrite)
                return new MCPResponse { Success = false, Error = "Property not writable: " + propertyName };
            
            try
            {
                object value = ConvertValue(valueObj, prop.PropertyType);
                prop.SetValue(prop.GetAccessors(true)[0].IsStatic ? null : obj, value, null);
                return new MCPResponse { Success = true, Data = new { PropertyName = prop.Name, NewValue = value != null ? value.ToString() : "null" } };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.Message }; }
        }
        
        private MCPResponse HandleInvokeMethod(MCPRequest request)
        {
            MCPResponse stale = ValidateWriteHandle(request);
            if (stale != null) return stale;
            int instanceId = ExtractInstanceId(request);
            string typeName = ExtractString(request, "type") ?? ExtractString(request, "Type");
            string methodName = ExtractString(request, "method") ?? ExtractString(request, "Method");
            object[] args = null;
            if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("args", out var argsProp) || j.TryGetProperty("Args", out argsProp))
                {
                    if (argsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var argList = new System.Collections.Generic.List<object>();
                        foreach (var item in argsProp.EnumerateArray())
                        {
                            if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                                argList.Add(item.GetString());
                            else if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                                argList.Add(item.GetDouble());
                            else if (item.ValueKind == System.Text.Json.JsonValueKind.True)
                                argList.Add(true);
                            else if (item.ValueKind == System.Text.Json.JsonValueKind.False)
                                argList.Add(false);
                            else
                                argList.Add(null);
                        }
                        args = argList.ToArray();
                    }
                }
            }
            
            UnityEngine.Object obj = null;
            if (instanceId != 0) obj = FindObjectById(instanceId);

#if CPP
            if (obj is Component comp)
            {
                return InvokeIl2CppMethod(comp, methodName, args);
            }
#endif
            
            Type type = null;
            if (obj != null) type = obj.GetType();
            else if (!string.IsNullOrEmpty(typeName)) type = AssemblyInspector.ResolveType(null, typeName);

            if (type == null)
                return new MCPResponse { Success = false, Error = "Type not found: " + typeName };

            var rawArgs = new System.Collections.Generic.List<System.Text.Json.JsonElement>();
            if (request.Params is System.Text.Json.JsonElement j2)
                rawArgs = ValueSerializer.GetArgsArray(j2);

            string returnEncoding = ExtractString(request, "returnEncoding") ?? ExtractString(request, "ReturnEncoding") ?? "hex";

            AssemblyInspector.StaticCall call;
            try { call = AssemblyInspector.ResolveInstanceCall(type, methodName, rawArgs); }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.Message }; }

            if (!call.Method.IsStatic && obj == null)
                return new MCPResponse { Success = false, Error = "Method " + methodName + " is an instance method but no live instance_id was given (" + StaleHandleError(instanceId) + ")" };

            try
            {
                // out/ref: callee must run before we read ConvertedArgs back.
                object result = call.Method.Invoke(call.Method.IsStatic ? null : obj, call.ConvertedArgs);
                var outArgs = AssemblyInspector.CollectOutArgs(call.Method, call.ConvertedArgs, returnEncoding);
                return new MCPResponse
                {
                    Success = true,
                    Data = new
                    {
                        methodName = call.Method.Name,
                        returnType = call.Method.ReturnType.FullName,
                        result = result != null ? result.ToString() : "null",
                        resultValue = ValueSerializer.Serialize(result, returnEncoding),
                        outArgs = outArgs
                    }
                };
            }
            catch (TargetInvocationException tie)
            {
                return new MCPResponse { Success = false, Error = (tie.InnerException ?? tie).ToString() };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }
        
        private MCPResponse HandleHierarchy(MCPRequest request)
        {
            int maxDepth = 10;
            Paging.Params paging = null;
            if (request.Params is HierarchyParams hp) maxDepth = hp.MaxDepth;
            else if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("max_depth", out var md) || j.TryGetProperty("MaxDepth", out md))
                {
                    try { maxDepth = md.GetInt32(); } catch { }
                    if (maxDepth < 1) maxDepth = 1;
                    if (maxDepth > 20) maxDepth = 20;
                }
                paging = Paging.Read(j, 0);
            }

            string contains = paging != null ? paging.NameContains : null;
            int budget = (paging != null && paging.Limit > 0) ? paging.Limit : int.MaxValue;
            int used = 0;
            bool truncated = false;

            var rootObjects = new List<HierarchyInfo>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var node = BuildHierarchy(root, 0, maxDepth, contains, ref used, budget, ref truncated);
                    if (node != null) rootObjects.Add(node);
                    if (used >= budget) { truncated = true; break; }
                }
                if (used >= budget) { truncated = true; break; }
            }

            if (paging != null && paging.Requested)
            {
                return new MCPResponse
                {
                    Success = true,
                    Data = new Dictionary<string, object>
                    {
                        { "items", rootObjects },
                        { "truncated", truncated }
                    }
                };
            }
            return new MCPResponse { Success = true, Data = rootObjects };
        }

        private HierarchyInfo BuildHierarchy(GameObject obj, int currentDepth, int maxDepth,
            string contains, ref int used, int budget, ref bool truncated)
        {
            if (used >= budget) { truncated = true; return null; }
            bool selfMatch = string.IsNullOrEmpty(contains)
                || obj.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0;

            var info = new HierarchyInfo
            {
                Name = obj.name,
                InstanceId = obj.GetInstanceID(),
                Active = obj.activeInHierarchy,
                Children = new List<HierarchyInfo>()
            };

            if (currentDepth < maxDepth)
            {
                for (int i = 0; i < obj.transform.childCount; i++)
                {
                    if (used >= budget) { truncated = true; break; }
                    var child = BuildHierarchy(obj.transform.GetChild(i).gameObject,
                        currentDepth + 1, maxDepth, contains, ref used, budget, ref truncated);
                    if (child != null) info.Children.Add(child);
                }
            }

            // name_contains prunes subtrees with no match (ancestors of matches survive).
            if (!selfMatch && info.Children.Count == 0) return null;
            used++;
            info.Handle = Handle.Format(info.InstanceId);
            return info;
        }
        
        private MCPResponse HandleExecuteCSharp(MCPRequest request)
        {
            string code = ExtractString(request, "code") ?? ExtractString(request, "Code");
            if (string.IsNullOrEmpty(code))
                return new MCPResponse { Success = false, Error = "Missing 'code' parameter" };
            string encoding = ExtractString(request, "returnEncoding") ?? ExtractString(request, "ReturnEncoding") ?? "hex";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = CSharpExecutor.Execute(code, encoding);
            sw.Stop();

            if (!result.Compiled && result.ReturnValue == null)
                return new MCPResponse { Success = false, Error = result.Error, Data = new { compilerOutput = result.CompilerOutput, elapsedMs = sw.ElapsedMilliseconds } };
            if (!string.IsNullOrEmpty(result.Error))
                return new MCPResponse { Success = false, Error = result.Error, Data = new { compilerOutput = result.CompilerOutput, elapsedMs = sw.ElapsedMilliseconds } };
            return new MCPResponse
            {
                Success = true,
                Data = new
                {
                    result = result.ReturnValue,
                    returnType = result.ReturnType,
                    compilerOutput = result.CompilerOutput,
                    elapsedMs = sw.ElapsedMilliseconds
                }
            };
        }

        private MCPResponse HandleListAssemblies(MCPRequest request)
        {
            string filter = ExtractString(request, "filter") ?? ExtractString(request, "Filter");
            try
            {
                var names = AssemblyInspector.ListAssemblies(filter);
                if (request.Params is System.Text.Json.JsonElement pj)
                {
                    var paging = Paging.Read(pj, 0);
                    if (paging.Requested)
                    {
                        var page = Paging.Apply(names, paging, null);
                        var env = (Dictionary<string, object>)Paging.Envelope(page);
                        env["assemblies"] = env["items"];
                        env.Remove("items");
                        return new MCPResponse { Success = true, Data = env };
                    }
                }
                return new MCPResponse { Success = true, Data = new { assemblies = names, count = names.Count } };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleInspectType(MCPRequest request)
        {
            string asm = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            string type = ExtractString(request, "type") ?? ExtractString(request, "Type");
            if (string.IsNullOrEmpty(type))
                return new MCPResponse { Success = false, Error = "Missing 'type' parameter (full type name, e.g. System.Math)" };
            try
            {
                int memberLimit = 200;
                string memberContains = null;
                if (request.Params is System.Text.Json.JsonElement pj)
                {
                    System.Text.Json.JsonElement tmp;
                    if (pj.TryGetProperty("member_limit", out tmp) || pj.TryGetProperty("MemberLimit", out tmp))
                    { try { memberLimit = Math.Max(1, tmp.GetInt32()); } catch { } }
                    if (pj.TryGetProperty("member_contains", out tmp) || pj.TryGetProperty("MemberContains", out tmp))
                    { try { memberContains = tmp.GetString(); } catch { } }
                }
                var info = AssemblyInspector.InspectType(asm, type, memberLimit, memberContains);
                return new MCPResponse { Success = true, Data = info };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleInvokeStatic(MCPRequest request)
        {
            string asm = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            string typeName = ExtractString(request, "type") ?? ExtractString(request, "Type");
            string methodName = ExtractString(request, "method") ?? ExtractString(request, "Method");
            string encoding = ExtractString(request, "returnEncoding") ?? ExtractString(request, "ReturnEncoding") ?? "hex";
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                return new MCPResponse { Success = false, Error = "Missing 'type' and/or 'method' parameter" };

            try
            {
                Type type = AssemblyInspector.ResolveType(asm, typeName);
                if (type == null)
                {
#if CPP
                    // Fallback: pure-Il2Cpp static call via il2cpp_class_from_name.
                    return InvokeIl2CppStatic(typeName, methodName, request, encoding);
#else
                    // Mono backend: managed reflection sees every type; nothing more to try.
                    return new MCPResponse { Success = false, Error = "Type not found: " + typeName };
#endif
                }

                var rawArgs = new System.Collections.Generic.List<System.Text.Json.JsonElement>();
                if (request.Params is System.Text.Json.JsonElement j)
                    rawArgs = ValueSerializer.GetArgsArray(j);

                var call = AssemblyInspector.ResolveStaticCall(type, methodName, rawArgs);
                object result = call.Method.Invoke(null, call.ConvertedArgs);

                var outArgs = new System.Collections.Generic.List<object>();
                ParameterInfo[] ps = call.Method.GetParameters();
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
                            { "value", ValueSerializer.Serialize(call.ConvertedArgs[i], encoding) }
                        });
                    }
                }
                return new MCPResponse
                {
                    Success = true,
                    Data = new
                    {
                        methodName = call.Method.Name,
                        returnType = call.Method.ReturnType.FullName,
                        result = result != null ? result.ToString() : "null",
                        resultValue = ValueSerializer.Serialize(result, encoding),
                        outArgs = outArgs
                    }
                };
            }
            catch (TargetInvocationException tie)
            {
                return new MCPResponse { Success = false, Error = (tie.InnerException ?? tie).ToString() };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleResolvePath(MCPRequest request)
        {
            string path = ExtractString(request, "path") ?? ExtractString(request, "Path");
            if (string.IsNullOrEmpty(path))
                return new MCPResponse { Success = false, Error = "Missing 'path' parameter (e.g. Root/Child)" };
            try
            {
                var go = AssemblyInspector.ResolvePath(path);
                if (go == null)
                    return new MCPResponse { Success = false, Error = "Path not found: " + path + " (session " + SessionId + ")" };
                int freshId = go.GetInstanceID();
                return new MCPResponse
                {
                    Success = true,
                    Data = new GameObjectInfo
                    {
                        Name = go.name,
                        InstanceId = freshId,
                        Handle = Handle.Format(freshId),
                        Scene = go.scene.name,
                        Active = go.activeInHierarchy,
                        Path = AssemblyInspector.GetGameObjectPath(go),
                        Tag = go.tag,
                        Layer = go.layer
                    }
                };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleCapabilities(MCPRequest request)
        {
            string bepinExVersion = null;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n = null;
                    try { n = asm.GetName().Name; } catch { continue; }
                    if (n == "BepInEx" || n == "BepInEx.Core")
                    {
                        try { bepinExVersion = asm.GetName().Version.ToString(); } catch { }
                        break;
                    }
                }
            }
            catch { }
            return new MCPResponse
            {
                Success = true,
                Data = new Dictionary<string, object>
                {
                    { "backend", BridgeConfig.Backend },
                    { "loader", "BepInEx" },
                    { "bepinex_version", bepinExVersion ?? "unknown" },
                    { "bridge_version", "1.2.0" },
                    { "unity_version", Application.unityVersion },
                    { "session_id", SessionId },
                    { "enabled_tools", new List<string>(EnabledTools) },
                    { "limits", new Dictionary<string, object>
                        {
                            { "array_limit", BridgeConfig.ArrayLimit },
                            { "string_limit", BridgeConfig.StringLimit },
                            { "byte_limit", BridgeConfig.ByteLimit },
                            { "request_timeout_ms", BridgeConfig.RequestTimeoutMs }
                        }
                    },
                    { "marshalling", new Dictionary<string, object>
                        {
                            { "ref", true },
                            { "out", true },
                            { "byte_array", true },
#if CPP
                            { "il2cpp_array", true },
#else
                            { "il2cpp_array", false },
#endif
                            { "csharp_execute", true }
                        }
                    }
                }
            };
        }

        private MCPResponse HandleSearchMembers(MCPRequest request)
        {
            string nameContains = ExtractString(request, "name_contains") ?? ExtractString(request, "NameContains")
                ?? ExtractString(request, "name") ?? ExtractString(request, "Name");
            if (string.IsNullOrEmpty(nameContains))
                return new MCPResponse { Success = false, Error = "Missing 'name_contains' parameter" };
            string typeFilter = ExtractString(request, "type_filter") ?? ExtractString(request, "TypeFilter");
            string memberKind = ExtractString(request, "member_kind") ?? ExtractString(request, "MemberKind") ?? "all";
            string assemblyFilter = ExtractString(request, "assembly_filter") ?? ExtractString(request, "AssemblyFilter");
            int limit = 50;
            int cursor = 0;
            if (request.Params is System.Text.Json.JsonElement pj)
            {
                var paging = Paging.Read(pj, 50);
                limit = paging.Limit > 0 ? paging.Limit : 50;
                cursor = paging.Cursor;
            }
            try
            {
                var all = AssemblyInspector.SearchMembers(nameContains, typeFilter, memberKind, assemblyFilter);
                var page = Paging.Apply(all, new Paging.Params
                {
                    Limit = limit,
                    Cursor = cursor,
                    Requested = true
                }, null);
                return new MCPResponse { Success = true, Data = Paging.Envelope(page) };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleFindObjectsOfType(MCPRequest request)
        {
            string typeName = ExtractString(request, "type") ?? ExtractString(request, "Type");
            if (string.IsNullOrEmpty(typeName))
                return new MCPResponse { Success = false, Error = "Missing 'type' parameter (full type name)" };
            string asm = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            int limit = 100;
            int cursor = 0;
            if (request.Params is System.Text.Json.JsonElement pj)
            {
                var paging = Paging.Read(pj, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try
            {
                var all = AssemblyInspector.FindObjectsOfType(asm, typeName);
                var page = Paging.Apply(all, new Paging.Params
                {
                    Limit = limit,
                    Cursor = cursor,
                    Requested = true
                }, null);
                return new MCPResponse { Success = true, Data = Paging.Envelope(page) };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private static bool ResolveStaticMember(string asm, string typeName, string member,
            out Type type, out FieldInfo field, out PropertyInfo prop, out string error)
        {
            type = null;
            field = null;
            prop = null;
            error = null;
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(member))
            {
                error = "Missing 'type' and/or 'member' parameter";
                return false;
            }
            type = AssemblyInspector.ResolveType(asm, typeName);
            if (type == null)
            {
                error = "Type not found: " + typeName;
                return false;
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            field = type.GetField(member, flags);
            if (field == null) prop = type.GetProperty(member, flags);
            if (field == null && prop == null)
            {
                error = "Static field/property not found: " + type.FullName + "." + member;
                return false;
            }
            return true;
        }

        private MCPResponse HandleGetStatic(MCPRequest request)
        {
            string asm = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            string typeName = ExtractString(request, "type") ?? ExtractString(request, "Type");
            string member = ExtractString(request, "member") ?? ExtractString(request, "Member");
            string encoding = ExtractString(request, "returnEncoding") ?? ExtractString(request, "ReturnEncoding") ?? "hex";
            Type type;
            FieldInfo field;
            PropertyInfo prop;
            string error;
            if (!ResolveStaticMember(asm, typeName, member, out type, out field, out prop, out error))
                return new MCPResponse { Success = false, Error = error };
            try
            {
                object value;
                string valueType;
                if (field != null)
                {
                    value = field.GetValue(null);
                    valueType = field.FieldType.FullName;
                    member = field.Name;
                }
                else
                {
                    if (!prop.CanRead)
                        return new MCPResponse { Success = false, Error = "Property not readable: " + prop.Name };
                    value = prop.GetValue(null, null);
                    valueType = prop.PropertyType.FullName;
                    member = prop.Name;
                }
                return new MCPResponse
                {
                    Success = true,
                    Data = new Dictionary<string, object>
                    {
                        { "type", type.FullName },
                        { "member", member },
                        { "member_type", valueType },
                        { "is_static", true },
                        { "value", ValueSerializer.Serialize(value, encoding) }
                    }
                };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleSetStatic(MCPRequest request)
        {
            string asm = ExtractString(request, "assembly") ?? ExtractString(request, "Assembly");
            string typeName = ExtractString(request, "type") ?? ExtractString(request, "Type");
            string member = ExtractString(request, "member") ?? ExtractString(request, "Member");
            Type type;
            FieldInfo field;
            PropertyInfo prop;
            string error;
            if (!ResolveStaticMember(asm, typeName, member, out type, out field, out prop, out error))
                return new MCPResponse { Success = false, Error = error };
            try
            {
                object rawValue = ExtractValue(request, "value") ?? ExtractValue(request, "Value");
                System.Text.Json.JsonElement rawEl = default(System.Text.Json.JsonElement);
                bool hasEl = false;
                if (request.Params is System.Text.Json.JsonElement j)
                {
                    System.Text.Json.JsonElement v;
                    if (j.TryGetProperty("value", out v) || j.TryGetProperty("Value", out v))
                    {
                        rawEl = v;
                        hasEl = true;
                    }
                }
                if (field != null)
                {
                    if (field.IsLiteral && !field.IsInitOnly)
                        return new MCPResponse { Success = false, Error = "Field is const and cannot be written: " + field.Name };
                    object converted = hasEl
                        ? ValueSerializer.ConvertJsonElement(rawEl, field.FieldType)
                        : ConvertValue(rawValue, field.FieldType);
                    field.SetValue(null, converted);
                    return new MCPResponse
                    {
                        Success = true,
                        Data = new Dictionary<string, object>
                        {
                            { "type", type.FullName },
                            { "member", field.Name },
                            { "newValue", ValueSerializer.Serialize(converted, "hex") }
                        }
                    };
                }
                if (!prop.CanWrite)
                    return new MCPResponse { Success = false, Error = "Property not writable: " + prop.Name };
                object pconverted = hasEl
                    ? ValueSerializer.ConvertJsonElement(rawEl, prop.PropertyType)
                    : ConvertValue(rawValue, prop.PropertyType);
                prop.SetValue(null, pconverted, null);
                return new MCPResponse
                {
                    Success = true,
                    Data = new Dictionary<string, object>
                    {
                        { "type", type.FullName },
                        { "member", prop.Name },
                        { "newValue", ValueSerializer.Serialize(pconverted, "hex") }
                    }
                };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        private MCPResponse HandleListHooks(MCPRequest request)
        {
            int limit = 100;
            int cursor = 0;
            if (request.Params is System.Text.Json.JsonElement pj)
            {
                var paging = Paging.Read(pj, 100);
                limit = paging.Limit > 0 ? paging.Limit : 100;
                cursor = paging.Cursor;
            }
            try
            {
                var all = ListHarmonyPatches();
                var page = Paging.Apply(all, new Paging.Params
                {
                    Limit = limit,
                    Cursor = cursor,
                    Requested = true
                }, null);
                return new MCPResponse { Success = true, Data = Paging.Envelope(page) };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.ToString() }; }
        }

        /// <summary>
        /// P1-4 read-only: enumerate Harmony-patched methods via reflection only,
        /// so no Harmony reference is needed on either backend.
        /// </summary>
        private static List<Dictionary<string, object>> ListHarmonyPatches()
        {
            var results = new List<Dictionary<string, object>>();
            Type harmonyType = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType("HarmonyLib.Harmony");
                    if (t != null) { harmonyType = t; break; }
                }
                catch { }
            }
            if (harmonyType == null)
            {
                results.Add(new Dictionary<string, object>
                {
                    { "note", "HarmonyLib is not loaded in this runtime; no patch inventory available." }
                });
                return results;
            }
            try
            {
                MethodInfo getAll = harmonyType.GetMethod("GetAllPatchedMethods",
                    BindingFlags.Public | BindingFlags.Static);
                MethodInfo getInfo = harmonyType.GetMethod("GetPatchInfo",
                    BindingFlags.Public | BindingFlags.Static);
                if (getAll == null) return results;
                var patched = getAll.Invoke(null, null) as System.Collections.IEnumerable;
                if (patched == null) return results;
                foreach (object m in patched)
                {
                    try
                    {
                        var mb = m as MethodBase;
                        if (mb == null) continue;
                        var entry = new Dictionary<string, object>
                        {
                            { "method", (mb.DeclaringType != null ? mb.DeclaringType.FullName + "." : "") + mb.Name },
                            { "owners", new List<string>() }
                        };
                        if (getInfo != null)
                        {
                            object info = getInfo.Invoke(null, new object[] { mb });
                            if (info != null)
                            {
                                var owners = (List<string>)entry["owners"];
                                foreach (string slot in new string[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                                {
                                    PropertyInfo pi = info.GetType().GetProperty(slot);
                                    if (pi == null) continue;
                                    var patches = pi.GetValue(info, null) as System.Collections.IEnumerable;
                                    if (patches == null) continue;
                                    foreach (object patch in patches)
                                    {
                                        try
                                        {
                                            PropertyInfo ownerProp = patch.GetType().GetProperty("owner");
                                            if (ownerProp == null) continue;
                                            string owner = ownerProp.GetValue(patch, null) as string;
                                            if (!string.IsNullOrEmpty(owner) && !owners.Contains(owner))
                                                owners.Add(owner);
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        results.Add(entry);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                results.Add(new Dictionary<string, object>
                {
                    { "note", "Failed to enumerate Harmony patches: " + ex.GetType().Name }
                });
            }
            return results;
        }

        #endregion
        
        #region Utility Methods
        
        private int ExtractInstanceId(MCPRequest request)
        {
            if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("instance_id", out var idProp) || j.TryGetProperty("InstanceId", out idProp))
                {
                    try
                    {
                        if (idProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string s;
                            int hid;
                            if (Handle.TryParse(idProp.GetString(), out s, out hid)) return hid;
                            return 0;
                        }
                        return idProp.GetInt32();
                    }
                    catch { return 0; }
                }
                string handleText = ExtractHandleText(request);
                if (handleText != null)
                {
                    string sess;
                    int hid;
                    if (Handle.TryParse(handleText, out sess, out hid)) return hid;
                }
            }
            return 0;
        }

        private string ExtractHandleText(MCPRequest request)
        {
            if (request.Params is System.Text.Json.JsonElement j)
            {
                System.Text.Json.JsonElement hp;
                if (j.TryGetProperty("handle", out hp) || j.TryGetProperty("Handle", out hp))
                {
                    try
                    {
                        if (hp.ValueKind == System.Text.Json.JsonValueKind.String)
                            return hp.GetString();
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// Session validation for write operations. Returns an error response
        /// when a session-bound handle belongs to an older session (fast fail,
        /// no object scan), otherwise null.
        /// </summary>
        private MCPResponse ValidateWriteHandle(MCPRequest request)
        {
            string handleText = ExtractHandleText(request);
            if (string.IsNullOrEmpty(handleText)) return null;
            StaleHandleError stale = Handle.Validate(handleText);
            if (stale == null) return null;
            return new MCPResponse { Success = false, Error = stale.Message, Data = stale.ToData() };
        }
        
        private string ExtractString(MCPRequest request, string propName)
        {
            if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty(propName, out var prop))
                    return prop.GetString();
            }
            return null;
        }
        
        private object ExtractValue(MCPRequest request, string propName)
        {
            if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty(propName, out var prop))
                {
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.String)
                        return prop.GetString();
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.Number)
                        return prop.GetDouble();
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.True)
                        return true;
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.False)
                        return false;
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return null;
                }
            }
            return null;
        }

#if CPP
        // ---- IL2CPP-native helpers (il2cpp_* P/Invoke + raw Marshal封送).
        // Mono backend does not compile this region; managed reflection
        // (ResolveType / MethodInfo.Invoke) covers the same operations.
        private string GetIl2CppTypeName(Component comp)
        {
            try
            {
                var il2cppClass = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                var namePtr = IL2CPP.il2cpp_class_get_name(il2cppClass);
                return Marshal.PtrToStringAnsi(namePtr);
            }
            catch { return comp.GetType().Name; }
        }
        
        private MCPResponse InvokeIl2CppMethod(Component comp, string methodName, object[] args)
        {
            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                IntPtr iter = IntPtr.Zero;
                IntPtr method = IntPtr.Zero;
                
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method));
                    if (name == methodName)
                    {
                        uint paramCount = IL2CPP.il2cpp_method_get_param_count(method);
                        if ((args == null && paramCount == 0) || (args != null && args.Length == paramCount))
                        {
                            unsafe
                            {
                                IntPtr exc = IntPtr.Zero;
                                void** paramsPtr = null;
                                
                                if (args != null && args.Length > 0)
                                {
                                    paramsPtr = (void**)Marshal.AllocHGlobal(args.Length * IntPtr.Size);
                                    for (int i = 0; i < args.Length; i++)
                                    {
                                        IntPtr paramType = IL2CPP.il2cpp_method_get_param(method, (uint)i);
                                        string paramTypeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(paramType));
                                        paramsPtr[i] = (void*)ConvertArgToIl2Cpp(args[i], paramTypeName);
                                    }
                                }
                                
                                IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, comp.Pointer, paramsPtr, ref exc);
                                
                                if (args != null && args.Length > 0)
                                {
                                    Marshal.FreeHGlobal((IntPtr)paramsPtr);
                                }
                                
                                if (exc != IntPtr.Zero)
                                {
                                    return new MCPResponse { Success = false, Error = "IL2CPP exception: " + ReadIl2CppException(exc) };
                                }

                                string returnType = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_return_type(method)));
                                object resultValue = FormatIl2CppResult(result, returnType, "hex");
                                string resultStr = resultValue != null ? resultValue.ToString() : "null";

                                return new MCPResponse { Success = true, Data = new { Method = methodName, Result = resultStr, ResultValue = resultValue, ReturnType = returnType } };
                            }
                        }
                    }
                }

                return new MCPResponse { Success = false, Error = "Method not found: " + methodName };
            }
            catch (Exception ex)
            {
                return new MCPResponse { Success = false, Error = "IL2CPP invoke error: " + ex.ToString() };
            }
        }

        /// <summary>
        /// Pure-Il2Cpp static call fallback (type invisible to managed reflection).
        /// Resolves the class via il2cpp_class_from_name across domain assemblies.
        /// </summary>
        private unsafe MCPResponse InvokeIl2CppStatic(string typeFullName, string methodName, MCPRequest request, string encoding)
        {
            try
            {
                int dot = typeFullName.LastIndexOf('.');
                string ns = dot > 0 ? typeFullName.Substring(0, dot) : "";
                string cls = dot > 0 ? typeFullName.Substring(dot + 1) : typeFullName;

                IntPtr klass = IntPtr.Zero;
                IntPtr domain = IL2CPP.il2cpp_domain_get();
                if (domain != IntPtr.Zero)
                {
                    uint size = 0;
                    IntPtr* assemblies = IL2CPP.il2cpp_domain_get_assemblies(domain, ref size);
                    for (uint i = 0; i < size && klass == IntPtr.Zero; i++)
                    {
                        try
                        {
                            IntPtr image = IL2CPP.il2cpp_assembly_get_image(assemblies[i]);
                            IntPtr found = IL2CPP.il2cpp_class_from_name(image, ns, cls);
                            if (found != IntPtr.Zero) klass = found;
                        }
                        catch { }
                    }
                }
                if (klass == IntPtr.Zero)
                    return new MCPResponse { Success = false, Error = "Il2Cpp class not found: " + typeFullName };

                object[] args = ParseLegacyArgs(request);
                IntPtr iter = IntPtr.Zero;
                IntPtr method = IntPtr.Zero;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method));
                    if (name != methodName) continue;
                    uint paramCount = IL2CPP.il2cpp_method_get_param_count(method);
                    int got = args != null ? args.Length : 0;
                    if ((uint)got != paramCount) continue;

                    IntPtr exc = IntPtr.Zero;
                    void** paramsPtr = null;
                    if (got > 0)
                    {
                        paramsPtr = (void**)Marshal.AllocHGlobal(got * IntPtr.Size);
                        for (int i = 0; i < got; i++)
                        {
                            IntPtr pt = IL2CPP.il2cpp_method_get_param(method, (uint)i);
                            paramsPtr[i] = (void*)ConvertArgToIl2Cpp(args[i], Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(pt)));
                        }
                    }
                    IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, paramsPtr, ref exc);
                    if (got > 0) Marshal.FreeHGlobal((IntPtr)paramsPtr);
                    if (exc != IntPtr.Zero)
                        return new MCPResponse { Success = false, Error = "IL2CPP exception: " + ReadIl2CppException(exc) };

                    string returnType = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_return_type(method)));
                    object resultValue = FormatIl2CppResult(result, returnType, encoding);
                    return new MCPResponse
                    {
                        Success = true,
                        Data = new
                        {
                            methodName = methodName,
                            returnType = returnType,
                            result = resultValue != null ? resultValue.ToString() : "null",
                            resultValue = resultValue,
                            outArgs = new object[0]
                        }
                    };
                }
                return new MCPResponse { Success = false, Error = "Il2Cpp static method not found: " + typeFullName + "." + methodName };
            }
            catch (Exception ex)
            {
                return new MCPResponse { Success = false, Error = "IL2CPP static invoke error: " + ex.ToString() };
            }
        }

        private static object[] ParseLegacyArgs(MCPRequest request)
        {
            if (!(request.Params is System.Text.Json.JsonElement j)) return null;
            if (!(j.TryGetProperty("args", out var argsProp) || j.TryGetProperty("Args", out argsProp))) return null;
            if (argsProp.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var list = new System.Collections.Generic.List<object>();
            foreach (var item in argsProp.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String: list.Add(item.GetString()); break;
                    case System.Text.Json.JsonValueKind.Number:
                        if (item.TryGetInt32(out int oi)) list.Add(oi);
                        else list.Add(item.GetDouble());
                        break;
                    case System.Text.Json.JsonValueKind.True: list.Add(true); break;
                    case System.Text.Json.JsonValueKind.False: list.Add(false); break;
                    default: list.Add(null); break;
                }
            }
            return list.ToArray();
        }

        private static string ReadIl2CppException(IntPtr exc)
        {
            try
            {
                var exObj = new Il2CppSystem.Exception(exc);
                return exObj.Message ?? exObj.ToString();
            }
            catch (Exception ex) { return "<unreadable Il2Cpp exception>; bridge error: " + ex.Message; }
        }

        private unsafe IntPtr ConvertArgToIl2Cpp(object value, string typeName)
        {
            string t = (typeName ?? "").ToLower();

            // Reference types: il2cpp_runtime_invoke takes the object pointer itself.
            if (t == "system.string" || t == "string")
            {
                try
                {
                    if (value == null) return IntPtr.Zero;
                    return IL2CPP.ManagedStringToIl2Cpp(value.ToString());
                }
                catch { return IntPtr.Zero; }
            }
            if (t == "system.byte[]" || t == "byte[]")
            {
                try
                {
                    byte[] bytes = value is string s ? ValueSerializer.DecodeBytes(s) : (byte[])value;
                    if (bytes == null) return IntPtr.Zero;
                    IntPtr corlib = IL2CPP.il2cpp_get_corlib();
                    IntPtr image = IL2CPP.il2cpp_assembly_get_image(corlib);
                    IntPtr byteClass = IL2CPP.il2cpp_class_from_name(image, "System", "Byte");
                    IntPtr arr = IL2CPP.il2cpp_array_new(byteClass, (nuint)bytes.Length);
                    // SzArray data starts after 32-byte header (16 obj + 8 bounds + 8 length).
                    Marshal.Copy(bytes, 0, arr + 32, bytes.Length);
                    return arr;
                }
                catch { return IntPtr.Zero; }
            }

            // Value types: pass pointer to the raw value.
            IntPtr ptr = Marshal.AllocHGlobal(16);
            try
            {
                switch (t)
                {
                    case "system.int32":
                    case "int":
                        Marshal.WriteInt32(ptr, Convert.ToInt32(value)); break;
                    case "system.int64":
                    case "long":
                        Marshal.WriteInt64(ptr, Convert.ToInt64(value)); break;
                    case "system.single":
                    case "float":
                        float f = Convert.ToSingle(value);
                        Marshal.WriteInt32(ptr, BitConverter.ToInt32(BitConverter.GetBytes(f), 0)); break;
                    case "system.double":
                        double d = Convert.ToDouble(value);
                        Marshal.WriteInt64(ptr, BitConverter.ToInt64(BitConverter.GetBytes(d), 0)); break;
                    case "system.boolean":
                    case "bool":
                        Marshal.WriteByte(ptr, Convert.ToBoolean(value) ? (byte)1 : (byte)0); break;
                    case "system.byte":
                        Marshal.WriteByte(ptr, Convert.ToByte(value)); break;
                    default:
                        Marshal.WriteIntPtr(ptr, IntPtr.Zero); break;
                }
            }
            catch { Marshal.WriteIntPtr(ptr, IntPtr.Zero); }
            return ptr;
        }

        /// <summary>P3 result marshalling for raw Il2Cpp pointers.</summary>
        private object FormatIl2CppResult(IntPtr result, string returnType, string encoding = "hex")
        {
            if (result == IntPtr.Zero) return null;
            string t = (returnType ?? "").ToLower();
            try
            {
                switch (t)
                {
                    case "system.void":
                    case "void":
                        return "void";
                    case "system.boolean":
                    case "bool":
                        return Marshal.ReadByte(IL2CPP.il2cpp_object_unbox(result)) != 0;
                    case "system.int32":
                    case "int":
                        return Marshal.ReadInt32(IL2CPP.il2cpp_object_unbox(result));
                    case "system.int64":
                    case "long":
                        return Marshal.ReadInt64(IL2CPP.il2cpp_object_unbox(result));
                    case "system.single":
                    case "float":
                        return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(IL2CPP.il2cpp_object_unbox(result))), 0);
                    case "system.double":
                        return BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(IL2CPP.il2cpp_object_unbox(result))), 0);
                    case "system.byte":
                        return Marshal.ReadByte(IL2CPP.il2cpp_object_unbox(result));
                    case "system.string":
                    case "string":
                        return ReadIl2CppString(result);
                    case "system.byte[]":
                        try
                        {
                            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(result);
                            int len = (int)arr.Length;
                            var bytes = new byte[len];
                            for (int i = 0; i < len; i++) bytes[i] = arr[i];
                            return ValueSerializer.EncodeBytes(bytes, encoding);
                        }
                        catch { return new Dictionary<string, object> { { "type", returnType }, { "pointer", result.ToString() } }; }
                    default:
                        return new Dictionary<string, object> { { "type", returnType }, { "pointer", result.ToString() } };
                }
            }
            catch { return new Dictionary<string, object> { { "type", returnType }, { "pointer", result.ToString() } }; }
        }

        private static string ReadIl2CppString(IntPtr strPtr)
        {
            if (strPtr == IntPtr.Zero) return null;
            try
            {
                // Il2CppString: 16-byte object header + int32 length + UTF-16 chars.
                int len = Marshal.ReadInt32(strPtr + 16);
                if (len < 0 || len > 1 << 20) return Marshal.PtrToStringUni(strPtr);
                return Marshal.PtrToStringUni(strPtr + 20, len);
            }
            catch { return Marshal.PtrToStringUni(strPtr); }
        }
#endif

        private UnityEngine.Object FindObjectById(int instanceId)
        {
            if (instanceId >= 1000000 && ObjectRegistry.TryGet(instanceId, out object reg) && reg is UnityEngine.Object uo)
                return uo;
            foreach (var obj in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
            {
                if (obj != null && obj.GetInstanceID() == instanceId)
                    return obj;
            }
            return null;
        }
        
        private string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            var parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.gameObject.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
        
        private object ConvertValue(object valueObj, Type targetType)
        {
            if (valueObj == null) return null;
            string valueStr = valueObj.ToString();
            
            if (targetType == typeof(string)) return valueStr;
            if (targetType == typeof(int)) return int.Parse(valueStr);
            if (targetType == typeof(float)) return float.Parse(valueStr);
            if (targetType == typeof(bool)) return bool.Parse(valueStr);
            if (targetType == typeof(double)) return double.Parse(valueStr);
            if (targetType == typeof(long)) return long.Parse(valueStr);
            if (targetType.IsEnum) return Enum.Parse(targetType, valueStr);
            return Convert.ChangeType(valueStr, targetType);
        }
        
        private object[] ConvertArgs(object[] args, ParameterInfo[] parameters)
        {
            if (args == null || args.Length == 0) return new object[0];
            int count = Math.Min(args.Length, parameters.Length);
            var result = new object[count];
            for (int i = 0; i < count; i++)
            {
                try
                {
                    result[i] = args[i] == null ? null : ConvertValue(args[i], parameters[i].ParameterType);
                }
                catch { result[i] = null; }
            }
            return result;
        }
        
        #endregion
    }
}


