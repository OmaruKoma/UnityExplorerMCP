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
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime;
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
        
        private MCPResponse HandlePing(MCPRequest request)
        {
            return new MCPResponse
            {
                Success = true,
                Data = new PingResponse
                {
                    Status = "ok",
                    UnityVersion = Application.unityVersion,
                    BridgeVersion = "1.0.0"
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
                    LoadedScenes = loadedScenes
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
            
            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                try
                {
                    if (obj.transform != null && obj.transform.root != null && 
                        obj.transform.root.name == "UniverseLibCanvas") continue;
                    
                    if (!string.IsNullOrEmpty(name) && !obj.name.Contains(name)) continue;
                    
                    if (!includeInactive && !obj.activeInHierarchy) continue;
                    
                    results.Add(new GameObjectInfo
                    {
                        Name = obj.name,
                        InstanceId = obj.GetInstanceID(),
                        Scene = obj.scene.name,
                        Active = obj.activeInHierarchy,
                        Path = GetGameObjectPath(obj),
                        Tag = obj.tag,
                        Layer = obj.layer
                    });
                }
                catch { }
            }
            
            return new MCPResponse { Success = true, Data = results };
        }
        
        private MCPResponse HandleGetGameObject(MCPRequest request)
        {
            int instanceId = 0;
            if (request.Params is GetGameObjectParams p)
            {
                instanceId = p.InstanceId;
            }
            else if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("instance_id", out var idProp) || j.TryGetProperty("InstanceId", out idProp))
                    instanceId = idProp.GetInt32();
            }
            
            var obj = FindObjectById(instanceId) as GameObject;
            if (obj == null)
                return new MCPResponse { Success = false, Error = "GameObject not found: " + instanceId };
            
            var info = new GameObjectDetailInfo
            {
                Name = obj.name,
                InstanceId = obj.GetInstanceID(),
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
                info.Parent = new GameObjectInfo
                {
                    Name = obj.transform.parent.gameObject.name,
                    InstanceId = obj.transform.parent.gameObject.GetInstanceID()
                };
            }
            
            for (int i = 0; i < obj.transform.childCount; i++)
            {
                var child = obj.transform.GetChild(i).gameObject;
                info.Children.Add(new GameObjectInfo
                {
                    Name = child.name,
                    InstanceId = child.GetInstanceID(),
                    Active = child.activeInHierarchy
                });
            }
            
            return new MCPResponse { Success = true, Data = info };
        }
        
        private MCPResponse HandleGetComponents(MCPRequest request)
        {
            int instanceId = 0;
            if (request.Params is GetComponentsParams p)
            {
                instanceId = p.InstanceId;
            }
            else if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("instance_id", out var idProp) || j.TryGetProperty("InstanceId", out idProp))
                    instanceId = idProp.GetInt32();
            }
            
            var obj = FindObjectById(instanceId) as GameObject;
            if (obj == null)
                return new MCPResponse { Success = false, Error = "GameObject not found: " + instanceId };
            
            var components = new List<ComponentInfo>();
            foreach (var comp in obj.GetComponents<Component>())
            {
                if (comp == null) continue;
                bool? enabled = null;
                if (comp is Behaviour) enabled = ((Behaviour)comp).enabled;
components.Add(new ComponentInfo
                {
                    TypeName = GetIl2CppTypeName(comp),
                    InstanceId = comp.GetInstanceID(),
                    Enabled = enabled
                });
            }
            
            return new MCPResponse { Success = true, Data = components };
        }
        
        private MCPResponse HandleInspect(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = "Object not found" };
            
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
                else
                {
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
            
            return new MCPResponse { Success = true, Data = info };
        }
        
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
        private MCPResponse HandleGetField(MCPRequest request)
        {
            int instanceId = ExtractInstanceId(request);
            string fieldName = ExtractString(request, "field") ?? ExtractString(request, "Field");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = "Object not found: " + instanceId };
            
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
            int instanceId = ExtractInstanceId(request);
            string fieldName = ExtractString(request, "field") ?? ExtractString(request, "Field");
            object valueObj = ExtractValue(request, "value") ?? ExtractValue(request, "Value");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = "Object not found: " + instanceId };
            
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
                return new MCPResponse { Success = false, Error = "Object not found: " + instanceId };
            
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
            int instanceId = ExtractInstanceId(request);
            string propertyName = ExtractString(request, "property") ?? ExtractString(request, "Property");
            object valueObj = ExtractValue(request, "value") ?? ExtractValue(request, "Value");
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = "Object not found: " + instanceId };
            
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
            
            if (obj is Component comp)
            {
                return InvokeIl2CppMethod(comp, methodName, args);
            }
            
            Type type = null;
            if (obj != null) type = obj.GetType();
            else if (!string.IsNullOrEmpty(typeName))
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(typeName);
                    if (type != null) break;
                }
            }
            
            if (type == null)
                return new MCPResponse { Success = false, Error = "Type not found: " + typeName };
            
            MethodInfo method = null;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name == methodName) { method = m; break; }
            }
            
            if (method == null)
                return new MCPResponse { Success = false, Error = "Method not found: " + methodName };
            
            try
            {
                var methodArgs = ConvertArgs(args, method.GetParameters());
                object result = method.Invoke(method.IsStatic ? null : obj, methodArgs);
                return new MCPResponse { Success = true, Data = new MethodInvokeResponse { MethodName = method.Name, ReturnType = method.ReturnType.FullName, Result = result != null ? result.ToString() : "null" } };
            }
            catch (Exception ex) { return new MCPResponse { Success = false, Error = ex.Message }; }
        }
        
        private MCPResponse HandleHierarchy(MCPRequest request)
        {
            var parameters = request.Params as HierarchyParams;
            int maxDepth = parameters != null ? parameters.MaxDepth : 10;
            
            var rootObjects = new List<HierarchyInfo>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    rootObjects.Add(BuildHierarchy(root, 0, maxDepth));
            }
            
            return new MCPResponse { Success = true, Data = rootObjects };
        }
        
        private HierarchyInfo BuildHierarchy(GameObject obj, int currentDepth, int maxDepth)
        {
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
                    info.Children.Add(BuildHierarchy(obj.transform.GetChild(i).gameObject, currentDepth + 1, maxDepth));
            }
            
            return info;
        }
        
        private MCPResponse HandleExecuteCSharp(MCPRequest request)
        {
            return new MCPResponse { Success = true, Data = new { Output = "C# execution requires UnityExplorer C# Console" } };
        }
        
        #endregion
        
        #region Utility Methods
        
        private int ExtractInstanceId(MCPRequest request)
        {
            if (request.Params is System.Text.Json.JsonElement j)
            {
                if (j.TryGetProperty("instance_id", out var idProp) || j.TryGetProperty("InstanceId", out idProp))
                    return idProp.GetInt32();
            }
            return 0;
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
                                    return new MCPResponse { Success = false, Error = "IL2CPP Exception occurred" };
                                }
                                
                                string returnType = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_return_type(method)));
                                string resultStr = FormatIl2CppResult(result, returnType);
                                
                                return new MCPResponse { Success = true, Data = new { Method = methodName, Result = resultStr } };
                            }
                        }
                    }
                }
                
                return new MCPResponse { Success = false, Error = "Method not found: " + methodName };
            }
            catch (Exception ex)
            {
                return new MCPResponse { Success = false, Error = "IL2CPP invoke error: " + ex.Message };
            }
        }
        
        private unsafe IntPtr ConvertArgToIl2Cpp(object value, string typeName)
        {
            IntPtr ptr = Marshal.AllocHGlobal(8);
            
            switch (typeName.ToLower())
            {
                case "system.int32":
                case "int":
                    Marshal.WriteInt32(ptr, Convert.ToInt32(value));
                    break;
                case "system.single":
                case "float":
                    float f = Convert.ToSingle(value);
                    Marshal.WriteInt32(ptr, BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
                    break;
                case "system.boolean":
                case "bool":
                    Marshal.WriteByte(ptr, Convert.ToBoolean(value) ? (byte)1 : (byte)0);
                    break;
                case "system.double":
                    double d = Convert.ToDouble(value);
                    Marshal.WriteInt64(ptr, BitConverter.ToInt64(BitConverter.GetBytes(d), 0));
                    break;
                case "system.string":
                    IntPtr strPtr = Marshal.StringToHGlobalAnsi(value?.ToString());
                    Marshal.WriteIntPtr(ptr, strPtr);
                    break;
                default:
                    Marshal.WriteInt32(ptr, 0);
                    break;
            }
            
            return ptr;
        }
        
        private string FormatIl2CppResult(IntPtr result, string returnType)
        {
            if (result == IntPtr.Zero) return "null";
            
            switch (returnType.ToLower())
            {
                case "system.void":
                    return "void";
                case "system.int32":
                case "int":
                    return Marshal.ReadInt32(result).ToString();
                case "system.single":
                case "float":
                    return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(result)), 0).ToString();
                case "system.boolean":
                case "bool":
                    return (Marshal.ReadByte(result) != 0).ToString();
                case "system.string":
                    IntPtr strPtr = Marshal.ReadIntPtr(result);
                    if (strPtr == IntPtr.Zero) return "null";
                    return Marshal.PtrToStringAnsi(strPtr);
                default:
                    return $"[Object @ {result}]";
            }
        }
        
        private UnityEngine.Object FindObjectById(int instanceId)
        {
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


