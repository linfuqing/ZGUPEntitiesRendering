using System;
using System.Threading;
using System.Collections.Generic;
//using System.Runtime.InteropServices;
using Unity.Jobs;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
//using UnityEngine.SceneManagement;
//using ZG.Unsafe;
//using FrustumPlanes = Unity.Rendering.FrustumPlanes;

//[assembly: RegisterGenericJobType(typeof(ZG.MeshInstanceTransformJob<ZG.MeshInstanceRendererTransformSystem.Enumerator, ZG.MeshInstanceRendererTransformSystem.Enumerable>))]

namespace ZG
{
    public struct MeshInstanceRendererBuilder
    {
        public bool isStatic;
        public int instanceID;

        public int startRendererDefinitionIndex;
        public int rendererDefinitionCount;
        public int rendererCount;
        public int startRendererIndex;
        public int startLODGroupIndex;
        public int lodGroupCount;
    }

    [/*UpdateInGroup(typeof(PresentationSystemGroup)), */
        CreateAfter(typeof(EntityCommandSharedSystemGroup)), 
        UpdateInGroup(typeof(StructuralChangePresentationSystemGroup), OrderFirst = true)]
    public partial class MeshInstanceSystemGroup : ComponentSystemGroup
    {
        private EntityCommandSharedSystemGroup __sharedSystemGroup;

        protected override void OnCreate()
        {
            base.OnCreate();

            __sharedSystemGroup = World.GetExistingSystemManaged<EntityCommandSharedSystemGroup>();
        }

        protected override void OnUpdate()
        {
            __sharedSystemGroup.Update();

            base.OnUpdate();
        }
    }

    [BurstCompile, UpdateInGroup(typeof(MeshInstanceSystemGroup), OrderFirst = true)]
    public partial struct MeshInstanceFactorySystem : ISystem
    {
        //private EntityQuery __groupToAssign;

        private EntityArchetype __rootGroupArchetype;
        private EntityArchetype __subGroupArchetype;
        private EntityArchetype __instanceArchetype;
        private EntityArchetype __lodArchetype;

        private ComponentTypeHandle<MeshInstanceRendererID> __idType;
        private ComponentTypeHandle<MeshInstanceRendererData> __instanceType;
        private ComponentLookup<RenderBounds> __renderBounds;
        private ComponentLookup<LocalToWorld> __localToWorlds;
        private ComponentLookup<MaterialMeshInfo> __materialMeshInfos;
        private ComponentLookup<MeshLODGroupComponent> __meshLODGroupComponents;
        private ComponentLookup<MeshLODComponent> __meshLODComponents;
        private ComponentLookup<MeshInstanceLODParentIndex> __lodParentIndices;
        private ComponentLookup<MeshStreamingVertexOffset> __meshStreamingVertexOffsets;
        private ComponentLookup<MeshInstanceLODParent> __meshInstanceLODParents;
        private BufferLookup<MeshInstanceLODChild> __lodChildren;

        private SingletonAssetContainer<TypeIndex> __componentTypeIndices;
        private SingletonAssetContainer<ComponentTypeSet> __componentTypes;

        private SingletonAssetContainer<MeshInstanceMaterialAsset> __materialAssets;
        private SingletonAssetContainer<MeshInstanceMeshAsset> __meshAssets;

        //private SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID> __batchMaterialIDs;
        //private SharedHashMap<MeshInstanceMeshAsset, BatchMeshID> __batchMeshIDs;

        private EntityComponentAssigner __assigner;

        public static readonly int InnerloopBatchCount = 4;

#if UNITY_ANDROID || UNITY_IOS
        public static readonly int MaxRendererDefinitionCount = 64;
#else
        public static readonly int MaxRendererDefinitionCount = 256;
#endif

        public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>> prefabs
        {
            get;

            private set;
        }

        public SharedHashMap<int, MeshInstanceRendererPrefabBuilder> builders
        {
            get;

            private set;
        }

        public EntityQuery groupToCreate
        {
            get;

            private set;
        }

        public EntityQuery groupToDestroy
        {
            get;

            private set;
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            prefabs = new SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>(Allocator.Persistent);

            builders = new SharedHashMap<int, MeshInstanceRendererPrefabBuilder>(Allocator.Persistent);

            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                groupToDestroy = builder
                        .WithAll<MeshInstanceRendererID>()
                        .WithNone<MeshInstanceRendererData>()
                        .AddAdditionalQuery()
                        .WithAll<MeshInstanceRendererID, MeshInstanceRendererData>()
                        .WithAny<MeshInstanceRendererDisabled, MeshInstanceRendererDirty>()
                        .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                        .Build(ref state);

            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                groupToCreate = builder
                    .WithAll<MeshInstanceRendererData>()
                    .WithNone<MeshInstanceRendererID, MeshInstanceRendererDisabled>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                    .Build(ref state);

            MeshInstanceRendererUtility.CreateEntityArchetypes(
                state.EntityManager,
                out __rootGroupArchetype,
                out __subGroupArchetype,
                out __instanceArchetype,
                out __lodArchetype);

            __idType = state.GetComponentTypeHandle<MeshInstanceRendererID>(true);
            __instanceType = state.GetComponentTypeHandle<MeshInstanceRendererData>(true);
            __renderBounds = state.GetComponentLookup<RenderBounds>();
            __localToWorlds = state.GetComponentLookup<LocalToWorld>();
            __materialMeshInfos = state.GetComponentLookup<MaterialMeshInfo>();
            __meshLODGroupComponents = state.GetComponentLookup<MeshLODGroupComponent>();
            __meshLODComponents = state.GetComponentLookup<MeshLODComponent>();
            __lodParentIndices = state.GetComponentLookup<MeshInstanceLODParentIndex>();
            __meshStreamingVertexOffsets = state.GetComponentLookup<MeshStreamingVertexOffset>();
            __meshInstanceLODParents = state.GetComponentLookup<MeshInstanceLODParent>();
            __lodChildren = state.GetBufferLookup<MeshInstanceLODChild>();

            //var sharedSystem = state.World.GetExistingSystemManaged<MeshInstanceRendererSharedSystem>();

            //__batchMaterialIDs = sharedSystem.batchMaterialIDs;
            //__batchMeshIDs = sharedSystem.batchMeshIDs;

            __materialAssets = SingletonAssetContainer<MeshInstanceMaterialAsset>.Retain();
            __meshAssets = SingletonAssetContainer<MeshInstanceMeshAsset>.Retain();

            __componentTypeIndices = SingletonAssetContainer<TypeIndex>.Retain();
            __componentTypes = SingletonAssetContainer<ComponentTypeSet>.Retain();

            __assigner = new EntityComponentAssigner(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            __assigner.Dispose();

            __materialAssets.Release();
            __meshAssets.Release();

            __componentTypes.Release();
            __componentTypeIndices.Release();

            var enumerator = prefabs.GetEnumerator();
            while (enumerator.MoveNext())
                enumerator.Current.Value.Dispose();

            prefabs.Dispose();

            builders.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;

            var prefabs = this.prefabs;
            var builders = this.builders;

            var groupToDestroy = this.groupToDestroy;
            if (!groupToDestroy.IsEmpty)
                MeshInstanceRendererUtility.Destroy(
                    groupToDestroy,
                    __idType.UpdateAsRef(ref state),
                    ref prefabs,
                    ref builders,
                    ref entityManager);

            var results = new NativeList<MeshInstanceRendererPrefabBuilder>(Allocator.TempJob);

            MeshInstanceRendererUtility.Create/*Function*/(
                MaxRendererDefinitionCount,
                __rootGroupArchetype,
                __subGroupArchetype,
                __instanceArchetype,
                __lodArchetype,
                groupToCreate,
                __instanceType.UpdateAsRef(ref state),
                __componentTypeIndices.reader,
                __componentTypes.reader,
                ref results,
                ref builders,
                ref prefabs,
                ref __assigner,
                ref entityManager);

            var jobHandle = state.Dependency;

            __assigner.Playback(ref state);

            var sharedData = SystemAPI.GetSingleton<MeshInstanceRendererSharedData>();

            __renderBounds.Update(ref state);
            __localToWorlds.Update(ref state);
            __materialMeshInfos.Update(ref state);
            __meshLODGroupComponents.Update(ref state);
            __meshLODComponents.Update(ref state);
            __lodParentIndices.Update(ref state);
            __meshStreamingVertexOffsets.Update(ref state);
            __meshInstanceLODParents.Update(ref state);
            __lodChildren.Update(ref state);

            jobHandle = MeshInstanceRendererUtility.Schedule/*Function*/(
                state.GetSystemID(),
                InnerloopBatchCount,
                jobHandle,
                results.AsArray(),
                __materialAssets,
                __meshAssets,
                sharedData.batchMaterialIDs,
                sharedData.batchMeshIDs,
                ref __renderBounds,
                ref __localToWorlds,
                ref __materialMeshInfos,
                ref __meshLODGroupComponents,
                ref __meshLODComponents,
                ref __lodParentIndices,
                ref __meshStreamingVertexOffsets,
                ref __meshInstanceLODParents,
                ref __lodChildren);

            jobHandle = results.Dispose(jobHandle);

            jobHandle = JobHandle.CombineDependencies(jobHandle, state.Dependency);

            state.Dependency = jobHandle;
        }
    }

