using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Burst;
using Unity.Transforms;
#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
using Unity.Rendering.Occlusion;
#endif

namespace ZG
{
    public struct MeshInstanceOccluderProxy : ICleanupBufferElementData
    {
        public Entity entity;
    }

#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
    [BurstCompile, UpdateInGroup(typeof(MeshInstanceSystemGroup))]
    public partial struct MeshInstanceOccluderSystem : ISystem
    {
        private struct CollectToDestroy
        {
            [ReadOnly]
            public BufferAccessor<MeshInstanceOccluderProxy> occluderProxies;

            public NativeList<Entity> entities;

            public void Execute(int index)
            {
                entities.AddRange(this.occluderProxies[index].Reinterpret<Entity>().AsNativeArray());
            }
        }

        [BurstCompile]
        private struct CollectToDestroyEx : IJobChunk
        {
            [ReadOnly]
            public BufferTypeHandle<MeshInstanceOccluderProxy> occluderProxyType;

            public NativeList<Entity> entities;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectToDestroy collect;
                collect.occluderProxies = batchInChunk.GetBufferAccessor(occluderProxyType);
                collect.entities = entities;

                int count = batchInChunk.Count;
                for (int i = 0; i < count; ++i)
                    collect.Execute(i);
            }
        }

        private struct CollectToCreate
        {
            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>.Reader prefabs;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<MeshInstanceOccluderData> instances;

            public NativeParallelMultiHashMap<int, Entity> entities;

            public void Execute(int index)
            {
                ref var definition = ref instances[index].definition.Value;

                entities.Add(instances[index].definition.Value.instanceID, entityArray[index]);
            }
        }

        [BurstCompile]
        private struct CollectToCreateEx : IJobChunk
        {
            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>>.Reader prefabs;

            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceOccluderData> instanceType;

            public NativeParallelMultiHashMap<int, Entity> entities;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectToCreate collectToCreate;
                collectToCreate.prefabs = prefabs;
                collectToCreate.entityArray = batchInChunk.GetNativeArray(entityType);
                collectToCreate.instances = batchInChunk.GetNativeArray(instanceType);
                collectToCreate.entities = entities;

