using System;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Burst;
using Unity.Transforms;
#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
using Unity.Rendering.Occlusion;
#endif

namespace ZG
{
    public struct MeshInstanceOcclusionMeshAsset : IDisposable, IEquatable<MeshInstanceOcclusionMeshAsset>
    {
        public BlobAssetReference<float4> vertexData;
        public BlobAssetReference<int> indexData;
        public int vertexCount;
        public int indexCount;

        public void Dispose()
        {
            vertexData.Dispose();
            indexData.Dispose();
        }

        public bool Equals(MeshInstanceOcclusionMeshAsset other)
        {
            return (vertexData.GetHashCode() == other.vertexData.GetHashCode() && indexData.GetHashCode() == other.indexData.GetHashCode());
        }

        public override int GetHashCode()
        {
            return vertexData.GetHashCode() ^ indexData.GetHashCode();
        }
    }

    public struct MeshInstanceOccluderDefinition
    {
        public struct Occluder
        {
            public int meshIndex;
            public int rendererIndex;
            public float4x4 matrix;
        }

        public struct OccluderProxy
        {
            public int meshIndex;
            public float4x4 matrix;
        }

        public int instanceID;

        public BlobArray<Occluder> occluders;
        public BlobArray<OccluderProxy> occluderProxies;
    }

    public struct MeshInstanceOccluderData : IComponentData
    {
        public BlobAssetReference<MeshInstanceOccluderDefinition> definition;
    }

    public struct MeshInstanceOccluderID : ICleanupComponentData
    {
        public int value;
    }

    public struct MeshInstanceOccluderPrefab
    {
        public int instanceCount;
        public BlobArray<Entity> occluderProxies;
    }

#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
    [BurstCompile, UpdateInGroup(typeof(MeshInstanceSystemGroup), OrderFirst = true)]
    public partial struct MeshInstanceOccluderFactorySystem : ISystem
    {
        private struct Result
        {
            public int offset;
            public Entity entity;
            public BlobAssetReference<MeshInstanceOccluderPrefab> prefab;
        }

        private struct CollectToDestroy
        {
            [ReadOnly]
            public NativeArray<MeshInstanceOccluderID> ids;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>.Writer prefabs;

            public NativeList<Entity> results;

            public void Execute(int index)
            {
                int id = ids[index].value;
                var prefabAsset = prefabs[id];
                ref var prefab = ref prefabAsset.Value;
                if (--prefab.instanceCount > 0)
                    return;

                prefabs.Remove(id);

                int numOccluderProxies = prefab.occluderProxies.Length;
                for (int i = 0; i < numOccluderProxies; ++i)
                    results.Add(prefab.occluderProxies[i]);

                prefabAsset.Dispose();
            }
        }

        [BurstCompile]
        private struct CollectToDestroyEx : IJobChunk
        {
            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceOccluderID> idType;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>.Writer prefabs;

            public NativeList<Entity> results;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectToDestroy collectToDestroy;
                collectToDestroy.ids = batchInChunk.GetNativeArray(idType);
                collectToDestroy.prefabs = prefabs;
                collectToDestroy.results = results;

                int count = batchInChunk.Count;
                for (int i = 0; i < count; ++i)
                    collectToDestroy.Execute(i);
            }
        }

        private struct CollectToCreate
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<MeshInstanceOccluderData> instances;

            [ReadOnly]
            public NativeArray<MeshInstanceRendererID> rendererIDs;

            [ReadOnly]
            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Reader rendererBuilders;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstancePrefab>>.Reader rendererPrefabs;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>.Writer prefabs;

            public NativeList<Entity> renderers;

            public NativeArray<Result> results;

            public NativeArray<Entity> entities;

            public NativeArray<int> counters;

