using System.Collections.Generic;
using UnityEngine;


namespace UnityExplorer.MCPBridge
{
    public class MCPRequest
    {
        public string Method { get; set; }
        public object Params { get; set; }
    }
    
    public class MCPResponse
    {
        public bool Success { get; set; }
        public object Data { get; set; }
        public string Error { get; set; }
    }
    
    public class MCPRequestWithContext
    {
        public MCPRequest Request { get; set; }
        public System.Net.HttpListenerContext Context { get; set; }
        public System.Threading.ManualResetEvent ResponseReady { get; set; }
    }
    
    // Request Parameters
    
    public class FindGameObjectsParams
    {
        public string Name { get; set; }
        public bool IncludeInactive { get; set; } = true;
    }
    
    public class GetGameObjectParams
    {
        public int InstanceId { get; set; }
    }
    
    public class GetComponentsParams
    {
        public int InstanceId { get; set; }
    }
    
    public class InspectParams
    {
        public int InstanceId { get; set; }
        public string TypeName { get; set; }
    }
    
    public class GetFieldParams
    {
        public int InstanceId { get; set; }
        public string Type { get; set; }
        public string Field { get; set; }
    }
    
    public class SetFieldParams
    {
        public int InstanceId { get; set; }
        public string Type { get; set; }
        public string Field { get; set; }
        public object Value { get; set; }
    }
    
    public class GetPropertyParams
    {
        public int InstanceId { get; set; }
        public string Type { get; set; }
        public string Property { get; set; }
    }
    
    public class SetPropertyParams
    {
        public int InstanceId { get; set; }
        public string Type { get; set; }
        public string Property { get; set; }
        public object Value { get; set; }
    }
    
    public class InvokeMethodParams
    {
        public int InstanceId { get; set; }
        public string Type { get; set; }
        public string Method { get; set; }
        public object[] Args { get; set; }
    }
    
    public class HierarchyParams
    {
        public int MaxDepth { get; set; } = 10;
    }
    
    public class ExecuteCSharpParams
    {
        public string Code { get; set; }
    }
    
    // Response Data Models
    
    public class PingResponse
    {
        public string Status { get; set; }
        public string UnityVersion { get; set; }
        public string BridgeVersion { get; set; }
    }
    
    public class SceneInfoResponse
    {
        public SceneInfo ActiveScene { get; set; }
        public List<SceneInfo> LoadedScenes { get; set; }
    }
    
    public class SceneInfo
    {
        public string Name { get; set; }
        public int BuildIndex { get; set; }
        public string Path { get; set; }
        public int RootCount { get; set; }
    }
    
    public class GameObjectInfo
    {
        public string Name { get; set; }
        public int InstanceId { get; set; }
        public string Scene { get; set; }
        public bool Active { get; set; }
        public string Path { get; set; }
        public string Tag { get; set; }
        public int Layer { get; set; }
    }
    
    public class GameObjectDetailInfo : GameObjectInfo
    {
        public Vector3Info Position { get; set; }
        public Vector3Info Rotation { get; set; }
        public Vector3Info Scale { get; set; }
        public GameObjectInfo Parent { get; set; }
        public List<GameObjectInfo> Children { get; set; }
    }
    
    public class Vector3Info
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        
        public Vector3Info() { }
        
        public Vector3Info(Vector3 v)
        {
            X = v.x;
            Y = v.y;
            Z = v.z;
        }
    }
    
    public class ComponentInfo
    {
        public string TypeName { get; set; }
        public int InstanceId { get; set; }
        public bool? Enabled { get; set; }
    }
    
    public class InspectResponse
    {
        public string TypeName { get; set; }
        public int InstanceId { get; set; }
        public List<MemberInfoItem> Fields { get; set; }
        public List<MemberInfoItem> Properties { get; set; }
        public List<MethodInfoItem> Methods { get; set; }
    }
    
    public class MemberInfoItem
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public string Value { get; set; }
        public bool IsStatic { get; set; }
        public bool CanWrite { get; set; }
    }
    
    public class MethodInfoItem
    {
        public string Name { get; set; }
        public string ReturnType { get; set; }
        public List<ParameterInfoItem> Parameters { get; set; }
        public bool IsStatic { get; set; }
    }
    
    public class ParameterInfoItem
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
    }
    
    public class FieldValueResponse
    {
        public string FieldName { get; set; }
        public string TypeName { get; set; }
        public string Value { get; set; }
    }
    
    public class PropertyValueResponse
    {
        public string PropertyName { get; set; }
        public string TypeName { get; set; }
        public string Value { get; set; }
    }
    
    public class MethodInvokeResponse
    {
        public string MethodName { get; set; }
        public string ReturnType { get; set; }
        public string Result { get; set; }
    }
    
    public class HierarchyInfo
    {
        public string Name { get; set; }
        public int InstanceId { get; set; }
        public bool Active { get; set; }
        public List<HierarchyInfo> Children { get; set; }
    }
}