    [BurstCompile, /*AlwaysUpdateSystem, */UpdateInGroup(typeof(MeshInstanceSystemGroup))]
    public partial struct MeshInstanceRendererSystem : ISystem
    {
        private struct Key : IEquatable<Key>
        {
            public bool isStatic;
            public int index;
            public int instanceID;

            public bool Equals(Key other)
            {
                return isStatic == other.isStatic && index == other.index && instanceID == other.instanceID;
            }

            public override int GetHashCode()
            {
                return (isStatic ? 1 : -1) * (index ^ instanceID);
            }
        }

        private struct Counter
        {
            public int startIndex;
            public int count;

            public int Pop()
            {
                int count = Interlocked.Decrement(ref this.count);
                UnityEngine.Assertions.Assert.IsFalse(count < 0);
                return startIndex + count;
            }
        }

        private struct Result
        {
            public uint meshStreamingVertexOffset;
            public int prefabRendererCount;
            public Entity entity;
            public MeshInstanceRendererBuilder builder;
        }

        private struct CollectToDestroy
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public BufferAccessor<MeshInstanceNode> renderers;
            [ReadOnly]
            public BufferAccessor<MeshInstanceObject> lodGroups;

            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Writer builders;

            public NativeList<Entity> entities;

            public void Execute(int index)
            {
                entities.AddRange(renderers[index].Reinterpret<Entity>().AsNativeArray());

                if (index < lodGroups.Length)
                    entities.AddRange(lodGroups[index].Reinterpret<Entity>().AsNativeArray());

                builders.Remove(entityArray[index]);
            }
        }

        [BurstCompile]
        private struct CollectToDestroyEx : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle entityType;
            [ReadOnly]
            public BufferTypeHandle<MeshInstanceNode> rendererType;
            [ReadOnly]
            public BufferTypeHandle<MeshInstanceObject> lodGroupType;

            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Writer builders;

            public NativeList<Entity> entities;
            public NativeList<Entity> lodGroupEntities;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entityArray = chunk.GetNativeArray(entityType);

