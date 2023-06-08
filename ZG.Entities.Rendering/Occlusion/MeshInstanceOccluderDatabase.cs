using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZG
{
    [CreateAssetMenu(fileName = "Mesh Instance Occluser Database", menuName = "ZG/Mesh Instance/Occluser Database")]
    public class MeshInstanceOccluderDatabase : MeshInstanceDatabase<MeshInstanceOccluderDatabase>, ISerializationCallbackReceiver
    {
        public const int VERSION = 0;

        [SerializeField, HideInInspector]
        private byte[] __bytes;

        [SerializeField, HideInInspector]
        private int __assetCount;

        private bool __isInit;

        private MeshInstanceOcclusionMeshAsset[] __assets;

        private BlobAssetReference<MeshInstanceOccluderDefinition> __definition;

        public override int instanceID => __definition.IsCreated ? __definition.Value.instanceID : 0;

        public BlobAssetReference<MeshInstanceOccluderDefinition> definition
        {
            get
            {
                Init();

                return __definition;
            }
        }

        protected override void _Dispose()
        {
            if (__definition.IsCreated)
            {
                __definition.Dispose();

                __definition = BlobAssetReference<MeshInstanceOccluderDefinition>.Null;
            }
        }

        protected override void _Destroy()
        {
            if(__isInit)
            {
                var instance = SingletonAssetContainer<MeshInstanceOcclusionMeshAsset>.instance;

                SingletonAssetContainerHandle handle;
                handle.instanceID = __definition.Value.instanceID;
                for (int i = 0; i < __assetCount; ++i)
                {
                    handle.index = i;

                    instance.Delete(handle);
                }

                __isInit = false;
            }
        }

        protected override void _Init()
        {
            if (__isInit)
                return;

            __isInit = true;

            var instance = SingletonAssetContainer<MeshInstanceOcclusionMeshAsset>.instance;

            SingletonAssetContainerHandle handle;
            handle.instanceID = __definition.Value.instanceID;

            for (int i = 0; i < __assetCount; ++i)
            {
                handle.index = i;

                instance[handle] = __assets[i];
            }
        }

        private static MeshInstanceOcclusionMeshAsset __ReadAsset(MemoryBinaryReader reader)
        {
            MeshInstanceOcclusionMeshAsset result;
            result.vertexData = reader.Read<float4>();
            result.indexData = reader.Read<int>();
            result.vertexCount = reader.ReadInt();
            result.indexCount = reader.ReadInt();

            return result;
        }

        private static void __WriteAsset(MemoryBinaryWriter writer, in MeshInstanceOcclusionMeshAsset asset)
        {
            writer.Write(asset.vertexData);
            writer.Write(asset.indexData);
            writer.Write(asset.vertexCount);
            writer.Write(asset.indexCount);
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (__bytes != null && __bytes.Length > 0)
            {
                if (__definition.IsCreated)
                    __definition.Dispose();

                if (__assets != null)
                {
                    foreach (var asset in __assets)
                        asset.Dispose();
                }

                unsafe
                {
                    fixed (byte* ptr = __bytes)
                    {
                        using (var reader = new MemoryBinaryReader(ptr, __bytes.Length))
                        {
                            int version = reader.ReadInt();

                            UnityEngine.Assertions.Assert.AreEqual(VERSION, version);

                            __definition = reader.Read<MeshInstanceOccluderDefinition>();

                            __assets = new MeshInstanceOcclusionMeshAsset[__assetCount];
                            for (int i = 0; i < __assetCount; ++i)
                                __assets[i] = __ReadAsset(reader);
                        }
                    }
                }

                __bytes = null;
            }

            __isInit = false;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (__definition.IsCreated)
            {
                using (var writer = new MemoryBinaryWriter())
                {
                    writer.Write(VERSION);
                    writer.Write(__definition);

                    __assetCount = __assets == null ? 0 : __assets.Length;
                    for (int i = 0; i < __assetCount; ++i)
                        __WriteAsset(writer, __assets[i]);

                    __bytes = writer.GetContentAsNativeArray().ToArray();
                }
            }
        }

#if UNITY_EDITOR
        [Serializable]
        public struct Data
        {
            [Serializable]
            public struct Mesh
            {
                public Vector3[] vertices;
                public int[] indices;
            }

            [Serializable]
            public struct Occluder
            {
                public int meshIndex;
                public int rendererIndex;
                public Matrix4x4 matrix;
            }

            [Serializable]
            public struct OccluderProxy
            {
                public int meshIndex;
                public Matrix4x4 matrix;
            }

            public Mesh[] meshes;
            public Occluder[] occluders;
            public OccluderProxy[] occluderProxies;

#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
            public void Create(
                Unity.Rendering.Occlusion.Occluder[] origins,
                IDictionary<Renderer, LODGroup[]> rendererLODGroups,
                IDictionary<Renderer, int> rendererStartIndices)
            {
                var occluderProxies = new List<OccluderProxy>();
                var occluders = new List<Occluder>();
                var meshes = new List<Mesh>();
                var meshIndices = new Dictionary<UnityEngine.Mesh, int>();
                foreach (var origin in origins)
                {
                    __Apply(
                        origin,
                        origin.GetComponent<Renderer>(),
                        occluderProxies,
                        occluders,
                        meshes,
                        meshIndices,
                        rendererLODGroups,
                        rendererStartIndices);
                }

                this.meshes = meshes.Count > 0 ? meshes.ToArray() : null;
                this.occluders = occluders.ToArray();
                this.occluderProxies = occluderProxies.ToArray();
            }
#endif

            public void Create(Transform root)
            {
                var rendererLODGroups = new Dictionary<Renderer, LODGroup[]>();
                MeshInstanceRendererDatabase.Build(root.GetComponentsInChildren<LODGroup>(), rendererLODGroups);

                var rendererStartIndices = new Dictionary<Renderer, int>();
                MeshInstanceRendererDatabase.Build(root.GetComponentsInChildren<Renderer>(), rendererLODGroups, rendererStartIndices);

#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
                Create(root.GetComponentsInChildren<Unity.Rendering.Occlusion.Occluder>(), rendererLODGroups, rendererStartIndices);
#endif
            }

            public unsafe MeshInstanceOcclusionMeshAsset[] ToAssets()
            {
                int i, j, numMeshes = meshes == null ? 0 : meshes.Length;
                float4[] vertices;
                var assets = new MeshInstanceOcclusionMeshAsset[numMeshes];
                for(i = 0; i < numMeshes; ++i)
                {
                    ref readonly var mesh = ref meshes[i];
                    ref var asset = ref assets[i];

                    asset.vertexCount = mesh.vertices == null ? 0 : mesh.vertices.Length;
                    if (asset.vertexCount > 0)
                    {
                        vertices = new float4[asset.vertexCount];
                        for(j = 0; j < asset.vertexCount; ++j)
                        {
                            vertices[j] = math.float4(mesh.vertices[j], 1.0f);
                        }

                        fixed (void* ptr = vertices)
                        {
                            asset.vertexData = BlobAssetReference<float4>.Create(ptr, UnsafeUtility.SizeOf<float4>() * asset.vertexCount);
                        }
                    }
                    else
                        asset.vertexData = BlobAssetReference<float4>.Null;

                    asset.indexCount = mesh.indices == null ? 0 : mesh.indices.Length;
                    if (asset.indexCount > 0)
                    {
                        fixed (void* ptr = mesh.indices)
                        {
                            asset.indexData = BlobAssetReference<int>.Create(ptr, UnsafeUtility.SizeOf<int>() * asset.indexCount);
                        }
                    }
                    else
                        asset.indexData = BlobAssetReference<int>.Null;
                }

                return assets;
            }

            public BlobAssetReference<MeshInstanceOccluderDefinition> ToAsset(int instanceID)
            {
                using(var blobBuilder = new BlobBuilder(Allocator.Temp))
                {
                    ref var root = ref blobBuilder.ConstructRoot<MeshInstanceOccluderDefinition>();

                    root.instanceID = instanceID;

                    int numOccluders = this.occluders == null ? 0 : this.occluders.Length;
                    var occluders = blobBuilder.Allocate(ref root.occluders, numOccluders);
                    for(int i = 0; i < numOccluders; ++i)
                    {
                        ref readonly var source = ref this.occluders[i];
                        ref var destination = ref occluders[i];

                        destination.meshIndex = source.meshIndex;
                        destination.rendererIndex = source.rendererIndex;
                        destination.matrix = source.matrix;
                    }

                    int numOccluderProxies = this.occluderProxies == null ? 0 : this.occluderProxies.Length;
                    var occluderProxies = blobBuilder.Allocate(ref root.occluderProxies, numOccluderProxies);
                    for (int i = 0; i < numOccluderProxies; ++i)
                    {
                        ref readonly var source = ref this.occluderProxies[i];
                        ref var destination = ref occluderProxies[i];

                        destination.meshIndex = source.meshIndex;
                        destination.matrix = source.matrix;
                    }

                    return blobBuilder.CreateBlobAssetReference<MeshInstanceOccluderDefinition>(Allocator.Persistent);
                }
            }

#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
            private static void __Apply(
                Unity.Rendering.Occlusion.Occluder origin, 
                Renderer renderer,
                List<OccluderProxy> occluderProxies,
                List<Occluder> occluders, 
                List<Mesh> meshes,
                Dictionary<UnityEngine.Mesh, int> meshIndices,
                IDictionary<Renderer, LODGroup[]> rendererLODGroups,
                IDictionary<Renderer, int> rendererStartIndices)
            {
                if (renderer == null || rendererStartIndices == null || !rendererStartIndices.TryGetValue(renderer, out int rendererStartIndex))
                {
                    var lodGroup = origin.GetComponent<LODGroup>();
                    if (lodGroup == null)
                    {
                        OccluderProxy occluderProxy;
                        occluderProxy.meshIndex = __Apply(origin.Mesh, meshes, meshIndices);
                        occluderProxy.matrix = origin.localTransform;
                        occluderProxies.Add(occluderProxy);
                    }
                    else
                    {
                        var lods = lodGroup.GetLODs();
                        LOD lod;
                        MeshFilter meshFilter;
                        int numLODs = lods == null ? 0 : lods.Length;
                        for (int i = numLODs - 1; i >= 0; --i)
                        {
                            lod = lods[i];
                            if (lod.renderers == null)
                                continue;

                            foreach (var lodRenderer in lod.renderers)
                            {
                                if(origin.Mesh == null)
                                {
                                    meshFilter = lodRenderer.GetComponent<MeshFilter>();
                                    origin.Mesh = meshFilter == null ? null : meshFilter.sharedMesh;
                                }

                                __Apply(
                                    origin,
                                    lodRenderer,
                                    occluderProxies,
                                    occluders,
                                    meshes,
                                    meshIndices,
                                    rendererLODGroups,
                                    rendererStartIndices);
                            }
                        }
                    }
                }
                else
                {
                    int numLODGroups;
                    if (renderer != null &&
                        rendererLODGroups != null &&
                        rendererLODGroups.TryGetValue(renderer, out var lodGroups))
                    {
                        numLODGroups = lodGroups == null ? 0 : lodGroups.Length;
                        if (numLODGroups > 0)
                        {
                            var lodGroup = origin.GetComponent<LODGroup>();
                            if (lodGroup != null)
                            {
                                int lodGroupIndex = Array.IndexOf(lodGroups, lodGroup);
                                if (lodGroupIndex != -1)
                                    rendererStartIndex += lodGroupIndex;
                            }
                        }
                    }
                    else
                        numLODGroups = 0;

                    Occluder occluder;
                    Matrix4x4 matrix = renderer == null ? origin.localTransform : renderer.worldToLocalMatrix * origin.localTransform;
                    var sharedMaterials = renderer == null ? null : renderer.sharedMaterials;
                    int numSharedMaterials = sharedMaterials == null ? 0 : sharedMaterials.Length, rendererIndexStep = Mathf.Max(numLODGroups, 1);
                    for (int i = 0; i < numSharedMaterials; ++i)
                    {
                        occluder.meshIndex = __Apply(origin.Mesh, meshes, meshIndices);
                        occluder.rendererIndex = rendererStartIndex;
                        occluder.matrix = matrix;
                        occluders.Add(occluder);

                        rendererStartIndex += rendererIndexStep;
                    }
                }
            }

            private static int __Apply(
                UnityEngine.Mesh input, 
                List<Mesh> outputs, 
                Dictionary<UnityEngine.Mesh, int> meshIndices)
            {
                if (!meshIndices.TryGetValue(input, out int meshIndex))
                {
                    meshIndex = outputs.Count;

                    meshIndices[input] = meshIndex;

                    Mesh output;
                    output.vertices = input.vertices;
                    output.indices = input.triangles;
                    outputs.Add(output);
                }

                return meshIndex;
            }
#endif
        }

        [HideInInspector]
        public Transform root;

        public Data data;

        public void Create()
        {
            data.Create(root);
        }

        public void Rebuild()
        {
            if (__definition.IsCreated)
                __definition.Dispose();

            __definition = data.ToAsset(GetInstanceID());

            __assets = data.ToAssets();

            __assetCount = __assets == null ? 0 : __assets.Length;

            __bytes = null;

            ((ISerializationCallbackReceiver)this).OnBeforeSerialize();
        }

        public void EditorMaskDirty()
        {
            Rebuild();

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}