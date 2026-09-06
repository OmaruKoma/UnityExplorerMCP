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
            var parameters = request.Params as FindGameObjectsParams;
            string name = parameters != null ? parameters.Name : null;
            bool includeInactive = parameters != null ? parameters.IncludeInactive : true;
            
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
            var parameters = request.Params as GetGameObjectParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            
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
            var parameters = request.Params as GetComponentsParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            
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
                    TypeName = comp.GetType().FullName,
                    InstanceId = comp.GetInstanceID(),
                    Enabled = enabled
                });
            }
            
            return new MCPResponse { Success = true, Data = components };
        }
        
        private MCPResponse HandleInspect(MCPRequest request)
        {
            var parameters = request.Params as InspectParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            
            var obj = FindObjectById(instanceId);
            if (obj == null)
                return new MCPResponse { Success = false, Error = "Object not found" };
            
            var type = obj.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            
            var info = new InspectResponse
            {
                TypeName = type.FullName,
                InstanceId = obj.GetInstanceID(),
                Fields = new List<MemberInfoItem>(),
                Properties = new List<MemberInfoItem>(),
                Methods = new List<MethodInfoItem>()
            };
            
            try
            {
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
                
                foreach (var prop in type.GetProperties(flags))
                {
                    try
                    {
                        object value = null;
                        if (prop.CanRead)
                        {
                            var getter = prop.GetGetMethod(true);
                            if (getter != null) value = prop.GetValue(getter.IsStatic ? null : obj, null);
                        }
                        info.Properties.Add(new MemberInfoItem
                        {
                            Name = prop.Name,
                            TypeName = prop.PropertyType.FullName,
                            Value = value != null ? value.ToString() : "null",
                            IsStatic = prop.GetAccessors(true)[0].IsStatic,
                            CanWrite = prop.CanWrite
                        });
                    }
                    catch { }
                }
                
                foreach (var method in type.GetMethods(flags))
                {
                    if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) continue;
                    if (method.DeclaringType != type) continue;
                    
                    var paramList = new List<ParameterInfoItem>();
                    foreach (var p in method.GetParameters())
                        paramList.Add(new ParameterInfoItem { Name = p.Name, TypeName = p.ParameterType.FullName });
                    
                    info.Methods.Add(new MethodInfoItem
                    {
                        Name = method.Name,
                        ReturnType = method.ReturnType.FullName,
                        Parameters = paramList,
                        IsStatic = method.IsStatic
                    });
                }
            }
            catch (Exception ex)
            {
                return new MCPResponse { Success = false, Error = "Inspection error: " + ex.Message };
            }
            
            return new MCPResponse { Success = true, Data = info };
        }
        
        private MCPResponse HandleGetField(MCPRequest request)
        {
            var parameters = request.Params as GetFieldParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            string fieldName = parameters != null ? parameters.Field : null;
            
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
            var parameters = request.Params as SetFieldParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            string fieldName = parameters != null ? parameters.Field : null;
            object valueObj = parameters != null ? parameters.Value : null;
            
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
            var parameters = request.Params as GetPropertyParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            string propertyName = parameters != null ? parameters.Property : null;
            
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
            var parameters = request.Params as SetPropertyParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            string propertyName = parameters != null ? parameters.Property : null;
            object valueObj = parameters != null ? parameters.Value : null;
            
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
            var parameters = request.Params as InvokeMethodParams;
            int instanceId = parameters != null ? parameters.InstanceId : 0;
            string typeName = parameters != null ? parameters.Type : null;
            string methodName = parameters != null ? parameters.Method : null;
            object[] args = parameters != null ? parameters.Args : null;
            
            UnityEngine.Object obj = null;
            if (instanceId != 0) obj = FindObjectById(instanceId);
            
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