                CollectToDestroy collectToDestroy;
                collectToDestroy.entityArray = entityArray;
                collectToDestroy.renderers = chunk.GetBufferAccessor(ref rendererType);
                collectToDestroy.lodGroups = chunk.GetBufferAccessor(ref lodGroupType);
                collectToDestroy.builders = builders;
                collectToDestroy.entities = entities;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                {
                    lodGroupEntities.Add(entityArray[i]);

                    collectToDestroy.Execute(i);
                }
            }
        }

        private struct CollectToCreate
        {
            public bool isStatic;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<MeshInstanceRendererID> ids;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Reader prefabs;

            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Writer builders;

            public NativeList<Entity> lodGroupEntities;

            public void Execute(int index)
            {
                int instanceID = ids[index].value;
                Entity entity = entityArray[index];

                MeshInstanceRendererBuilder builder;
                builder.isStatic = isStatic;
                builder.instanceID = instanceID;
                builder.startRendererDefinitionIndex = 0;
                builder.rendererDefinitionCount = 0;
                builder.rendererCount = 0;
                builder.startRendererIndex = 0;
                builder.startLODGroupIndex = 0;
                builder.lodGroupCount = 0;

                builders.Add(entity, builder);

                if (prefabs.TryGetValue(instanceID, out var prefab) && prefab.Value.objects.Length > 0)
                    lodGroupEntities.Add(entity);
            }
        }

        [BurstCompile]
        private struct CollectToCreateEx : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceStatic> staticType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceRendererID> idType;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Reader prefabs;

            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Writer builders;

            public NativeList<Entity> lodGroupEntities;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectToCreate collectToCreate;
                collectToCreate.isStatic = chunk.Has(ref staticType);
                collectToCreate.entityArray = chunk.GetNativeArray(entityType);
                collectToCreate.ids = chunk.GetNativeArray(ref idType);
                collectToCreate.prefabs = prefabs;
                collectToCreate.builders = builders;
                collectToCreate.lodGroupEntities = lodGroupEntities;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    collectToCreate.Execute(i);
            }
        }

        private struct Build
        {
            public int maxRendererDefinitionCount;
            public UnsafeParallelHashMap<Key, int> indices;
            public UnsafeListEx<Counter> staticCounters;
            public UnsafeListEx<Counter> dynamicCounters;
            public UnsafeListEx<Result> results;

            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Writer builders;

            [ReadOnly]
            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Reader prefabBuilders;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Reader prefabs;

            [ReadOnly]
            public ComponentLookup<MeshInstanceRendererData> instances;

            [ReadOnly]
            public ComponentLookup<MeshStreamingVertexOffset> meshStreamingVertexOffsets;

            public void Execute()
            {
                using (var keyValueArray = builders.GetKeyValueArrays(Allocator.Temp))
                {
                    UnsafeListEx<Counter> counters;
                    Counter counter;
                    MeshInstanceRendererPrefabBuilder prefabBuilder;
                    Result result;
                    Key key;
                    int maxRendererDefinitionCount = this.maxRendererDefinitionCount, 
                        length = keyValueArray.Length, 
                        totalRendererDefinitionCount, 
                        rendererDefinitionCount, 
                        normalRendererCount, 
                        loadRendererCount, 
                        lodGroupCount, 
                        startLODGroupIndex, 
                        index, i, j;
                    for (i = 0; i < length; ++i)
                    {
                        result.entity = keyValueArray.Keys[i];
                        if (meshStreamingVertexOffsets.HasComponent(result.entity))
                        {
                            result.meshStreamingVertexOffset = meshStreamingVertexOffsets[result.entity].value;
                            if (result.meshStreamingVertexOffset == uint.MaxValue)
                                continue;
                        }
                        else
                            result.meshStreamingVertexOffset = uint.MaxValue;

                        result.builder = keyValueArray.Values[i];

                        ref var prefab = ref prefabs[result.builder.instanceID].Value;

                        ref var definition = ref instances[result.entity].definition.Value;

                        totalRendererDefinitionCount = definition.nodes.Length;

                        if (prefabBuilders.TryGetValue(result.builder.instanceID, out prefabBuilder))
                        {
                            rendererDefinitionCount = prefabBuilder.startRendererDefinitionIndex + prefabBuilder.rendererDefinitionCount;
                            lodGroupCount = prefabBuilder.startLODGroupIndex + prefabBuilder.lodGroupCount;
                        }
                        else
                        {
                            rendererDefinitionCount = totalRendererDefinitionCount;
                            lodGroupCount = prefab.objects.Length;
                        }

                        counters = result.builder.isStatic ? staticCounters : dynamicCounters;

                        key.isStatic = result.builder.isStatic;
                        key.instanceID = result.builder.instanceID;

                        result.builder.startRendererDefinitionIndex += result.builder.rendererDefinitionCount;

                        result.builder.rendererDefinitionCount = MeshInstanceRendererUtility.EntityCountOf(
                            ref definition.nodes,
                            result.builder.startRendererDefinitionIndex,
                            math.min(rendererDefinitionCount - result.builder.startRendererDefinitionIndex, maxRendererDefinitionCount),
                            out normalRendererCount,
                            out loadRendererCount);

                        maxRendererDefinitionCount -= result.builder.rendererDefinitionCount;

                        result.builder.startRendererIndex += result.builder.rendererCount;
                        result.builder.rendererCount = normalRendererCount + loadRendererCount;

                        result.builder.startLODGroupIndex += result.builder.lodGroupCount;

                        if (result.builder.startRendererDefinitionIndex + result.builder.rendererDefinitionCount < totalRendererDefinitionCount)
                        {
                            if (loadRendererCount > 0)
                            {
                                result.builder.lodGroupCount = result.builder.startLODGroupIndex - 1;

                                MeshInstanceRendererUtility.GetMaxLODGroupIndex(
                                    ref definition.objects,
                                    ref definition.nodes,
                                    result.builder.startRendererDefinitionIndex,
                                    result.builder.rendererDefinitionCount,
                                    ref result.builder.lodGroupCount);

                                result.builder.lodGroupCount = result.builder.lodGroupCount + 1 - result.builder.startLODGroupIndex;
                            }
                            else
                                result.builder.lodGroupCount = 0;

                            builders[result.entity] = result.builder;
                        }
                        else
                        {
                            result.builder.lodGroupCount = lodGroupCount - result.builder.startLODGroupIndex;

                            builders.Remove(result.entity);
                        }

                        if (result.builder.rendererCount > 0 || result.builder.lodGroupCount > 0)
                        {
                            for (j = 0; j < result.builder.rendererCount; ++j)
                            {
                                key.index = result.builder.startRendererIndex + j;

                                if (indices.TryGetValue(key, out index))
                                    ++counters.ElementAt(index).count;
                                else
                                {
                                    counter.startIndex = 0;
                                    counter.count = 1;

                                    index = counters.length;
                                    counters.Add(counter);

                                    indices[key] = index;
                                }
                            }

                            result.prefabRendererCount = prefabs[result.builder.instanceID].Value.nodes.Length;

                            startLODGroupIndex = result.prefabRendererCount + result.builder.startLODGroupIndex;
                            for (j = 0; j < result.builder.lodGroupCount; ++j)
                            {
                                key.index = startLODGroupIndex + j;

                                if (indices.TryGetValue(key, out index))
                                    ++counters.ElementAt(index).count;
                                else
                                {
                                    counter.startIndex = 0;
                                    counter.count = 1;

                                    index = counters.length;
                                    counters.Add(counter);

                                    indices[key] = index;
                                }
                            }

                            results.Add(result);
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct InitEntitiesEx : IJobParallelFor
        {
            [ReadOnly]
            public UnsafeListEx<Result> results;

            [ReadOnly]
            public UnsafeParallelHashMap<Key, int> indices;

            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> instanceEntities;

            public UnsafeListEx<Counter> staticCounters;
            public UnsafeListEx<Counter> dynamicCounters;

            [NativeDisableParallelForRestriction]
            public BufferLookup<MeshInstanceNode> renderers;
            [NativeDisableParallelForRestriction]
            public BufferLookup<MeshInstanceObject> lodGroups;

            public void Execute(int index)
            {
                ref var result = ref results.ElementAt(index);

                Key key;
                key.isStatic = result.builder.isStatic;
                key.instanceID = result.builder.instanceID;

                var counters = result.builder.isStatic ? staticCounters : dynamicCounters;

                if (result.builder.rendererCount > 0)
                {
                    var renderers = this.renderers[result.entity].Reinterpret<Entity>();
                    UnityEngine.Assertions.Assert.AreEqual(renderers.Length, result.builder.startRendererIndex);
                    renderers.ResizeUninitialized(result.builder.startRendererIndex + result.builder.rendererCount);

                    for (int i = 0; i < result.builder.rendererCount; ++i)
                    {
                        key.index = result.builder.startRendererIndex + i;

                        ref var counter = ref counters.ElementAt(indices[key]);

                        renderers[result.builder.startRendererIndex + i] = instanceEntities[counter.Pop()];
                    }
                }

                if (result.builder.lodGroupCount > 0)
                {
                    var lodGroups = this.lodGroups[result.entity].Reinterpret<Entity>();
                    UnityEngine.Assertions.Assert.AreEqual(lodGroups.Length, result.builder.startLODGroupIndex);
                    lodGroups.ResizeUninitialized(result.builder.startLODGroupIndex + result.builder.lodGroupCount);

                    int startLODGroupIndex = result.prefabRendererCount + result.builder.startLODGroupIndex;
                    for (int i = 0; i < result.builder.lodGroupCount; ++i)
                    {
                        key.index = startLODGroupIndex + i;

                        ref var counter = ref counters.ElementAt(indices[key]);

                        lodGroups[result.builder.startLODGroupIndex + i] = instanceEntities[counter.Pop()];
                    }
                }
            }
        }

        [BurstCompile]
        private struct ReplaceEntities : IJob
        {
            [ReadOnly]
            public UnsafeListEx<Result> results;

            [ReadOnly]
            public BufferLookup<MeshInstanceNode> renderers;
            [ReadOnly]
            public BufferLookup<MeshInstanceObject> lodGroups;

            public BufferLookup<MeshInstanceLODChild> children;
            public ComponentLookup<MeshLODComponent> meshLODComponents;
            public ComponentLookup<MeshLODGroupComponent> meshLODGroupComponents;
            public ComponentLookup<MeshInstanceLODParent> parents;

            [ReadOnly]
            public ComponentLookup<MeshInstanceLODParentIndex> parentIndices;

            public void Execute(in Entity entity, in DynamicBuffer<Entity> objects)
            {
                if (!parentIndices.HasComponent(entity))
                    return;

                Entity parentEntity = objects[parentIndices[entity].value];
                var parent = parents[entity];
                var children = this.children[parentEntity];
                int numChildren = children.Length;
                if(numChildren <= parent.childIndex)
                {
                    var prefabChildren = this.children[parent.entity];
                    int numPrefabChildren = prefabChildren.Length;

                    UnityEngine.Assertions.Assert.IsTrue(numPrefabChildren > numChildren);
                    children.ResizeUninitialized(numPrefabChildren);
                    for (int i = numChildren; i < numPrefabChildren; ++i)
                        children[i] = prefabChildren[i];
                }

                var child = children[parent.childIndex];
                child.entity = entity;
                children[parent.childIndex] = child;

                parent.entity = parentEntity;
                parents[entity] = parent;

                if (meshLODComponents.HasComponent(entity))
                {
                    var meshLODComponent = meshLODComponents[entity];
                    meshLODComponent.Group = parentEntity;
                    meshLODComponents[entity] = meshLODComponent;
                }
                else
                {
                    var meshLODGroupComponent = meshLODGroupComponents[entity];
                    meshLODGroupComponent.ParentGroup = parentEntity;
                    meshLODGroupComponents[entity] = meshLODGroupComponent;
                }

                Execute(parentEntity, objects);
            }

            public void Execute(int index)
            {
                ref var result = ref results.ElementAt(index);

                if (!this.lodGroups.HasBuffer(result.entity))
                    return;

                var lodGroups = this.lodGroups[result.entity].Reinterpret<Entity>();
                var renderers = this.renderers[result.entity].Reinterpret<Entity>();

                for (int i = 0; i < result.builder.rendererCount; ++i)
                    Execute(renderers[result.builder.startRendererIndex + i], lodGroups);
            }

            public void Execute()
            {
                int length = results.length;
                for (int i = 0; i < length; ++i)
                    Execute(i);
            }
        }

        [BurstCompile]
        private struct SetParents : IJobParallelFor
        {
            [ReadOnly]
            public UnsafeListEx<Result> results;

            [ReadOnly]
            public BufferLookup<MeshInstanceNode> renderers;
            [ReadOnly]
            public BufferLookup<MeshInstanceObject> lodGroups;
            [ReadOnly]
            public BufferLookup<EntityParent> entityParents;
            [ReadOnly]
            public ComponentLookup<MeshInstanceTransform> transforms;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToParent> localToParents;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<Parent> parents;
            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshStreamingVertexOffset> meshStreamingVertexOffsets;

            public void Execute(int index)
            {
                ref var result = ref results.ElementAt(index);

                Entity entity;
                Parent parent;
                parent.Value = EntityParent.Get(result.entity, entityParents, localToWorlds);

                var matrix = transforms.HasComponent(result.entity) ? transforms[result.entity].matrix : float4x4.identity;

                LocalToWorld localToWorld;
                LocalToParent localToParent;
                if (result.builder.rendererCount > 0)
                {
                    var renderers = this.renderers[result.entity].Reinterpret<Entity>();
                    for (int i = 0; i < result.builder.rendererCount; ++i)
                    {
                        entity = renderers[result.builder.startRendererIndex + i];

                        parents[entity] = parent;

                        localToWorld.Value = math.mul(matrix, localToWorlds[entity].Value);

                        localToWorlds[entity] = localToWorld;

                        localToParent.Value = localToWorld.Value;

                        localToParents[entity] = localToParent;
                    }

                    if(result.meshStreamingVertexOffset != uint.MaxValue)
                    {
                        MeshStreamingVertexOffset meshStreamingVertexOffset;
                        for (int i = 0; i < result.builder.rendererCount; ++i)
                        {
                            entity = renderers[result.builder.startRendererIndex + i];

                            if (!meshStreamingVertexOffsets.HasComponent(entity))
                                continue;

                            meshStreamingVertexOffset = meshStreamingVertexOffsets[entity];
                            meshStreamingVertexOffset.value += result.meshStreamingVertexOffset;
                            meshStreamingVertexOffsets[entity] = meshStreamingVertexOffset;
                        }
                    }
                }

                if (result.builder.lodGroupCount > 0)
                {
                    var lodGroups = this.lodGroups[result.entity].Reinterpret<Entity>();
                    for (int i = 0; i < result.builder.lodGroupCount; ++i)
                    {
                        entity = lodGroups[result.builder.startLODGroupIndex + i];

                        parents[entity] = parent;

                        localToWorld.Value = math.mul(matrix, localToWorlds[entity].Value);

                        localToWorlds[entity] = localToWorld;

                        localToParent.Value = localToWorld.Value;

                        localToParents[entity] = localToParent;
                    }
                }
            }
        }

        /*private struct Init
        {
            public NativeArray<MeshInstanceTransformSource> transforms;

            public void Execute(int index)
            {
                MeshInstanceTransformSource transform;
                transform.matrix = float4x4.identity;
                transforms[index] = transform;
            }
        }

        [BurstCompile]
        private struct InitEx : IJobChunk
        {
            public ComponentTypeHandle<MeshInstanceTransformSource> transformType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask, int _)
            {
                if (!batchInChunk.Has(transformType))
                    return;

                Init init;
                init.transforms = batchInChunk.GetNativeArray(transformType);

                int count = batchInChunk.Count;
                for (int i = 0; i < count; ++i)
                    init.Execute(i);
            }
        }*/

        [BurstCompile]
        private struct DisposeAll : IJob
        {
            public UnsafeParallelHashMap<Key, int> indices;
            public UnsafeListEx<Counter> staticCounters;
            public UnsafeListEx<Counter> dynamicCounters;

            public void Execute()
            {
                indices.Dispose();
                staticCounters.Dispose();
                dynamicCounters.Dispose();
            }
        }

        [BurstCompile]
        private struct DisposeResults : IJob
        {
            public UnsafeListEx<Result> values;

            public void Execute()
            {
                values.Dispose();
            }
        }

        private struct ChangeMeshStreamingOffsets
        {
            [ReadOnly]
            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader builders;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<MeshInstanceRendererData> instances;

            [ReadOnly]
            public NativeArray<MeshStreamingVertexOffset> inputs;

            [ReadOnly]
            public BufferAccessor<MeshInstanceNode> renderers;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshStreamingVertexOffset> outputs;

            public void Execute(int index)
            {
                ref var definition = ref instances[index].definition.Value;

                int rendererDefinitionCount;
                Entity entity = entityArray[index];
                if (builders.TryGetValue(entity, out var builder))
                    rendererDefinitionCount = builder.startRendererDefinitionIndex + builder.rendererDefinitionCount;
                else
                    rendererDefinitionCount = definition.nodes.Length;

                int i, j, rendererCount, rendererIndex = 0;
                uint input = inputs[index].value;
                var renderers = this.renderers[index];
                MeshStreamingVertexOffset output;
                for (i = 0; i < rendererDefinitionCount; ++i)
                {
                    ref var node = ref definition.nodes[i];
                    rendererCount = math.max(node.lods.Length, 1);

                    if (node.meshStreamingOffset != -1)
                    {
                        output.value = input + (uint)node.meshStreamingOffset;

                        for(j = 0; j < rendererCount; ++j)
                        {
                            entity = renderers[rendererIndex + j].entity;

                            if (outputs.HasComponent(entity))
                                outputs[entity] = output;
                        }
                    }

                    rendererIndex += rendererCount;
                }
            }
        }

        [BurstCompile]
        private struct ChangeMeshStreamingOffsetsEx : IJobChunk
        {
            [ReadOnly]
            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader builders;

            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceRendererData> instanceType;

            [ReadOnly]
            public ComponentTypeHandle<MeshStreamingVertexOffset> inputType;

            [ReadOnly]
            public BufferTypeHandle<MeshInstanceNode> rendererType;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshStreamingVertexOffset> outputs;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                ChangeMeshStreamingOffsets changeMeshStreamingOffsets;
                changeMeshStreamingOffsets.builders = builders;
                changeMeshStreamingOffsets.entityArray = chunk.GetNativeArray(entityType);
                changeMeshStreamingOffsets.instances = chunk.GetNativeArray(ref instanceType);
                changeMeshStreamingOffsets.inputs = chunk.GetNativeArray(ref inputType);
                changeMeshStreamingOffsets.renderers = chunk.GetBufferAccessor(ref rendererType);
                changeMeshStreamingOffsets.outputs = outputs;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    changeMeshStreamingOffsets.Execute(i);
            }
        }

        public static readonly int InnerloopBatchCount = 1;
        public int maxRendererDefinitionCount;

        private EntityQuery __groupToDestroy;
        private EntityQuery __groupToCreate;
        private EntityQuery __groupToChange;

        private SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>> __prefabs;

        private SharedHashMap<int, MeshInstanceRendererPrefabBuilder> __prefabBuilders;

        public SharedHashMap<Entity, MeshInstanceRendererBuilder> builders
        {
            get;

            private set;
        }

        public void OnCreate(ref SystemState state)
        {
            BurstUtility.InitializeJobParallelFor<InitEntitiesEx>();
            BurstUtility.InitializeJob<ReplaceEntities>();
            BurstUtility.InitializeJobParallelFor<SetParents>();
            BurstUtility.InitializeJob<DisposeAll>();
            BurstUtility.InitializeJob<DisposeResults>();

            state.SetAlwaysUpdateSystem(true);

#if UNITY_ANDROID || UNITY_IOS
            maxRendererDefinitionCount = 128;
#else
            maxRendererDefinitionCount = 512;
#endif

            __groupToDestroy = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceNode>()
                    },

                    None = new ComponentType[]
                    {
                        typeof(MeshInstanceRendererID)
                    },

                    Options = EntityQueryOptions.IncludeDisabledEntities
                },
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceNode>()
                    },

                    Any = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceRendererDirty>()
                    },

                    Options = EntityQueryOptions.IncludeDisabledEntities
                });

            __groupToCreate = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceRendererID>()
                    },
                    None = new ComponentType[]
                    {
                        typeof(MeshInstanceNode)
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });

            __groupToChange = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshStreamingVertexOffset>(),
                        ComponentType.ReadOnly<MeshInstanceRendererData>(),
                        ComponentType.ReadOnly<MeshInstanceNode>()
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
            __groupToChange.SetChangedVersionFilter(typeof(MeshStreamingVertexOffset));

            ref var factorySystem = ref state.World.GetOrCreateSystemUnmanaged<MeshInstanceFactorySystem>();

            __prefabs = factorySystem.prefabs;

            __prefabBuilders = factorySystem.builders;

            builders = new SharedHashMap<Entity, MeshInstanceRendererBuilder>(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            builders.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;

            var builders = this.builders;
            builders.lookupJobManager.CompleteReadWriteDependency();

            var buildersWriter = this.builders.writer;

            if (!__groupToDestroy.IsEmptyIgnoreFilter)
            {
                using (var entities = new NativeList<Entity>(Allocator.TempJob))
                using (var lodGroupEntities = new NativeList<Entity>(Allocator.TempJob))
                {
                    state.CompleteDependency();

                    CollectToDestroyEx collectToDestroy;
                    collectToDestroy.entityType = state.GetEntityTypeHandle();
                    collectToDestroy.rendererType = state.GetBufferTypeHandle<MeshInstanceNode>(true);
                    collectToDestroy.lodGroupType = state.GetBufferTypeHandle<MeshInstanceObject>(true);
                    collectToDestroy.builders = buildersWriter;
                    collectToDestroy.entities = entities;
                    collectToDestroy.lodGroupEntities = lodGroupEntities;

                    collectToDestroy.Run(__groupToDestroy);

                    entityManager.DestroyEntity(entities.AsArray());
                    entityManager.RemoveComponent<MeshInstanceObject>(lodGroupEntities.AsArray());
                }

                entityManager.RemoveComponent<MeshInstanceNode>(__groupToDestroy);
            }

            var prefabs = __prefabs.reader;
            if (!__groupToCreate.IsEmptyIgnoreFilter)
            {
                using (var entities = new NativeList<Entity>(Allocator.TempJob))
                {
                    __prefabs.lookupJobManager.CompleteReadOnlyDependency();

                    state.CompleteDependency();

                    CollectToCreateEx collectToCreate;
                    collectToCreate.entityType = state.GetEntityTypeHandle();
                    collectToCreate.staticType = state.GetComponentTypeHandle<MeshInstanceStatic>(true);
                    collectToCreate.idType = state.GetComponentTypeHandle<MeshInstanceRendererID>(true);
                    collectToCreate.prefabs = prefabs;
                    collectToCreate.builders = buildersWriter;
                    collectToCreate.lodGroupEntities = entities;
                    collectToCreate.Run(__groupToCreate);

                    entityManager.AddComponent<MeshInstanceNode>(__groupToCreate);

                    entityManager.AddComponentBurstCompatible<MeshInstanceObject>(entities.AsArray());
                }
            }

            BufferTypeHandle<MeshInstanceNode> rendererType;
            ComponentLookup<MeshStreamingVertexOffset> meshStreamingVertexOffsets;

            var jobHandle = state.Dependency;
            JobHandle? dependency = null;
            if (!buildersWriter.isEmpty)
            {
                __prefabs.lookupJobManager.CompleteReadOnlyDependency();

                __prefabBuilders.lookupJobManager.CompleteReadOnlyDependency();

                state.CompleteDependency();

                var indices = new UnsafeParallelHashMap<Key, int>(1, Allocator.TempJob);
                var staticCounters = new UnsafeListEx<Counter>(Allocator.TempJob);
                var dynamicCounters = new UnsafeListEx<Counter>(Allocator.TempJob);
                var results = new UnsafeListEx<Result>(Allocator.TempJob);

                Build build;
                build.maxRendererDefinitionCount = maxRendererDefinitionCount;
                build.indices = indices;
                build.staticCounters = staticCounters;
                build.dynamicCounters = dynamicCounters;
                build.results = results;
                build.builders = buildersWriter;
                build.prefabBuilders = __prefabBuilders.reader;
                build.prefabs = prefabs;
                build.instances = state.GetComponentLookup<MeshInstanceRendererData>(true);
                build.meshStreamingVertexOffsets = state.GetComponentLookup<MeshStreamingVertexOffset>(true);
                build.Execute();

                int instanceEntityCount = 0, numCounters = staticCounters.length;
                for (int i = 0; i < numCounters; ++i)
                {
                    ref var staticCounter = ref staticCounters.ElementAt(i);

                    staticCounter.startIndex = instanceEntityCount;

                    instanceEntityCount += staticCounter.count;
                }

                int staticInstanceEntityCount = instanceEntityCount;

                numCounters = dynamicCounters.length;
                for (int i = 0; i < numCounters; ++i)
                {
                    ref var dynamicCounter = ref dynamicCounters.ElementAt(i);

                    dynamicCounter.startIndex = instanceEntityCount;

                    instanceEntityCount += dynamicCounter.count;
                }

                var instanceEntities = new NativeArray<Entity>(instanceEntityCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                int prefabRendererCount;
                Counter counter;
                Key key;
                KeyValue<Key, int> keyValue;
                var enumerator = indices.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    keyValue = enumerator.Current;
                    key = keyValue.Key;

                    ref var prefab = ref prefabs[key.instanceID].Value;

                    prefabRendererCount = prefab.nodes.Length;

                    counter = (key.isStatic ? staticCounters : dynamicCounters)[keyValue.Value];

                    entityManager.Instantiate(
                        key.index < prefabRendererCount ? prefab.nodes[key.index] : prefab.objects[key.index - prefabRendererCount],
                        instanceEntities.GetSubArray(counter.startIndex, counter.count));
                }

                entityManager.AddComponentBurstCompatible<Static>(instanceEntities.GetSubArray(0, staticInstanceEntityCount));

                var dynamicInstanceEntities = instanceEntities.GetSubArray(staticInstanceEntityCount, instanceEntityCount - staticInstanceEntityCount);
                entityManager.AddComponentBurstCompatible<LocalToParent>(dynamicInstanceEntities);
                entityManager.AddComponentBurstCompatible<Parent>(dynamicInstanceEntities);

                /*entityManager.AddComponent<ChunkHeader>(instanceEntities);
                ChunkHeader chunkHeader;
                Entity entity;
                for (int i = 0; i < instanceEntityCount; ++i)
                {
                    entity = instanceEntities[i];
                    chunkHeader.ArchetypeChunk = entityManager.GetChunk(entity);

                    entityManager.SetComponentData(entity, chunkHeader);
                }*/

                meshStreamingVertexOffsets = state.GetComponentLookup<MeshStreamingVertexOffset>();
                rendererType = state.GetBufferTypeHandle<MeshInstanceNode>(true);

                var renderers = state.GetBufferLookup<MeshInstanceNode>();
                var lodGroups = state.GetBufferLookup<MeshInstanceObject>();

                var renderersReadOnly = state.GetBufferLookup<MeshInstanceNode>(true);
                var lodGroupsReadOnly = state.GetBufferLookup<MeshInstanceObject>(true);

                int numResults = results.length;

                InitEntitiesEx initEntities;
                initEntities.results = results;
                initEntities.indices = indices;
                initEntities.instanceEntities = instanceEntities;
                initEntities.staticCounters = staticCounters;
                initEntities.dynamicCounters = dynamicCounters;
                initEntities.renderers = renderers;
                initEntities.lodGroups = lodGroups;
                jobHandle = initEntities.Schedule(numResults, InnerloopBatchCount, jobHandle);

                DisposeAll disposeAll;
                disposeAll.indices = indices;
                disposeAll.staticCounters = staticCounters;
                disposeAll.dynamicCounters = dynamicCounters;
                var result = disposeAll.Schedule(jobHandle);

                ReplaceEntities replaceEntities;
                replaceEntities.results = results;
                replaceEntities.renderers = renderersReadOnly;
                replaceEntities.lodGroups = lodGroupsReadOnly;
                replaceEntities.children = state.GetBufferLookup<MeshInstanceLODChild>();
                replaceEntities.meshLODComponents = state.GetComponentLookup<MeshLODComponent>();
                replaceEntities.meshLODGroupComponents = state.GetComponentLookup<MeshLODGroupComponent>();
                replaceEntities.parents = state.GetComponentLookup<MeshInstanceLODParent>();
                replaceEntities.parentIndices = state.GetComponentLookup<MeshInstanceLODParentIndex>(true);
                var temp = replaceEntities.Schedule(jobHandle);

                SetParents setParents;
                setParents.results = results;
                setParents.renderers = renderersReadOnly;
                setParents.lodGroups = lodGroupsReadOnly;
                setParents.entityParents = state.GetBufferLookup<EntityParent>(true);
                setParents.transforms = state.GetComponentLookup<MeshInstanceTransform>(true);
                setParents.localToWorlds = state.GetComponentLookup<LocalToWorld>();
                setParents.localToParents = state.GetComponentLookup<LocalToParent>();
                setParents.parents = state.GetComponentLookup<Parent>();
                setParents.meshStreamingVertexOffsets = meshStreamingVertexOffsets;
                temp = JobHandle.CombineDependencies(temp, setParents.Schedule(numResults, InnerloopBatchCount, jobHandle));

                DisposeResults disposeResults;
                disposeResults.values = results;
                temp = disposeResults.Schedule(temp);

                dependency = JobHandle.CombineDependencies(result, temp);
            }
            else
            {
                meshStreamingVertexOffsets = state.GetComponentLookup<MeshStreamingVertexOffset>();
                rendererType = state.GetBufferTypeHandle<MeshInstanceNode>(true);
            }

            ChangeMeshStreamingOffsetsEx changeMeshStreamingOffsets;
            changeMeshStreamingOffsets.builders = builders.reader;
            changeMeshStreamingOffsets.entityType = state.GetEntityTypeHandle();
            changeMeshStreamingOffsets.instanceType = state.GetComponentTypeHandle<MeshInstanceRendererData>(true);
            changeMeshStreamingOffsets.inputType = state.GetComponentTypeHandle<MeshStreamingVertexOffset>(true);
            changeMeshStreamingOffsets.rendererType = rendererType;
            changeMeshStreamingOffsets.outputs = meshStreamingVertexOffsets;

            jobHandle = changeMeshStreamingOffsets.ScheduleParallel(__groupToChange, jobHandle);

            builders.lookupJobManager.AddReadOnlyDependency(jobHandle);

            if (dependency != null)
                jobHandle = JobHandle.CombineDependencies(jobHandle, dependency.Value);

            state.Dependency = jobHandle;
        }
    }

    [BurstCompile, UpdateInGroup(typeof(InitializationSystemGroup)), UpdateAfter(typeof(EntityObjectSystemGroup))]
    public partial struct MeshInstanceRendererInitSystem : ISystem
    {
        //private EntityQuery __groupToInit;
        private EntityQuery __groupToDirty;

        public void OnCreate(ref SystemState state)
        {
            /*__groupToInit = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceRendererInit>()
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });*/

            __groupToDirty = state.GetEntityQuery(new EntityQueryDesc()
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<MeshInstanceRendererDirty>(),
                },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            //entityManager.RemoveComponent<MeshInstanceRendererInit>(__groupToInit);
            entityManager.RemoveComponent<MeshInstanceRendererDirty>(__groupToDirty);
        }
    }

    /*[UpdateInGroup(typeof(MeshInstanceSystemGroup), OrderLast = true)]
    public partial class MeshInstanceMaterialPropertyTypeInitSystem : SystemBase
    {
        private struct MaterialPropertyType
        {
            public ComponentType componentType;
            public string[] keywordMasks;
        }

        private struct Key : IEquatable<Key>
        {
            public int materialIndex;
            public ComponentType componentType;

            public override int GetHashCode()
            {
                return materialIndex ^ componentType.GetHashCode();
            }

            public bool Equals(Key other)
            {
                return materialIndex == other.materialIndex && componentType == other.componentType;
            }
        }

        private struct Value
        {
            public int startIndex;
            public int count;
        }

        [BurstCompile]
        private struct Build : IJob
        {
            [ReadOnly]
            public EntityTypeHandle entityType;
            [ReadOnly]
            public SharedComponentTypeHandle<RenderMesh> renderMeshType;

            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<ArchetypeChunk> chunks;
            [ReadOnly]
            public NativeParallelHashMap<int, int> renderMeshMaterialIndices;
            [ReadOnly]
            public NativeParallelMultiHashMap<int, ComponentType> materialComponentTypes;

            public NativeParallelMultiHashMap<ComponentType, Entity> entities;

            public void Execute()
            {
                entities.Clear();

                int numChunks = chunks.Length, numEntities, materialIndex, i, j;
                NativeParallelMultiHashMapIterator<int> iterator;
                ComponentType componentType;
                ArchetypeChunk chunk;
                NativeArray<Entity> entityArray;
                for (i = 0; i < numChunks; ++i)
                {
                    chunk = chunks[i];
                    materialIndex = renderMeshMaterialIndices[chunk.GetSharedComponentIndex(renderMeshType)];
                    if (materialComponentTypes.TryGetFirstValue(materialIndex, out componentType, out iterator))
                    {
                        entityArray = chunk.GetNativeArray(entityType);

                        numEntities = entityArray.Length;
                        do
                        {
                            for (j = 0; j < numEntities; ++j)
                                entities.Add(componentType, entityArray[j]);

                        } while (materialComponentTypes.TryGetNextValue(out componentType, ref iterator));
                    }
                }
            }
        }

        [BurstCompile]
        private struct Init : IJobChunk
        {
            public ComponentType componentType;
            public DynamicComponentTypeHandle type;
            [ReadOnly]
            public SharedComponentTypeHandle<RenderMesh> renderMeshType;
            [ReadOnly]
            public NativeArray<float> values;
            [ReadOnly]
            public NativeParallelHashMap<Key, Value> ranges;
            [ReadOnly]
            public NativeParallelHashMap<int, int> renderMeshMaterialIndices;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Key key;
                key.materialIndex = renderMeshMaterialIndices[chunk.GetSharedComponentIndex(renderMeshType)];
                key.componentType = componentType;
                if (ranges.TryGetValue(key, out var value))
                {
                    int count = chunk.Count, i;
                    switch (value.count)
                    {
                        case 1:
                            var floatValue = values[value.startIndex];
                            var floats = chunk.GetDynamicComponentDataArrayReinterpret<float>(ref type, 4);
                            for (i = 0; i < count; ++i)
                                floats[i] = floatValue;
                            break;
                        case 4:
                            var vectorValue = values.Slice(value.startIndex, value.count).SliceConvert<float4>()[0];
                            var vectors = chunk.GetDynamicComponentDataArrayReinterpret<float4>(ref type, 4 * 4);
                            for (i = 0; i < count; ++i)
                                vectors[i] = vectorValue;
                            break;
                    }
                }
            }
        }

        private int __materialCount;
        private EntityQuery __group;
        private NativeList<float> __values;
        private NativeParallelHashMap<Key, Value> __ranges;
        private NativeParallelHashMap<int, int> __renderMeshMaterialIndices;
        private NativeParallelMultiHashMap<ComponentType, Entity> __entities;
        private NativeParallelMultiHashMap<int, ComponentType> __materialComponentTypes;
        private Dictionary<Material, int> __materialIndices;
        private Dictionary<string, MaterialPropertyType> __materialPropertyTypes;

        protected override void OnCreate()
        {
            base.OnCreate();

            __group = GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceRendererInit>(),
                        ComponentType.ReadOnly<RenderMesh>()
                    },
                    //Options = EntityQueryOptions.IncludePrefab
                });

            __values = new NativeList<float>(Allocator.Persistent);
            __ranges = new NativeParallelHashMap<Key, Value>(1, Allocator.Persistent);
            __renderMeshMaterialIndices = new NativeParallelHashMap<int, int>(1, Allocator.Persistent);
            __entities = new NativeParallelMultiHashMap<ComponentType, Entity>(1, Allocator.Persistent);
            __materialComponentTypes = new NativeParallelMultiHashMap<int, ComponentType>(1, Allocator.Persistent);
            __materialIndices = new Dictionary<Material, int>();

            __materialPropertyTypes = new Dictionary<string, MaterialPropertyType>();

            Type type;
            MaterialPropertyType materialPropertyType;
            MeshInstanceMaterialPropertyAttribute materialPropertyAttribute;
            object[] attributes;
            foreach (var typeInfo in TypeManager.AllTypes)
            {
                type = typeInfo.Type;
                if (typeof(IComponentData).IsAssignableFrom(type))
                {
                    attributes = type.GetCustomAttributes(typeof(MeshInstanceMaterialPropertyAttribute), false);
                    if (attributes.Length > 0)
                    {
                        materialPropertyAttribute = (MeshInstanceMaterialPropertyAttribute)attributes[0];
                        materialPropertyType.componentType = type;
                        materialPropertyType.keywordMasks = materialPropertyAttribute.keywordMasks;
                        __materialPropertyTypes.Add(materialPropertyAttribute.Name, materialPropertyType);
                    }
                }
            }
        }

        protected override void OnDestroy()
        {
            __values.Dispose();
            __ranges.Dispose();
            __renderMeshMaterialIndices.Dispose();
            __entities.Dispose();
            __materialComponentTypes.Dispose();

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var entityManager = EntityManager;
            var renderMeshType = GetSharedComponentTypeHandle<RenderMesh>();
            var chunks = __group.ToArchetypeChunkArray(Allocator.TempJob);
            {
                bool isMask;
                int propertyIndex, renderMeshIndex;
                ArchetypeChunk chunk;
                Key key;
                Value value;
                MaterialPropertyType materialPropertyType;
                RenderMesh renderMesh;
                Shader shader;

                value.startIndex = 0;
                value.count = 0;
                int numChunks = chunks.Length;
                for (int i = 0; i < numChunks; ++i)
                {
                    chunk = chunks[i];
                    renderMeshIndex = chunk.GetSharedComponentIndex(renderMeshType);
                    renderMesh = entityManager.GetSharedComponentManaged<RenderMesh>(renderMeshIndex);

                    if (!__materialIndices.TryGetValue(renderMesh.material, out key.materialIndex))
                    {
                        shader = renderMesh.material == null ? null : renderMesh.material.shader;
                        if (shader == null)
                            continue;

                        key.materialIndex = __materialCount++;

                        foreach (var pair in __materialPropertyTypes)
                        {
                            propertyIndex = shader.FindPropertyIndex(pair.Key);
                            if (propertyIndex == -1)
                                continue;

                            materialPropertyType = pair.Value;
                            if (materialPropertyType.keywordMasks != null &&
                                materialPropertyType.keywordMasks.Length >= 0)
                            {
                                isMask = false;
                                foreach (var keywordMask in materialPropertyType.keywordMasks)
                                {
                                    if (renderMesh.material.IsKeywordEnabled(keywordMask))
                                    {
                                        isMask = true;
                                        break;
                                    }
                                }

                                if (isMask)
                                    continue;
                            }

                            switch (shader.GetPropertyType(propertyIndex))
                            {
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    value.startIndex = __values.Length;
                                    value.count = 1;

                                    __values.Add(renderMesh.material.GetFloat(shader.GetPropertyNameId(propertyIndex)));
                                    break;
                                case ShaderPropertyType.Vector:
                                case ShaderPropertyType.Color:
                                    value.startIndex = __values.Length;
                                    value.count = 4;

                                    var vector = renderMesh.material.GetVector(shader.GetPropertyNameId(propertyIndex));
                                    __values.Add(vector.x);
                                    __values.Add(vector.y);
                                    __values.Add(vector.z);
                                    __values.Add(vector.w);
                                    break;
                                default:
                                    propertyIndex = -1;
                                    break;
                            }

                            if (propertyIndex == -1)
                                continue;

                            __materialComponentTypes.Add(key.materialIndex, materialPropertyType.componentType);

                            key.componentType = materialPropertyType.componentType;
                            __ranges.Add(key, value);
                        }

                        __materialIndices[renderMesh.material] = key.materialIndex;
                    }

                    __renderMeshMaterialIndices[renderMeshIndex] = key.materialIndex;
                }

                Build build;
                build.entityType = GetEntityTypeHandle();
                build.renderMeshType = renderMeshType;
                build.chunks = chunks;
                build.renderMeshMaterialIndices = __renderMeshMaterialIndices;
                build.materialComponentTypes = __materialComponentTypes;
                build.entities = __entities;

                build.Run();
            }

            using (var componentTypes = __entities.GetKeyArray(Allocator.Temp))
            {
                int numComponentTypes = componentTypes.ConvertToUniqueArray();
                if (numComponentTypes > 0)
                {
                    using (var entities = new NativeList<Entity>(componentTypes.Length, Allocator.Temp))
                    {
                        Entity entity;
                        NativeParallelMultiHashMapIterator<ComponentType> iterator;
                        ComponentType componentType;
                        for (int i = 0; i < numComponentTypes; ++i)
                        {
                            componentType = componentTypes[i];
                            entities.Clear();
                            if (__entities.TryGetFirstValue(componentType, out entity, out iterator))
                            {
                                do
                                {
                                    entities.Add(entity);
                                } while (__entities.TryGetNextValue(out entity, ref iterator));
                            }

                            entityManager.AddComponent(entities.AsArray(), componentType);
                        }
                    }

                    Init init;
                    init.renderMeshType = GetSharedComponentTypeHandle<RenderMesh>();
                    init.values = __values.AsArray();
                    init.ranges = __ranges;
                    init.renderMeshMaterialIndices = __renderMeshMaterialIndices;

                    JobHandle? result = null;
                    JobHandle inputDeps = Dependency, jobHandle;
                    for (int i = 0; i < numComponentTypes; ++i)
                    {
                        init.componentType = componentTypes[i];
                        init.type = GetDynamicComponentTypeHandle(init.componentType);

                        jobHandle = init.ScheduleParallel(__group, inputDeps);
                        if (result == null)
                            result = jobHandle;
                        else
                            result = JobHandle.CombineDependencies(result.Value, jobHandle);
                    }

                    Dependency = result.Value;
                }
            }
            //componentTypes.Dispose();
        }
    }*/
}