            public void Execute(int index)
            {
                Entity entity = entityArray[index];
                ref var definition = ref instances[index].definition.Value;
                if (prefabs.TryGetValue(definition.instanceID, out var prefab))
                    ++prefab.Value.instanceCount;
                else
                {
                    if (index < rendererIDs.Length)
                    {
                        int rendererInstanceID = rendererIDs[index].value;
                        if (rendererBuilders.ContainsKey(rendererInstanceID))
                            return;

                        ref var rendererPrefab = ref rendererPrefabs[rendererInstanceID].Value;
                        int numOccluders = definition.occluders.Length;
                        for (int i = 0; i < numOccluders; ++i)
                            renderers.Add(rendererPrefab.nodes[definition.occluders[i].rendererIndex]);
                    }

                    using (var blobBuilder = new BlobBuilder(Allocator.Temp))
                    {
                        ref var root = ref blobBuilder.ConstructRoot<MeshInstanceOccluderPrefab>();
                        root.instanceCount = 1;

                        int numOccluderProxies = definition.occluderProxies.Length;
                        var occluderProxies = blobBuilder.Allocate(ref root.occluderProxies, numOccluderProxies);

                        Result result;
                        result.offset = counters[0];
                        result.entity = entity;
                        result.prefab = blobBuilder.CreateBlobAssetReference<MeshInstanceOccluderPrefab>(Allocator.Persistent);

                        counters[0] = result.offset + numOccluderProxies;

                        prefabs[definition.instanceID] = result.prefab;

                        int numResults = counters[1];
                        results[numResults++] = result;
                        counters[1] = numResults;
                    }
                }

                int numEntities = counters[2];

                entities[numEntities++] = entity;

                counters[2] = numEntities;
            }
        }

        [BurstCompile]
        private struct CollectToCreateEx : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceOccluderData> instanceType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceRendererID> rendererIDType;

            [ReadOnly]
            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Reader rendererBuilders;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstancePrefab>>.Reader rendererPrefabs;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>.Writer prefabs;

            public NativeList<Entity> renderers;

            public NativeArray<Result> results;

            public NativeArray<Entity> entities;

            public NativeArray<int> counters;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask, int indexOfFirstEntityInQuery)
            {
                CollectToCreate collect;
                collect.entityArray = batchInChunk.GetNativeArray(entityType);
                collect.instances = batchInChunk.GetNativeArray(instanceType);
                collect.rendererIDs = batchInChunk.GetNativeArray(rendererIDType);
                collect.rendererBuilders = rendererBuilders;
                collect.rendererPrefabs = rendererPrefabs;
                collect.prefabs = prefabs;
                collect.renderers = renderers;
                collect.results = results;
                collect.entities = entities;
                collect.counters = counters;

                int count = batchInChunk.Count;
                for (int i = 0; i < count; ++i)
                    collect.Execute(i);
            }
        }

        [BurstCompile]
        private struct Init : IJobParallelFor
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Result> results;

            [ReadOnly]
            public SingletonAssetContainer<MeshInstanceOcclusionMeshAsset>.Reader assets;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstancePrefab>>.Reader rendererPrefabs;

            [ReadOnly]
            public ComponentLookup<MeshInstanceRendererID> rendererIDs;

            [ReadOnly]
            public ComponentLookup<MeshInstanceOccluderData> instances;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<OcclusionMesh> meshes;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToParent> localToParents;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;

            public OcclusionMesh Create(in MeshInstanceOcclusionMeshAsset asset, in float4x4 matrix)
            {
                OcclusionMesh result;

                result.vertexCount = asset.vertexCount;
                result.indexCount = asset.indexCount;
                result.vertexData = asset.vertexData;
                result.indexData = asset.indexData;

                result.transformedVertexData = BlobAssetReference<float4>.Null;

                result.screenMin = float.MaxValue;
                result.screenMax = -float.MaxValue;

                result.localToWorld = matrix;

                return result;
            }

            public void Execute(int index)
            {
                var result = results[index];
                ref var definition = ref instances[result.entity].definition.Value;

                SingletonAssetContainerHandle handle;
                handle.instanceID = definition.instanceID;

                LocalToParent localToParent;
                LocalToWorld localToWorld;
                Entity entity;
                if (rendererIDs.HasComponent(result.entity))
                {
                    ref var rendererPrefab = ref rendererPrefabs[rendererIDs[result.entity].value].Value;

                    int numOccluders = definition.occluders.Length;
                    for (int i = 0; i < numOccluders; ++i)
                    {
                        ref var occluder = ref definition.occluders[i];

                        handle.index = occluder.meshIndex;

                        entity = rendererPrefab.nodes[occluder.rendererIndex];

                        meshes[entity] = Create(assets[handle], occluder.matrix);
                    }
                }

                ref var prefab = ref result.prefab.Value;
                int numOccluderProxies = prefab.occluderProxies.Length;
                for(int i = 0; i < numOccluderProxies; ++i)
                {
                    ref var occluderProxy = ref definition.occluderProxies[i];

                    handle.index = occluderProxy.meshIndex;

                    entity = prefab.occluderProxies[i];

                    meshes[entity] = Create(assets[handle], occluderProxy.matrix);

                    localToParent.Value = float4x4.identity;
                    localToParents[entity] = localToParent;

                    localToWorld.Value = float4x4.identity;
                    localToWorlds[entity] = localToWorld;
                }
            }
        }

        [BurstCompile]
        private struct Apply : IJobParallelFor
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public ComponentLookup<MeshInstanceOccluderData> instances;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceOccluderID> ids;

            public void Execute(int index)
            {
                Entity entity = entityArray[index];
                MeshInstanceOccluderID id;
                id.value = instances[entity].definition.Value.instanceID;
                ids[entity] = id;
            }
        }

        public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>> prefabs
        {
            get;

            private set;
        }

        public static readonly int InnerloopBatchCount = 1;

        private EntityQuery __groupToDestroy;
        private EntityQuery __groupToCreate;
        private EntityArchetype __entityArchetype;
        private SingletonAssetContainer<MeshInstanceOcclusionMeshAsset> __assets;
        private SharedHashMap<int, MeshInstanceRendererPrefabBuilder> __rendererBuilders;
        private SharedHashMap<int, BlobAssetReference<MeshInstancePrefab>> __rendererPrefabs;

        public void OnCreate(ref SystemState state)
        {
            BurstUtility.InitializeJobParallelFor<Init>();
            BurstUtility.InitializeJobParallelFor<Apply>();

            state.SetAlwaysUpdateSystem(true);

            __groupToDestroy = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceOccluderID>(), 
                    },
                    None = new ComponentType[]
                    {
                        typeof(MeshInstanceOccluderData)
                    }, 
                    Options = EntityQueryOptions.IncludeDisabled
                });

            __groupToCreate = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceOccluderData>(),
                    },
                    None = new ComponentType[]
                    {
                        typeof(MeshInstanceOccluderID)
                    },
                    Options = EntityQueryOptions.IncludeDisabled
                });

            __entityArchetype = state.EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(OcclusionMesh), 
                typeof(LocalToWorld), 
                typeof(LocalToParent), 
                typeof(Parent));

            __assets = SingletonAssetContainer<MeshInstanceOcclusionMeshAsset>.instance;

            var system = state.World.GetOrCreateSystem<MeshInstanceFactorySystem>();
            __rendererBuilders = system.builders;
            __rendererPrefabs = system.prefabs;

            prefabs = new SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            prefabs.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();

            var prefabs = this.prefabs;
            prefabs.lookupJobManager.CompleteReadWriteDependency();
            var prefabWriter = prefabs.writer;

            var entityManager = state.EntityManager;

            if (!__groupToDestroy.IsEmptyIgnoreFilter)
            {
                using (var results = new NativeList<Entity>(Allocator.TempJob))
                {
                    CollectToDestroyEx collect;
                    collect.idType = state.GetComponentTypeHandle<MeshInstanceOccluderID>(true);
                    collect.prefabs = prefabWriter;
                    collect.results = results;
                    collect.RunBurstCompatible(__groupToDestroy);

                    entityManager.RemoveComponent<MeshInstanceOccluderID>(__groupToDestroy);

                    entityManager.DestroyEntity(results);
                }
            }

            if (!__groupToCreate.IsEmptyIgnoreFilter)
            {
                var rendererPrefabs = __rendererPrefabs.reader;

                var entityCount = __groupToCreate.CalculateEntityCount();

                var results = new NativeArray<Result>(entityCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                var entities = new NativeArray<Entity>(entityCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                int numResults, numEntities;
                using (var renderers = new NativeList<Entity>(Allocator.TempJob))
                using (var counters = new NativeArray<int>(3, Allocator.TempJob, NativeArrayOptions.ClearMemory))
                {
                    __rendererBuilders.lookupJobManager.CompleteReadOnlyDependency();
                    __rendererPrefabs.lookupJobManager.CompleteReadOnlyDependency();

                    CollectToCreateEx collect;
                    collect.entityType = state.GetEntityTypeHandle();
                    collect.instanceType = state.GetComponentTypeHandle<MeshInstanceOccluderData>(true);
                    collect.rendererIDType = state.GetComponentTypeHandle<MeshInstanceRendererID>(true);
                    collect.rendererBuilders = __rendererBuilders.reader;
                    collect.rendererPrefabs = rendererPrefabs;
                    collect.prefabs = prefabWriter;
                    collect.renderers = renderers;
                    collect.results = results;
                    collect.entities = entities;
                    collect.counters = counters;
                    collect.Run(__groupToCreate);

                    entityManager.AddComponentBurstCompatible<OcclusionMesh>(renderers);

                    numEntities = counters[2];
                    entityManager.AddComponentBurstCompatible<MeshInstanceOccluderID>(entities.GetSubArray(0, numEntities));

                    using (var entityArray = state.EntityManager.CreateEntity(__entityArchetype, counters[0], Allocator.Temp))
                    {
                        Result result;
                        int i, j, numOccluderProxies;
                        numResults = counters[1];
                        for (i = 0; i < numResults; ++i)
                        {
                            result = results[i];
                            ref var prefab = ref result.prefab.Value;

                            numOccluderProxies = prefab.occluderProxies.Length;
                            for (j = 0; j < numOccluderProxies; ++j)
                                prefab.occluderProxies[j] = entityArray[result.offset + j];
                        }
                    }
                }

                var inputDeps = state.Dependency;

                var instances = state.GetComponentLookup<MeshInstanceOccluderData>(true);

                Init init;
                init.results = results;
                init.assets = __assets.reader;
                init.rendererPrefabs = rendererPrefabs;
                init.rendererIDs = state.GetComponentLookup<MeshInstanceRendererID>(true);
                init.instances = instances;
                init.meshes = state.GetComponentLookup<OcclusionMesh>();
                init.localToParents = state.GetComponentLookup<LocalToParent>();
                init.localToWorlds = state.GetComponentLookup<LocalToWorld>();
                var jobHandle = init.Schedule(numResults, InnerloopBatchCount, state.Dependency);

                __assets.AddDependency(state.GetSystemID(), jobHandle);

                __rendererPrefabs.lookupJobManager.AddReadOnlyDependency(jobHandle);

                Apply apply;
                apply.entityArray = entities;
                apply.instances = instances;
                apply.ids = state.GetComponentLookup<MeshInstanceOccluderID>();
                jobHandle = JobHandle.CombineDependencies(jobHandle, apply.Schedule(numEntities, InnerloopBatchCount, inputDeps));

                state.Dependency = jobHandle;
            }
        }
    }

#endif
}