                int count = batchInChunk.Count;
                for (int i = 0; i < count; ++i)
                    collectToCreate.Execute(i);
            }
        }

        [BurstCompile]
        private struct InitEntities : IJobParallelFor
        {
            public int occluderProxyCount;

            [ReadOnly]
            public NativeArray<Entity> instanceEntities;
            [ReadOnly]
            public NativeArray<Entity> prefabEntities;

            [NativeDisableContainerSafetyRestriction]
            public BufferLookup<MeshInstanceOccluderProxy> occluderProxies;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Parent> parents;

            public void Execute(int index)
            {
                int numPrefabEntities = prefabEntities.Length;
                Entity prefabEntity = prefabEntities[index], instanceEntity;

                Parent parent;

                var entities = occluderProxies[prefabEntity].Reinterpret<Entity>();
                entities.ResizeUninitialized(occluderProxyCount);
                for (int i = 0; i < occluderProxyCount; ++i)
                {
                    instanceEntity = instanceEntities[numPrefabEntities * i + index];

                    parent.Value = prefabEntity;
                    parents[instanceEntity] = parent;

                    entities[i] = instanceEntity;
                }
            }
        }

        [BurstCompile]
        private struct DisposeAll : IJob
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> instanceEntities;
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> prefabEntities;

            public void Execute()
            {

            }
        }

        private struct Result
        {
            public struct Info
            {
                public int occluderProxyCount;
                public int prefabEntityCount;
                public int prefabEntityStartIndex;
                public int instanceEntityStartIndex;

                public int instanceEntityCount => prefabEntityCount * occluderProxyCount;

                public Info(
                    int prefabEntityStartIndex,
                    int prefabEntityCount,
                    in NativeArray<Entity> instanceEntities,
                    ref BlobArray<Entity> occluderProxies,
                    ref EntityManager entityManager,
                    ref int instanceEntityStartIndex)
                {
                    occluderProxyCount = occluderProxies.Length;

                    this.prefabEntityCount = prefabEntityCount;
                    this.prefabEntityStartIndex = prefabEntityStartIndex;
                    this.instanceEntityStartIndex = instanceEntityStartIndex;

                    int instanceEntityCount = this.instanceEntityCount;
                    NativeArray<Entity> entities = instanceEntities.GetSubArray(instanceEntityStartIndex, instanceEntityCount), rigRootEntities;
                    for (int i = 0; i < occluderProxyCount; ++i)
                    {
                        rigRootEntities = entities.GetSubArray(i * prefabEntityCount, prefabEntityCount);

                        entityManager.Instantiate(occluderProxies[i], rigRootEntities);
                    }

                    instanceEntityStartIndex += instanceEntityCount;
                }

                public Result ToResult(
                    in NativeArray<Entity> prefabEntities,
                    in NativeArray<Entity> instanceEntities)
                {
                    Result result;
                    result.occluderProxyCount = occluderProxyCount;
                    result.prefabEntities = prefabEntities.GetSubArray(prefabEntityStartIndex, prefabEntityCount);
                    result.instanceEntities = instanceEntities.GetSubArray(instanceEntityStartIndex, instanceEntityCount);

                    return result;
                }
            }

            public int occluderProxyCount;
            public NativeArray<Entity> prefabEntities;
            public NativeArray<Entity> instanceEntities;

            public static void Schedule(
                int innerloopBatchCount,
                in NativeArray<Entity> prefabEntities,
                in NativeArray<Entity> instanceEntities,
                in NativeArray<Info> infos,
                ref SystemState systemState)
            {
                var occluderProxies = systemState.GetBufferLookup<MeshInstanceOccluderProxy>();
                var parents = systemState.GetComponentLookup<Parent>();

                Result result;
                JobHandle temp, inputDeps = systemState.Dependency;
                JobHandle? jobHandle = null;
                int length = infos.Length;
                for (int i = 0; i < length; ++i)
                {
                    result = infos[i].ToResult(prefabEntities, instanceEntities);

                    temp = result.ScheduleInitEntities(
                        innerloopBatchCount,
                        ref occluderProxies,
                        ref parents, 
                        inputDeps);

                    jobHandle = jobHandle == null ? temp : JobHandle.CombineDependencies(jobHandle.Value, temp);
                }

                if (jobHandle != null)
                    systemState.Dependency = jobHandle.Value;
            }

            public JobHandle ScheduleInitEntities(
                int innerloopBatchCount,
                ref BufferLookup<MeshInstanceOccluderProxy> occluderProxies,
                ref ComponentLookup<Parent> parents,
                in JobHandle inputDeps)
            {
                InitEntities initEntities;
                initEntities.occluderProxyCount = occluderProxyCount;
                initEntities.instanceEntities = instanceEntities;
                initEntities.prefabEntities = prefabEntities;
                initEntities.occluderProxies = occluderProxies;
                initEntities.parents = parents;

                return initEntities.Schedule(prefabEntities.Length, innerloopBatchCount, inputDeps);
            }
        }

        public static readonly int InnerloopBatchCount = 1;

        private EntityQuery __groupToDestroy;
        private EntityQuery __groupToCreate;
        private SharedHashMap<int, BlobAssetReference<MeshInstanceOccluderPrefab>> __prefabs;

        public void OnCreate(ref SystemState state)
        {
            BurstUtility.InitializeJobParallelFor<InitEntities>();
            BurstUtility.InitializeJob<DisposeAll>();

            __groupToDestroy = state.GetEntityQuery(
                       new EntityQueryDesc()
                       {
                           All = new ComponentType[]
                           {
                                ComponentType.ReadOnly<MeshInstanceOccluderProxy>()
                           },
                           None = new ComponentType[]
                           {
                                typeof(MeshInstanceOccluderID)
                           },
                           Options = EntityQueryOptions.IncludeDisabled
                       });

            __groupToCreate = state.GetEntityQuery(
                    new EntityQueryDesc()
                    {
                        All = new ComponentType[]
                        {
                            ComponentType.ReadOnly<MeshInstanceOccluderData>(),
                            ComponentType.ReadOnly<MeshInstanceOccluderID>()
                        },
                        None = new ComponentType[]
                        {
                            typeof(MeshInstanceOccluderProxy)
                        },
                        Options = EntityQueryOptions.IncludeDisabled
                    });

            __prefabs = state.World.GetOrCreateSystemUnmanaged<MeshInstanceOccluderFactorySystem>().prefabs;
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;

            if(!__groupToDestroy.IsEmptyIgnoreFilter)
            {
                using (var entities = new NativeList<Entity>(Allocator.TempJob))
                {
                    CollectToDestroyEx collect;
                    collect.occluderProxyType = state.GetBufferTypeHandle<MeshInstanceOccluderProxy>(true);
                    collect.entities = entities;

                    collect.RunBurstCompatible(__groupToDestroy);

                    entityManager.RemoveComponent<MeshInstanceOccluderProxy>(__groupToDestroy);
                    entityManager.DestroyEntity(entities);
                }
            }

            int entityCount = __groupToCreate.CalculateEntityCount();
            if (entityCount > 0)
            {
                using (var entities = new NativeParallelMultiHashMap<int, Entity>(__groupToCreate.CalculateEntityCount(), Allocator.TempJob))
                {
                    __prefabs.lookupJobManager.CompleteReadOnlyDependency();

                    var prefabReader = __prefabs.reader;

                    CollectToCreateEx collectToCreate;
                    collectToCreate.prefabs = prefabReader;
                    collectToCreate.entityType = state.GetEntityTypeHandle();
                    collectToCreate.instanceType = state.GetComponentTypeHandle<MeshInstanceOccluderData>(true);
                    collectToCreate.entities = entities;
                    collectToCreate.RunBurstCompatible(__groupToCreate);

                    entityManager.AddComponent<MeshInstanceOccluderProxy>(__groupToCreate);

                    if (!entities.IsEmpty)
                    {
                        using (var keys = entities.GetKeyArray(Allocator.Temp))
                        {
                            int count = keys.ConvertToUniqueArray(), instanceCount = 0, numKeys, key;
                            for (int i = 0; i < count; ++i)
                            {
                                key = keys[i];
                                ref var prefab = ref prefabReader[key].Value;

                                numKeys = entities.CountValuesForKey(key);

                                instanceCount += numKeys * prefab.occluderProxies.Length;
                            }

                            var prefabEntities = new NativeArray<Entity>(entityCount, Allocator.TempJob);
                            var instanceEntities = new NativeArray<Entity>(instanceCount, Allocator.TempJob);

                            int instanceEntityStartIndex = 0, prefabEntityStartIndex = 0;
                            var infos = new NativeArray<Result.Info>(count, Allocator.Temp);
                            {
                                int prefabEntityStartCount;
                                NativeParallelMultiHashMap<int, Entity>.Enumerator enumerator;
                                for (int i = 0; i < count; ++i)
                                {
                                    key = keys[i];

                                    prefabEntityStartCount = 0;

                                    enumerator = entities.GetValuesForKey(key);
                                    while (enumerator.MoveNext())
                                        prefabEntities[prefabEntityStartIndex + prefabEntityStartCount++] = enumerator.Current;

                                    ref var prefab = ref prefabReader[key].Value;

                                    infos[i] = new Result.Info(
                                        prefabEntityStartIndex,
                                        prefabEntityStartCount,
                                        instanceEntities,
                                        ref prefab.occluderProxies,
                                        ref entityManager,
                                        ref instanceEntityStartIndex);

                                    prefabEntityStartIndex += prefabEntityStartCount;
                                }

                                Result.Schedule(InnerloopBatchCount, prefabEntities, instanceEntities, infos, ref state);

                                var jobHandle = state.Dependency;

                                DisposeAll disposeAll;
                                disposeAll.prefabEntities = prefabEntities;
                                disposeAll.instanceEntities = instanceEntities;

                                state.Dependency = disposeAll.Schedule(jobHandle);

                                infos.Dispose();
                            }
                        }
                    }
                }
            }
        }
    }
#endif
}