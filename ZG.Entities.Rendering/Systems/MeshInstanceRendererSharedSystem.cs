using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZG
{
    public struct MeshInstanceMaterialAsset : IEquatable<MeshInstanceMaterialAsset>
    {
        public int instanceID;

        public bool Equals(MeshInstanceMaterialAsset other)
        {
            return instanceID.Equals(other.instanceID);
        }

        public override int GetHashCode()
        {
            return instanceID;
        }

        public override string ToString()
        {
            return $"MeshInstanceMaterialAsset({instanceID})";
        }
    }

    public struct MeshInstanceMeshAsset : IEquatable<MeshInstanceMeshAsset>
    {
        public int instanceID;

        public bool Equals(MeshInstanceMeshAsset other)
        {
            return instanceID.Equals(other.instanceID);
        }

        public override int GetHashCode()
        {
            return instanceID;
        }

        public override string ToString()
        {
            return $"MeshInstanceMeshAsset({instanceID})";
        }
    }

    public struct MeshInstanceRendererMaterialModifier : IBufferElementData
    {
        public MeshInstanceMaterialAsset source;
        public MeshInstanceMaterialAsset destination;
    }

    public struct MeshInstanceRendererMeshModifier : IBufferElementData
    {
        public MeshInstanceMeshAsset source;
        public MeshInstanceMeshAsset destination;
    }

    public struct MeshInstanceRendererSharedData : IComponentData
    {
        public SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID> batchMaterialIDs;

        public SharedHashMap<MeshInstanceMeshAsset, BatchMeshID> batchMeshIDs;
    }

    public static class MeshInstanceRendererSharedUtility
    {
        public struct Comparer<T> : IEqualityComparer<T> where T : UnityEngine.Object
        {
            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        public static event Action<Material, MeshInstanceMaterialAsset> onMaterialRegistered;
        public static event Action<Mesh, MeshInstanceMeshAsset> onMeshRegistered;
        public static event Action<MeshInstanceMaterialAsset> onMaterialUnregistered;
        public static event Action<MeshInstanceMeshAsset> onMeshUnregistered;

        private static Assets<Material> __materials;
        private static Assets<Mesh> __meshes;

        private static Dictionary<int, AssetHandle> __materialAssetHandles;
        private static Dictionary<int, AssetHandle> __meshAssetHandles;

        public static IEnumerable<Material> materials => __materials;

        public static IEnumerable<Mesh> meshes => __meshes;

        public static bool TryGetMaterialAsset(Material material, out MeshInstanceMaterialAsset asset)
        {
            if (__materials.Contains(material))
            {
                asset.instanceID = material.GetInstanceID();

                return true;
            }

            asset.instanceID = 0;

            return false;
        }

        public static bool TryGetMeshAsset(Mesh mesh, out MeshInstanceMeshAsset asset)
        {
            if(__meshes.Contains(mesh))
            {
                asset.instanceID = mesh.GetInstanceID();

                return true;
            }

            asset.instanceID = 0;

            return false;
        }

        public static MeshInstanceMaterialAsset RegisterMaterial(Material material)
        {
            if (__materials == null)
            {
                Comparer<Material> comparer;
                __materials = new Assets<Material>(comparer);
            }

            var assetHandle = __materials.Add(material, out int count);

            MeshInstanceMaterialAsset materialAsset;
            materialAsset.instanceID = material.GetInstanceID();
            if (count == 1)
            {
                if (__materialAssetHandles == null)
                    __materialAssetHandles = new Dictionary<int, AssetHandle>();

                __materialAssetHandles.Add(materialAsset.instanceID, assetHandle);

                if (onMaterialRegistered != null)
                    onMaterialRegistered(material, materialAsset);
            }

            return materialAsset;
        }

        public static int UnregisterMaterial(in MeshInstanceMaterialAsset asset)
        {
            if (!__materialAssetHandles.TryGetValue(asset.instanceID, out var assetHandle))
                return 0;

            int count = __materials.Remove(assetHandle);
            if (count == 0)
            {
                __materialAssetHandles.Remove(asset.instanceID);

                if (onMaterialUnregistered != null)
                    onMaterialUnregistered(asset);
            }

            return count;
        }

        public static MeshInstanceMeshAsset RegisterMesh(Mesh mesh)
        {
            if (__meshes == null)
            {
                Comparer<Mesh> comparer;
                __meshes = new Assets<Mesh>(comparer);
            }

            var assetHandle = __meshes.Add(mesh, out int count);

            MeshInstanceMeshAsset meshAsset;
            meshAsset.instanceID = mesh.GetInstanceID();
            if (count == 1)
            {
                if (__meshAssetHandles == null)
                    __meshAssetHandles = new Dictionary<int, AssetHandle>();

                __meshAssetHandles.Add(meshAsset.instanceID, assetHandle);

                if (onMeshRegistered != null)
                    onMeshRegistered(mesh, meshAsset);
            }

            return meshAsset;
        }

        public static int UnregisterMesh(MeshInstanceMeshAsset asset)
        {
            if (!__meshAssetHandles.TryGetValue(asset.instanceID, out var assetHandle))
                return 0;

            int count = __meshes.Remove(assetHandle);
            if (count == 0)
            {
                __meshAssetHandles.Remove(asset.instanceID);

                if (onMeshUnregistered != null)
                    onMeshUnregistered(asset);
            }

            return count;
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup)), UpdateBefore(typeof(StructuralChangePresentationSystemGroup))]
    //[DisableAutoCreation]
    public partial class MeshInstanceRendererSharedSystem : SystemBase
    {
        private EntitiesGraphicsSystem __graphicsSystem;

        public EntitiesGraphicsSystem graphicsSystem
        {
            get
            {
                if (__graphicsSystem == null)
                    __graphicsSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();

                return __graphicsSystem;
            }
        }

        public readonly SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID> batchMaterialIDs;

        public readonly SharedHashMap<MeshInstanceMeshAsset, BatchMeshID> batchMeshIDs;

        private NativeHashMap<MeshInstanceMaterialAsset, BatchMaterialID> __batchMaterialIDsToRemove;

        private NativeHashMap<MeshInstanceMeshAsset, BatchMeshID> __batchMeshIDsToRemove;

        public MeshInstanceRendererSharedSystem()
        {
            batchMaterialIDs = new SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID>(Allocator.Persistent);
            batchMeshIDs = new SharedHashMap<MeshInstanceMeshAsset, BatchMeshID>(Allocator.Persistent);

            __batchMaterialIDsToRemove = new NativeHashMap<MeshInstanceMaterialAsset, BatchMaterialID>(1, Allocator.Persistent);
            __batchMeshIDsToRemove = new NativeHashMap<MeshInstanceMeshAsset, BatchMeshID>(1, Allocator.Persistent);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            MeshInstanceRendererSharedData instance;
            instance.batchMaterialIDs = batchMaterialIDs;
            instance.batchMeshIDs = batchMeshIDs;
            EntityManager.AddComponentData(SystemHandle, instance);

            var materials = MeshInstanceRendererSharedUtility.materials;
            if (materials != null)
            {
                MeshInstanceMaterialAsset materialAsset;
                foreach (var material in materials)
                {
                    if (MeshInstanceRendererSharedUtility.TryGetMaterialAsset(material, out materialAsset))
                        __OnMaterialRegistered(material, materialAsset);
                }
            }

            var meshes = MeshInstanceRendererSharedUtility.meshes;
            if (meshes != null)
            {
                MeshInstanceMeshAsset meshAsset;
                foreach (var mesh in meshes)
                {
                    if (MeshInstanceRendererSharedUtility.TryGetMeshAsset(mesh, out meshAsset))
                        __OnMeshRegistered(mesh, meshAsset);
                }
            }

            MeshInstanceRendererSharedUtility.onMaterialRegistered += __OnMaterialRegistered;
            MeshInstanceRendererSharedUtility.onMeshRegistered += __OnMeshRegistered;
            MeshInstanceRendererSharedUtility.onMaterialUnregistered += __OnMaterialUnregistered;
            MeshInstanceRendererSharedUtility.onMeshUnregistered += __OnMeshUnregistered;
        }

        protected override void OnDestroy()
        {
            MeshInstanceRendererSharedUtility.onMaterialRegistered -= __OnMaterialRegistered;
            MeshInstanceRendererSharedUtility.onMeshRegistered -= __OnMeshRegistered;
            MeshInstanceRendererSharedUtility.onMaterialUnregistered -= __OnMaterialUnregistered;
            MeshInstanceRendererSharedUtility.onMeshUnregistered -= __OnMeshUnregistered;

            if (__graphicsSystem != null && __graphicsSystem.Enabled)
            {
                foreach (var batchMaterialID in batchMaterialIDs)
                    __graphicsSystem.UnregisterMaterial(batchMaterialID.Value);

                foreach (var batchMeshID in batchMeshIDs)
                    __graphicsSystem.UnregisterMesh(batchMeshID.Value);

                foreach (var batchMaterialID in __batchMaterialIDsToRemove)
                    __graphicsSystem.UnregisterMaterial(batchMaterialID.Value);

                foreach (var batchMeshID in __batchMeshIDsToRemove)
                    __graphicsSystem.UnregisterMesh(batchMeshID.Value);
            }

            batchMaterialIDs.Dispose();
            batchMeshIDs.Dispose();

            __batchMaterialIDsToRemove.Dispose();
            __batchMeshIDsToRemove.Dispose();

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            /*if (__graphicsSystem == null)
                return;

            foreach (var batchMaterialIDToRemove in __batchMaterialIDsToRemove)
                __graphicsSystem.UnregisterMaterial(batchMaterialIDToRemove.Value);

            __batchMaterialIDsToRemove.Clear();

            foreach (var batchMeshIDToRemove in __batchMeshIDsToRemove)
                __graphicsSystem.UnregisterMesh(batchMeshIDToRemove.Value);

            __batchMeshIDsToRemove.Clear();*/
        }

        private void __OnMaterialRegistered(Material material, MeshInstanceMaterialAsset asset)
        {
            if (!batchMaterialIDs.isCreated)
                return;

            var graphicsSystem = this.graphicsSystem;
            if (graphicsSystem == null)
                return;

            if (__batchMaterialIDsToRemove.TryGetValue(asset, out var batchMaterialID))
                __batchMaterialIDsToRemove.Remove(asset);
            else
                batchMaterialID = graphicsSystem.RegisterMaterial(material);
            
            batchMaterialIDs.lookupJobManager.CompleteReadWriteDependency();

            batchMaterialIDs.writer.Add(asset, batchMaterialID);

            //Debug.Log($"Material Registered : {material} : {asset.instanceID}");
        }

        private void __OnMaterialUnregistered(MeshInstanceMaterialAsset asset)
        {
            if (!batchMaterialIDs.isCreated)
                return;

            var graphicsSystem = this.graphicsSystem;
            if (graphicsSystem == null)
                return;

            //Debug.Log($"Material Unregistered : {asset.instanceID}");

            batchMaterialIDs.lookupJobManager.CompleteReadWriteDependency();

            var writer = batchMaterialIDs.writer;

            __batchMaterialIDsToRemove.Add(asset, writer[asset]);

            //graphicsSystem.UnregisterMaterial(writer[asset]);

            writer.Remove(asset);
        }

        private void __OnMeshRegistered(Mesh mesh, MeshInstanceMeshAsset asset)
        {
            if (!batchMeshIDs.isCreated)
                return;

            var graphicsSystem = this.graphicsSystem;
            if (graphicsSystem == null)
                return;

            if (__batchMeshIDsToRemove.TryGetValue(asset, out var batchMeshID))
                __batchMeshIDsToRemove.Remove(asset);
            else
                batchMeshID = graphicsSystem.RegisterMesh(mesh);

            batchMeshIDs.lookupJobManager.CompleteReadWriteDependency();

            batchMeshIDs.writer.Add(asset, batchMeshID);

            //Debug.Log($"Mesh Registered : {mesh} : {asset.instanceID}");
        }

        private void __OnMeshUnregistered(MeshInstanceMeshAsset asset)
        {
            if (!batchMeshIDs.isCreated)
                return;

            var graphicsSystem = this.graphicsSystem;
            if (graphicsSystem == null)
                return;

            //Debug.Log($"Mesh Unregistered : {asset.instanceID}");

            batchMeshIDs.lookupJobManager.CompleteReadWriteDependency();

            var writer = batchMeshIDs.writer;

            __batchMeshIDsToRemove.Add(asset, writer[asset]);

            //graphicsSystem.UnregisterMesh(writer[asset]);

            writer.Remove(asset);
        }
    }

    [BurstCompile, 
        CreateAfter(typeof(MeshInstanceRendererSystem)), 
        UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    public partial struct MeshInstanceRendererReplaceSystem : ISystem
    {
        private struct Replace
        {
            [ReadOnly]
            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader builders;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID>.Reader batchMaterialIDs;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMeshAsset, BatchMeshID>.Reader batchMeshIDs;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public BufferAccessor<MeshInstanceRendererMaterialModifier> materialModifiers;

            [ReadOnly]
            public BufferAccessor<MeshInstanceRendererMeshModifier> meshModifiers;

            [ReadOnly]
            public BufferAccessor<MeshInstanceNode> renderers;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MaterialMeshInfo> materialMeshInfos;

            public void Execute(int index)
            {
                if (builders.ContainsKey(entityArray[index]))
                    return;

                var renderers = this.renderers[index];
                MaterialMeshInfo materialMeshInfo;
                if (materialModifiers.Length > index)
                {
                    BatchMaterialID source, destination;
                    var materialModifiers = this.materialModifiers[index];
                    foreach (var modifier in materialModifiers)
                    {
                        source = batchMaterialIDs[modifier.source];
                        destination = batchMaterialIDs[modifier.destination];
                        foreach (var renderer in renderers)
                        {
                            materialMeshInfo = materialMeshInfos[renderer.entity];

                            if(materialMeshInfo.MaterialID == source)
                            {
                                materialMeshInfo.MaterialID = destination;

                                materialMeshInfos[renderer.entity] = materialMeshInfo;
                            }
                        }
                    }
                }

                if (meshModifiers.Length > index)
                {
                    BatchMeshID source, destination;
                    var meshModifiers = this.meshModifiers[index];
                    foreach (var modifier in meshModifiers)
                    {
                        source = batchMeshIDs[modifier.source];
                        destination = batchMeshIDs[modifier.destination];
                        foreach (var renderer in renderers)
                        {
                            materialMeshInfo = materialMeshInfos[renderer.entity];

                            if (materialMeshInfo.MeshID == source)
                            {
                                materialMeshInfo.MeshID = destination;

                                materialMeshInfos[renderer.entity] = materialMeshInfo;
                            }
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct ReplaceEx : IJobChunk
        {
            [ReadOnly]
            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader builders;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID>.Reader batchMaterialIDs;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMeshAsset, BatchMeshID>.Reader batchMeshIDs;

            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public BufferTypeHandle<MeshInstanceRendererMaterialModifier> materialModifierType;

            [ReadOnly]
            public BufferTypeHandle<MeshInstanceRendererMeshModifier> meshModifierType;

            [ReadOnly]
            public BufferTypeHandle<MeshInstanceNode> rendererType;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MaterialMeshInfo> materialMeshInfos;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Replace replace;
                replace.builders = builders;
                replace.batchMaterialIDs = batchMaterialIDs;
                replace.batchMeshIDs = batchMeshIDs;
                replace.entityArray = chunk.GetNativeArray(entityType);
                replace.materialModifiers = chunk.GetBufferAccessor(ref materialModifierType);
                replace.meshModifiers = chunk.GetBufferAccessor(ref meshModifierType);
                replace.renderers = chunk.GetBufferAccessor(ref rendererType);
                replace.materialMeshInfos = materialMeshInfos;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    replace.Execute(i);
            }
        }

        private EntityQuery __group;

        private EntityTypeHandle __entityType;
        private BufferTypeHandle<MeshInstanceRendererMaterialModifier> __materialModifierType;
        private BufferTypeHandle<MeshInstanceRendererMeshModifier> __meshModifierType;
        private BufferTypeHandle<MeshInstanceNode> __rendererType;
        private ComponentLookup<MaterialMeshInfo> __materialMeshInfos;

        private SharedHashMap<Entity, MeshInstanceRendererBuilder> __builders;

        //private SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID> __batchMaterialIDs;

        //private SharedHashMap<MeshInstanceMeshAsset, BatchMeshID> __batchMeshIDs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __group = builder
                        .WithAll<MeshInstanceNode>()
                        .WithAny<MeshInstanceRendererMaterialModifier, MeshInstanceRendererMeshModifier>()
                        .Build(ref state);

            __entityType = state.GetEntityTypeHandle();
            __materialModifierType = state.GetBufferTypeHandle<MeshInstanceRendererMaterialModifier>(true);
            __meshModifierType = state.GetBufferTypeHandle<MeshInstanceRendererMeshModifier>(true);
            __rendererType = state.GetBufferTypeHandle<MeshInstanceNode>(true);
            __materialMeshInfos = state.GetComponentLookup<MaterialMeshInfo>();

            __builders = state.WorldUnmanaged.GetExistingSystemUnmanaged<MeshInstanceRendererSystem>().builders;

            /*var sharedSystem = world.GetOrCreateSystemManaged<MeshInstanceRendererSharedSystem>();
            __batchMaterialIDs = sharedSystem.batchMaterialIDs;
            __batchMeshIDs = sharedSystem.batchMeshIDs;*/
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var sharedData = SystemAPI.GetSingleton<MeshInstanceRendererSharedData>();

            ref var buildersJobManager = ref __builders.lookupJobManager;
            ref var batchMaterialIDsJobManager = ref sharedData.batchMaterialIDs.lookupJobManager;
            ref var batchMeshIDsJobManager = ref sharedData.batchMeshIDs.lookupJobManager;

            ReplaceEx replace;
            replace.builders = __builders.reader;
            replace.batchMaterialIDs = sharedData.batchMaterialIDs.reader;
            replace.batchMeshIDs = sharedData.batchMeshIDs.reader;
            replace.entityType = __entityType.UpdateAsRef(ref state);
            replace.materialModifierType = __materialModifierType.UpdateAsRef(ref state);
            replace.meshModifierType = __meshModifierType.UpdateAsRef(ref state);
            replace.rendererType = __rendererType.UpdateAsRef(ref state);
            replace.materialMeshInfos = __materialMeshInfos.UpdateAsRef(ref state);
            var jobHandle = JobHandle.CombineDependencies(buildersJobManager.readOnlyJobHandle, batchMaterialIDsJobManager.readOnlyJobHandle, batchMeshIDsJobManager.readOnlyJobHandle);
            jobHandle = replace.ScheduleParallelByRef(__group, JobHandle.CombineDependencies(jobHandle, state.Dependency));

            buildersJobManager.AddReadOnlyDependency(jobHandle);
            batchMaterialIDsJobManager.AddReadOnlyDependency(jobHandle);
            batchMeshIDsJobManager.AddReadOnlyDependency(jobHandle);

            state.Dependency = jobHandle;
        }
    }
}