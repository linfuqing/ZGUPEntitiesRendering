using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Jobs;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_UNITY_OCCLUSION
using Unity.Rendering.Occlusion;
#endif

namespace ZG
{
    public enum MeshInstanceRendererFlag
    {
        ReceiveShadows = 0x01,
        StaticShadowCaster = 0x02,
        AllowOcclusionWhenDynamic = 0x04, 
        DepthSorted = 0x08,
    }

    public struct MeshInstanceRendererPrefabBuilder
    {
        public int rendererDefinitionCount;
        public int startRendererDefinitionIndex;
        public int startRendererIndex;
        public int normalRendererCount;
        public int lodRendererCount;
        public int lodGroupCount;
        public int startLODGroupIndex;

        public BlobAssetReference<MeshInstanceRendererDefinition> definition;
        public BlobAssetReference<MeshInstanceRendererPrefab> prefab;
    }

    public struct MeshInstanceRendererPrefab
    {
        public int instanceCount;

        public int normalRendererCount;
        public int lodRendererCount;

        public BlobArray<Entity> nodes;
        public BlobArray<Entity> objects;
    }

    public struct MeshInstanceRendererDefinition
    {
        public struct MaterialProperty
        {
            public static readonly int FloatSize = UnsafeUtility.SizeOf<float>();

            public int typeIndex;

            public BlobArray<float> values;

            public void SetComponentData(
                int instanceID,
                in Entity entity,
                in SingletonAssetContainer<TypeIndex>.Reader componentTypeIndices,
                ref EntityComponentAssigner.Writer writer)
            {
                var componentTypeIndex = componentTypeIndices[new SingletonAssetContainerHandle(instanceID, typeIndex)];

                switch (values.Length)
                {
                    case 1:
                        writer.SetComponentData(
                            componentTypeIndex,
                            entity,
                            values[0]);
                        break;
                    case 2:
                        writer.SetComponentData(
                            componentTypeIndex,
                            entity,
                            values.AsArray().Reinterpret<float2>(FloatSize)[0]);
                        break;
                    case 3:
                        writer.SetComponentData(
                            componentTypeIndex,
                            entity,
                            values.AsArray().Reinterpret<float3>(FloatSize)[0]);
                        break;
                    case 4:
                        writer.SetComponentData(
                            componentTypeIndex,
                            entity,
                            values.AsArray().Reinterpret<float4>(FloatSize)[0]);
                        break;
                    case 8:
                        writer.SetComponentData(
                            componentTypeIndex,
                            entity,
                            values.AsArray().Reinterpret<float2x4>(FloatSize)[0]);
                        break;
                    case 16:
                        writer.SetComponentData(
                            componentTypeIndex,
                            entity,
                            values.AsArray().Reinterpret<float4x4>(FloatSize)[0]);
                        break;
                }
            }
        }

        public struct Renderer
        {
            public enum StaticLightingMode
            {
                None,
                LightProbes,
                LightMapped
            }

            public MeshInstanceRendererFlag flag;

            public MotionVectorGenerationMode motionVectorGenerationMode;

            public ShadowCastingMode shadowCastingMode;

            public LightProbeUsage lightProbeUsage;

            public int lightMapIndex;

            public int submeshIndex;

            public int meshIndex;

            public int materialIndex;

            public int layer;

            public uint renderingLayerMask;

            public StaticLightingMode staticLightingMode
            {
                get
                {
                    StaticLightingMode staticLightingMode;
                    if (lightMapIndex >= 65534 || lightMapIndex < 0)
                        staticLightingMode = StaticLightingMode.LightProbes;
                    else if (lightMapIndex >= 0)
                        staticLightingMode = StaticLightingMode.LightMapped;
                    else
                        staticLightingMode = StaticLightingMode.None;

                    return staticLightingMode;
                }
            }

            public RenderFilterSettings renderFilterSettings
            {
                get
                {
                    RenderFilterSettings renderFilterSettings;
                    renderFilterSettings.Layer = layer;
                    renderFilterSettings.RenderingLayerMask = renderingLayerMask;
                    renderFilterSettings.MotionMode = motionVectorGenerationMode;
                    renderFilterSettings.ShadowCastingMode = shadowCastingMode;
                    renderFilterSettings.ReceiveShadows = (flag & MeshInstanceRendererFlag.ReceiveShadows) == MeshInstanceRendererFlag.ReceiveShadows;
                    renderFilterSettings.StaticShadowCaster = (flag & MeshInstanceRendererFlag.StaticShadowCaster) == MeshInstanceRendererFlag.StaticShadowCaster;

                    return renderFilterSettings;
                }
            }
        }

        public struct LOD
        {
            public int objectIndex;
            public int mask;
        }

        public struct Node
        {
            public int rendererIndex;
            public int meshStreamingOffset;
            public int componentTypesIndex;

            public AABB bounds;

            public float4x4 matrix;

            public BlobArray<LOD> lods;
            public BlobArray<int> materialPropertyIndices;
        }

        public struct Object
        {
            public float3 localReferencePoint;

            public float4x4 matrix;

            public LOD parent;

            public BlobArray<float> distances;
        }

        public int instanceID;

        public BlobArray<MaterialProperty> materialProperties;
        public BlobArray<Renderer> renderers;
        public BlobArray<Node> nodes;
        public BlobArray<Object> objects;
    }

    public struct MeshInstanceRendererData : IComponentData
    {
        public BlobAssetReference<MeshInstanceRendererDefinition> definition;
    }

    [BurstCompile]
    public static class MeshInstanceRendererUtility
    {
        private struct DestroyPrefabs
        {
            [ReadOnly]
            public NativeArray<MeshInstanceRendererID> ids;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Writer prefabs;

            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Writer builders;

            public NativeList<Entity> entitiesToDestroy;

            public void Execute(int index)
            {
                int id = ids[index].value;
                var prefabAsset = prefabs[id];
                ref var prefab = ref prefabAsset.Value;
                if (--prefab.instanceCount > 0)
                    return;

                if (builders.TryGetValue(id, out var builder))
                {
                    int renderCount = builder.startRendererIndex + builder.normalRendererCount + builder.lodRendererCount;
                    if (renderCount > 0)
                        entitiesToDestroy.AddRange(prefab.nodes.AsArray().GetSubArray(0, renderCount));

                    int lodGroupCount = builder.startLODGroupIndex + builder.lodGroupCount;
                    if (lodGroupCount > 0)
                        entitiesToDestroy.AddRange(prefab.objects.AsArray().GetSubArray(0, lodGroupCount));

                    builders.Remove(id);
                }
                else
                {
                    int numRenderers = prefab.nodes.Length;
                    if(numRenderers > 0)
                        entitiesToDestroy.AddRange(prefab.nodes.AsArray());

                    int numLODGroups = prefab.objects.Length;
                    if(numLODGroups > 0)
                        entitiesToDestroy.AddRange(prefab.objects.AsArray());
                }

                prefabAsset.Dispose();

                prefabs.Remove(id);
            }
        }

        [BurstCompile]
        private struct DestroyPrefabsEx : IJobChunk
        {
            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceRendererID> idType;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Writer prefabs;

            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Writer builders;

            public NativeList<Entity> entitiesToDestroy;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                DestroyPrefabs destroyPrefabs;
                destroyPrefabs.ids = chunk.GetNativeArray(ref idType);
                destroyPrefabs.prefabs = prefabs;
                destroyPrefabs.builders = builders;
                destroyPrefabs.entitiesToDestroy = entitiesToDestroy;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while(iterator.NextEntityIndex(out int i))
                    destroyPrefabs.Execute(i);
            }
        }

        private struct CollectPrefab
        {
            [ReadOnly]
            public NativeArray<MeshInstanceRendererData> instances;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Writer prefabs;

            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Writer builders;

            public int Execute(int index)
            {
                var instance = instances[index].definition;

                ref var definition = ref instance.Value;

                if (prefabs.TryGetValue(definition.instanceID, out var prefab))
                    ++prefab.Value.instanceCount;
                else
                {
                    using (var blobBuilder = new BlobBuilder(Allocator.Temp))
                    {
                        ref var root = ref blobBuilder.ConstructRoot<MeshInstanceRendererPrefab>();
                        root.instanceCount = 1;

                        EntityCountOf(
                            ref definition.nodes,
                            0,
                            int.MaxValue,
                            out root.normalRendererCount,
                            out root.lodRendererCount);

                        blobBuilder.Allocate(ref root.nodes, root.normalRendererCount + root.lodRendererCount);
                        blobBuilder.Allocate(ref root.objects, definition.objects.Length);

                        MeshInstanceRendererPrefabBuilder builder;
                        builder.rendererDefinitionCount = 0;
                        builder.startRendererDefinitionIndex = 0;
                        builder.startRendererIndex = 0;
                        builder.normalRendererCount = 0;
                        builder.lodRendererCount = 0;
                        builder.lodGroupCount = 0;
                        builder.startLODGroupIndex = 0;
                        builder.definition = instance;
                        builder.prefab = blobBuilder.CreateBlobAssetReference<MeshInstanceRendererPrefab>(Allocator.Persistent);
                        prefabs[definition.instanceID] = builder.prefab;

                        builders.Add(definition.instanceID, builder);
                    }
                }

                return definition.instanceID;
            }
        }

        [BurstCompile]
        private struct CollectPrefabEx : IJobChunk
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<int> baseEntityIndexArray;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceRendererData> instanceType;

            public SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>>.Writer prefabs;

            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Writer builders;

            public NativeArray<MeshInstanceRendererID> ids;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectPrefab collectPrefab;
                collectPrefab.instances = chunk.GetNativeArray(ref instanceType);
                collectPrefab.prefabs = prefabs;
                collectPrefab.builders = builders;

                MeshInstanceRendererID id;

                int index = baseEntityIndexArray[unfilteredChunkIndex];
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                {
                    id.value = collectPrefab.Execute(i);

                    ids[index++] = id;
                }
            }
        }

        private struct Build
        {
            public int maxRendererDefinitionCount;
            public SharedHashMap<int, MeshInstanceRendererPrefabBuilder>.Writer builders;
            public NativeList<MeshInstanceRendererPrefabBuilder> results;

            public void Execute()
            {
                if (builders.isEmpty)
                    return;

                using (var keys = builders.GetKeyArray(Allocator.Temp))
                {
                    MeshInstanceRendererPrefabBuilder result;
                    int maxRendererDefinitionCount = this.maxRendererDefinitionCount, numKeys = keys.Length, key;
                    for(int i = 0; i < numKeys; ++i)
                    {
                        key = keys[i];

                        result = builders[key];

                        result.startRendererDefinitionIndex += result.rendererDefinitionCount;
                        result.startRendererIndex += result.normalRendererCount + result.lodRendererCount;

                        ref var definition = ref result.definition.Value;

                        result.rendererDefinitionCount = EntityCountOf(
                            ref definition.nodes,
                            result.startRendererDefinitionIndex,
                            maxRendererDefinitionCount,
                            out result.normalRendererCount,
                            out result.lodRendererCount);

                        maxRendererDefinitionCount -= result.rendererDefinitionCount;

                        result.startLODGroupIndex += result.lodGroupCount;

                        if (result.startRendererDefinitionIndex + result.rendererDefinitionCount < definition.nodes.Length)
                        {
                            if (result.lodRendererCount > 0)
                            {
                                result.lodGroupCount = result.startLODGroupIndex - 1;

                                GetMaxLODGroupIndex(
                                    ref definition.objects, 
                                    ref definition.nodes,
                                    result.startRendererDefinitionIndex,
                                    result.rendererDefinitionCount,
                                    ref result.lodGroupCount);

                                result.lodGroupCount = result.lodGroupCount + 1 - result.startLODGroupIndex;
                            }
                            else
                                result.lodGroupCount = 0;

                            builders[key] = result;
                        }
                        else
                        {
                            result.lodGroupCount = result.prefab.Value.objects.Length - result.startLODGroupIndex;

                            builders.Remove(key);
                        }

                        results.Add(result);
                    }
                }
            }
        }

        private struct ApplyObjects//: IJobParallelFor
        {
            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshLODGroupComponent> meshLODGroupComponents;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshInstanceLODParentIndex> lodParentIndices;

            public unsafe void Execute(
                int index,
                ref BlobArray<Entity> entityArray,
                ref BlobArray<MeshInstanceRendererDefinition.Object> targets)
            {
                ref var target = ref targets[index];

                Entity entity = entityArray[index];

                LocalToWorld localToWorld;
                localToWorld.Value = target.matrix;
                localToWorlds[entity] = localToWorld;

                MeshLODGroupComponent meshLODGroupComponent;
                meshLODGroupComponent.ParentMask = target.parent.mask;
                meshLODGroupComponent.ParentGroup = target.parent.objectIndex == -1 ? Entity.Null : entityArray[target.parent.objectIndex];

                meshLODGroupComponent.LODDistances0 = float.MaxValue;
                meshLODGroupComponent.LODDistances1 = float.MaxValue;

                int numDistances = target.distances.Length;
                int length = Math.Min(numDistances, 4);
                for (int i = 0; i < length; ++i)
                    meshLODGroupComponent.LODDistances0[i] = target.distances[i];

                length = Math.Min(numDistances, 8);
                for (int i = 4; i < length; ++i)
                    meshLODGroupComponent.LODDistances1[i - 4] = target.distances[i];

                meshLODGroupComponent.LocalReferencePoint = target.localReferencePoint;

                meshLODGroupComponents[entity] = meshLODGroupComponent;

                /*lodGroup.referencePosition = result.localReferencePoint;
                entityManager.SetComponentData(entity, lodGroup);

                lodWorldGroup.referencePosition = math.transform(result.matrix, result.localReferencePoint);
                entityManager.SetComponentData(entity, lodWorldGroup);*/

                if (target.parent.objectIndex != -1)
                {
                    MeshInstanceLODParentIndex lodParentIndex;
                    lodParentIndex.value = target.parent.objectIndex;
                    lodParentIndices[entity] = lodParentIndex;
                }
            }
        }

        private struct ApplyObjectHierarchies
        {
            [NativeDisableContainerSafetyRestriction]
            public BufferLookup<MeshInstanceLODChild> lodChildren;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshInstanceLODParent> lodParents;

            public void Execute(
                int index,
                ref BlobArray<Entity> entityArray,
                ref BlobArray<MeshInstanceRendererDefinition.Object> targets)
            {
                ref var target = ref targets[index];
                if (target.parent.objectIndex == -1)
                    return;

                //ref var parent = ref targets[target.parent.objectIndex];

                MeshInstanceLODParent lodParent;
                lodParent.entity = entityArray[target.parent.objectIndex];

                MeshInstanceLODChild lodChild;
                lodChild.entity = entityArray[index];
                GetDistance(
                    ref target.distances,
                    target.parent.mask,
                    out lodChild.minDistance,
                    out lodChild.maxDistance);

                var lodChildren = this.lodChildren[lodParent.entity];

                lodParent.childIndex = lodChildren.Length;

                lodChildren.Add(lodChild);

                lodParents[lodChild.entity] = lodParent;
            }

            public void Execute(
                int startIndex, 
                int count, 
                ref BlobArray<Entity> entityArray,
                ref BlobArray<MeshInstanceRendererDefinition.Object> targets)
            {
                for (int i = 0; i < count; ++i)
                    Execute(startIndex + i, ref entityArray, ref targets);
            }
        }

        private struct ApplyNodes//: IJobParallelFor
        {
            [ReadOnly]
            public SingletonAssetContainer<MeshInstanceMaterialAsset>.Reader materialAssets;

            [ReadOnly]
            public SingletonAssetContainer<MeshInstanceMeshAsset>.Reader meshAssets;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID>.Reader batchMaterialIDs;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMeshAsset, BatchMeshID>.Reader batchMeshIDs;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MaterialMeshInfo> materialMeshInfos;
#if ENABLE_UNITY_OCCLUSION
            [NativeDisableParallelForRestriction]
            public ComponentLookup<OcclusionTest> occludees;
#endif

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<RenderBounds> renderBounds;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshLODComponent> meshLODComponents;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshInstanceLODParentIndex> lodParentIndices;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshStreamingVertexOffset> meshStreamingVertexOffsets;

            public void Execute(
                int index,
                int instanceID, 
                ref BlobArray<MeshInstanceRendererDefinition.Node> nodes, 
                ref BlobArray<MeshInstanceRendererDefinition.Renderer> renderers,
                ref BlobArray<Entity> objectEntities,
                ref BlobArray<Entity> nodeEntities)
            {
                ref var node = ref nodes[index];
                ref var renderer = ref renderers[node.rendererIndex];

                SingletonAssetContainerHandle materialHandle, meshHandle;
                materialHandle.instanceID = instanceID;
                materialHandle.index = renderer.materialIndex;
                meshHandle.instanceID = instanceID;
                meshHandle.index = renderer.meshIndex;

                var materialMeshInfo = new MaterialMeshInfo(
                    batchMaterialIDs[materialAssets[materialHandle]],
                    batchMeshIDs[meshAssets[meshHandle]],
                    (sbyte)renderer.submeshIndex);

#if ENABLE_UNITY_OCCLUSION
                OcclusionTest occludee = new OcclusionTest(true);
#endif

                LocalToWorld localToWorld;
                localToWorld.Value = node.matrix;

                if (node.bounds.Extents.Equals(float3.zero))
                    node.bounds.Extents = math.float3(math.FLT_MIN_NORMAL, math.FLT_MIN_NORMAL, math.FLT_MIN_NORMAL);

                RenderBounds renderBounds;
                renderBounds.Value = node.bounds;

                MeshStreamingVertexOffset meshStreamingVertexOffset;
                meshStreamingVertexOffset.value = node.meshStreamingOffset < 0 ? uint.MaxValue : (uint)node.meshStreamingOffset;

                int entityStartIndex = EntityStartIndexOf(ref nodes, index), numLODs = node.lods.Length;
                Entity entity = nodeEntities[entityStartIndex];
                if (numLODs > 0)
                {
                    /*entityManager.SetSharedComponentData(entity, node.renderMesh);

                    entityManager.SetComponentData(entity, localToWorld);

                    entityManager.SetComponentData(entity, renderBounds);*/
                    Entity temp;
                    MeshLODComponent meshLODComponent;
                    MeshInstanceLODParentIndex lodParentIndex;
                    for (int i = 1; i < numLODs; ++i)
                    {
                        ref var lod = ref node.lods[i];

                        temp = nodeEntities[entityStartIndex + i];

                        localToWorlds[temp] = localToWorld;

                        this.renderBounds[temp] = renderBounds;

                        meshLODComponent.LODMask = lod.mask;
                        meshLODComponent.Group = objectEntities[lod.objectIndex];
                        //TODO:
                        meshLODComponent.ParentGroup = Entity.Null;
                        meshLODComponents[temp] = meshLODComponent;

                        materialMeshInfos[temp] = materialMeshInfo;

#if ENABLE_UNITY_OCCLUSION
                        if(occludees.HasComponent(temp))
                            occludees[temp] = occludee;
#endif

                        lodParentIndex.value = lod.objectIndex;
                        lodParentIndices[temp] = lodParentIndex;

                        if(meshStreamingVertexOffsets.HasComponent(temp))
                            meshStreamingVertexOffsets[temp] = meshStreamingVertexOffset;
                        
                        /*lodValue.mask = lod.mask;
                        entityManager.SetComponentData(temp, lodValue);

                        renderer.mesh = meshes.Add(node.renderMesh.mesh);
                        renderer.material = materials.Add(node.renderMesh.material);
                        entityManager.SetComponentData(temp, renderer);*/
                    }

                    {
                        ref var lod = ref node.lods[0];

                        meshLODComponent.LODMask = lod.mask;
                        meshLODComponent.Group = objectEntities[lod.objectIndex];
                        //TODO:
                        meshLODComponent.ParentGroup = Entity.Null;
                        meshLODComponents[entity] = meshLODComponent;

                        lodParentIndex.value = lod.objectIndex;
                        lodParentIndices[entity] = lodParentIndex;

                        /*lodValue.mask = lod.mask;
                        entityManager.SetComponentData(entity, lodValue);*/
                        ///
                    }
                }

                localToWorlds[entity] = localToWorld;

                this.renderBounds[entity] = renderBounds;

                materialMeshInfos[entity] = materialMeshInfo;

#if ENABLE_UNITY_OCCLUSION
                if (occludees.HasComponent(entity))
                    occludees[entity] = occludee;
#endif

                if (meshStreamingVertexOffsets.HasComponent(entity))
                    meshStreamingVertexOffsets[entity] = meshStreamingVertexOffset;
            }
        }

        private struct ApplyNodeHierarchies
        {
            [NativeDisableContainerSafetyRestriction]
            public BufferLookup<MeshInstanceLODChild> lodChildren;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshInstanceLODParent> lodParents;

            public void Execute(
                int startRendererIndex,
                int startRendererDefinitionIndex, 
                int rendererDefinitionCount, 
                ref BlobArray<Entity> nodeEntities,
                ref BlobArray<Entity> objectEntities,
                ref MeshInstanceRendererDefinition definition)
            {
                DynamicBuffer<MeshInstanceLODChild> lodChildren;
                MeshInstanceLODChild lodChild;
                MeshInstanceLODParent lodParent;
                Entity entity, temp;
                int entityStartIndex = startRendererIndex, numLODs, i, j;
                for (i = 0; i < rendererDefinitionCount; ++i)
                {
                    ref var node = ref definition.nodes[startRendererDefinitionIndex + i];

                    numLODs = node.lods.Length;

                    entity = nodeEntities[entityStartIndex];
                    if (numLODs > 0)
                    {
                        for (j = 1; j < numLODs; ++j)
                        {
                            ref var lod = ref node.lods[j];

                            temp = nodeEntities[entityStartIndex + j];

                            lodChild.entity = temp;

                            GetDistance(
                                ref definition.objects[lod.objectIndex].distances,
                                lod.mask,
                                out lodChild.minDistance,
                                out lodChild.maxDistance);

                            lodParent.entity = objectEntities[lod.objectIndex];

                            lodChildren = this.lodChildren[lodParent.entity];

                            lodParent.childIndex = lodChildren.Length;

                            lodChildren.Add(lodChild);

                            lodParents[temp] = lodParent;
                        }

                        {
                            ref var lod = ref node.lods[0];

                            lodChild.entity = entity;

                            GetDistance(
                                ref definition.objects[lod.objectIndex].distances,
                                lod.mask,
                                out lodChild.minDistance,
                                out lodChild.maxDistance);

                            lodParent.entity = objectEntities[lod.objectIndex];
                            lodChildren = this.lodChildren[lodParent.entity];

                            lodParent.childIndex = lodChildren.Length;

                            lodChildren.Add(lodChild);

                            lodParents[entity] = lodParent;
                        }

                        entityStartIndex += numLODs;
                    }
                    else
                        ++entityStartIndex;
                }
            }
        }

        private struct ComputeRenderBounds
        {
            [ReadOnly]
            public BufferLookup<MeshInstanceLODChild> lodChildren;

            [ReadOnly]
            public ComponentLookup<LocalToWorld> localToWorlds;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<RenderBounds> renderBounds;

            public RenderBounds Execute(in Entity entity)
            {
                var lodChildren = this.lodChildren[entity];
                if (lodChildren.Length < 1)
                {
                    RenderBounds renderBounds;
                    renderBounds.Value = MinMaxAABB.Empty;
                    return renderBounds;
                }
                else
                {
                    var child = lodChildren[0].entity;
                    var renderBounds = this.renderBounds[child];
                    if (this.lodChildren.HasBuffer(child))
                        renderBounds = Execute(child);

                    renderBounds.Value = AABB.Transform(localToWorlds[child].Value, renderBounds.Value);

                    MinMaxAABB result = renderBounds.Value;
                    int numChildren = lodChildren.Length;
                    for (int i = 1; i < numChildren; ++i)
                    {
                        child = lodChildren[i].entity;
                        renderBounds = this.renderBounds[child];
                        if (this.lodChildren.HasBuffer(child))
                            renderBounds = Execute(child);

                        renderBounds.Value = AABB.Transform(localToWorlds[child].Value, renderBounds.Value);

                        result.Encapsulate(renderBounds.Value);
                    }

                    renderBounds.Value = AABB.Transform(math.inverse(localToWorlds[entity].Value), result);
                    this.renderBounds[entity] = renderBounds;

                    return renderBounds;
                }
            }

            public unsafe void Execute(
                int lodGroupCount, 
                ref BlobArray<Entity> entityArray)
            {
                for (int i = 0; i < lodGroupCount; ++i)
                    Execute(entityArray[i]);
            }
        }

        [BurstCompile]
        private struct Init : IJob
        {
            [ReadOnly]
            public NativeArray<MeshInstanceRendererPrefabBuilder> results;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<RenderBounds> renderBounds;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceLODParent> lodParents;
            [NativeDisableParallelForRestriction]
            public BufferLookup<MeshInstanceLODChild> lodChildren;

            public void Execute(int index)
            {
                var result = results[index];
                ref var definition = ref result.definition.Value;
                ref var prefab = ref result.prefab.Value;

                if (result.lodGroupCount > 0)
                {
                    ApplyObjectHierarchies applyObjectHierarchies;
                    applyObjectHierarchies.lodChildren = lodChildren;
                    applyObjectHierarchies.lodParents = lodParents;

                    applyObjectHierarchies.Execute(
                        result.startLODGroupIndex,
                        result.lodGroupCount, 
                        ref prefab.objects, 
                        ref definition.objects);
                }

                ApplyNodeHierarchies applyNodeHierarchies;
                applyNodeHierarchies.lodChildren = lodChildren;
                applyNodeHierarchies.lodParents = lodParents;
                applyNodeHierarchies.Execute(
                    result.startRendererIndex, 
                    result.startRendererDefinitionIndex, 
                    result.rendererDefinitionCount, 
                    ref prefab.nodes, 
                    ref prefab.objects, 
                    ref definition);

                if (result.lodGroupCount > 0)
                {
                    ComputeRenderBounds computeRenderBounds;
                    computeRenderBounds.lodChildren = lodChildren;
                    computeRenderBounds.localToWorlds = localToWorlds;
                    computeRenderBounds.renderBounds = renderBounds;
                    computeRenderBounds.Execute(result.startLODGroupIndex + result.lodGroupCount, ref prefab.objects);
                }
            }

            public void Execute()
            {
                int length = results.Length;
                for (int i = 0; i < length; ++i)
                    Execute(i);

                //results.Dispose();
            }
        }

        [BurstCompile]
        private struct InitParallel : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<MeshInstanceRendererPrefabBuilder> results;

            [ReadOnly]
            public SingletonAssetContainer<MeshInstanceMaterialAsset>.Reader materialAssets;

            [ReadOnly]
            public SingletonAssetContainer<MeshInstanceMeshAsset>.Reader meshAssets;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID>.Reader batchMaterialIDs;

            [ReadOnly]
            public SharedHashMap<MeshInstanceMeshAsset, BatchMeshID>.Reader batchMeshIDs;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MaterialMeshInfo> materialMeshInfos;

#if ENABLE_UNITY_OCCLUSION
            [NativeDisableParallelForRestriction]
            public ComponentLookup<OcclusionTest> occludees;
#endif
            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshLODGroupComponent> meshLODGroupComponents;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshLODComponent> meshLODComponents;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<RenderBounds> renderBounds;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceLODParentIndex> lodParentIndices;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<MeshStreamingVertexOffset> meshStreamingVertexOffsets;

            public void Execute(int index)
            {
                MeshInstanceRendererPrefabBuilder result;
                int resultLength = results.Length, resultIndex = 0, count = 0, nextCount;
                for (int i = 0; i < resultLength; ++i)
                {
                    result = results[i];

                    nextCount = count + result.rendererDefinitionCount + result.lodGroupCount;
                    if (nextCount > index)
                        break;

                    count = nextCount;

                    ++resultIndex;
                }

                index -= count;

                {
                    result = results[resultIndex];
                    ref var definition = ref result.definition.Value;
                    ref var prefab = ref result.prefab.Value;

                    if (result.rendererDefinitionCount > index)
                    {
                        ApplyNodes applyNodes;
                        applyNodes.materialAssets = materialAssets;
                        applyNodes.meshAssets = meshAssets;
                        applyNodes.batchMaterialIDs = batchMaterialIDs;
                        applyNodes.batchMeshIDs = batchMeshIDs;

                        applyNodes.materialMeshInfos = materialMeshInfos;

#if ENABLE_UNITY_OCCLUSION
                        applyNodes.occludees = occludees;
#endif
                        applyNodes.meshLODComponents = meshLODComponents;
                        applyNodes.renderBounds = renderBounds;
                        applyNodes.localToWorlds = localToWorlds;

                        applyNodes.lodParentIndices = lodParentIndices;
                        applyNodes.meshStreamingVertexOffsets = meshStreamingVertexOffsets;

                        applyNodes.Execute(
                            result.startRendererDefinitionIndex + index,
                            definition.instanceID, 
                            ref definition.nodes,
                            ref definition.renderers, 
                            ref prefab.objects,
                            ref prefab.nodes);
                    }
                    else if (result.lodGroupCount > 0)
                    {
                        ApplyObjects applyObjects;
                        applyObjects.localToWorlds = localToWorlds;
                        applyObjects.meshLODGroupComponents = meshLODGroupComponents;
                        applyObjects.lodParentIndices = lodParentIndices;

                        applyObjects.Execute(result.startLODGroupIndex + index - result.rendererDefinitionCount, ref prefab.objects, ref definition.objects);
                    }
                }
            }
        }

        private struct ComputeCount
        {
            [ReadOnly]
            public NativeArray<MeshInstanceRendererPrefabBuilder> results;

            public NativeArray<int> count;

            public void Execute()
            {
                MeshInstanceRendererPrefabBuilder result;
                int length = results.Length;
                for(int i = 0; i < length; ++i)
                {
                    result = results[i];

                    count[0] += result.rendererDefinitionCount + result.lodGroupCount;
                }
            }
        }

        /*public delegate void InitDelegate(
            int innerloopBatchCount,
            in UnsafeListEx<MeshInstanceRendererPrefabBuilder> results,
            in EntityQuery group, 
            ref EntityComponentAssigner assigner,
            ref SystemState systemState);

        public delegate void CreateDelegate(
            int maxRendererDefinitionCount,
            in EntityArchetype rootGroupArchetype,
            in EntityArchetype subGroupArchetype,
            in EntityArchetype instanceArchetype,
            in EntityArchetype lodArchetype,
            in SingletonAssetContainer<int>.Reader componentTypeIndices,
            in SingletonAssetContainer<ComponentTypeSet>.Reader componentTypes,
            ref UnsafeListEx<MeshInstanceRendererPrefabBuilder> results,
            ref SharedHashMap<int, MeshInstanceRendererPrefabBuilder> builders,
            ref SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>> prefabs,
            ref EntityComponentAssigner assigner,
            ref SystemState systemState,
            in EntityQuery group);

        public delegate void DestroyDelegate(
            in EntityQuery group,
            ref SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>> prefabs,
            ref SharedHashMap<int, MeshInstanceRendererPrefabBuilder> builders, 
            ref SystemState state);

        public static readonly DestroyDelegate DestroyFunction = BurstCompiler.CompileFunctionPointer<DestroyDelegate>(Destroy).Invoke;

        public static readonly CreateDelegate CreateFunction = BurstCompiler.CompileFunctionPointer<CreateDelegate>(Create).Invoke;

        public static readonly InitDelegate InitFunction = BurstCompiler.CompileFunctionPointer<InitDelegate>(InitData).Invoke;*/

        public static unsafe void GetDistance(ref BlobArray<float> distances, int mask, out float min, out float max)
        {
            min = float.MaxValue;
            max = 0.0f;

            if (mask != 0)
            {
                int length = math.min(32 - math.lzcnt(mask), distances.Length);
                for (int i = 31 - math.lzcnt(mask ^ (mask - 1)); i < length; ++i)
                {
                    if ((mask & (1 << i)) == 0)
                        continue;

                    min = i > 0 ? math.min(min, distances[i - 1]) : 0.0f;
                    max = math.max(max, distances[i]);
                }
            }
        }

        public static unsafe int EntityStartIndexOf(ref BlobArray<MeshInstanceRendererDefinition.Node> nodes, int nodeIndex)
        {
            int entityStartIndex = 0, lodCount;
            for (int i = 0; i < nodeIndex; ++i)
            {
                lodCount = nodes[i].lods.Length;
                entityStartIndex += lodCount > 0 ? lodCount : 1;
            }

            return entityStartIndex;
        }

        public static int EntityCountOf(
            ref BlobArray<MeshInstanceRendererDefinition.Node> rendererDefinitions,
            int startRendererDefinitionIndex, 
            int maxRendererDefinitionCount, 
            out int normalRendererCount, 
            out int lodRendererCount)
        {
            normalRendererCount = 0;
            lodRendererCount = 0;

            int numRendererDefinitions = math.min(rendererDefinitions.Length - startRendererDefinitionIndex, maxRendererDefinitionCount), numLODs;
            for (int i = 0; i < numRendererDefinitions; ++i)
            {
                numLODs = rendererDefinitions[startRendererDefinitionIndex + i].lods.Length;

                if (numLODs > 0)
                    lodRendererCount += numLODs;
                else
                    ++normalRendererCount;
            }

            return numRendererDefinitions;
        }

        public static void GetMaxLODGroupIndex(
            ref BlobArray<MeshInstanceRendererDefinition.Object> lodGroups, 
            ref BlobArray<MeshInstanceRendererDefinition.Node> renderers,
            int startRendererDefinitionIndex,
            int rendererDefinitionCount,
            ref int maxLODGroupIndex)
        {
            int numLODs, i, j;
            for (i = 0; i < rendererDefinitionCount; ++i)
            {
                ref var lods = ref renderers[startRendererDefinitionIndex + i].lods;

                numLODs = lods.Length;
                if (numLODs > 0)
                {
                    for (j = 0; j < numLODs; ++j)
                        maxLODGroupIndex = math.max(maxLODGroupIndex, GetMaxLODGroupIndex(lods[j].objectIndex, ref lodGroups));
                }
            }
        }

        public static int GetMaxLODGroupIndex(
            int lodGroupIndex,
            ref BlobArray<MeshInstanceRendererDefinition.Object> lodGroups)
        {
            if (lodGroupIndex == -1)
                return -1;

            return math.max(lodGroupIndex, GetMaxLODGroupIndex(lodGroups[lodGroupIndex].parent.objectIndex, ref lodGroups));
        }

        /*[BurstCompile]
        [AOT.MonoPInvokeCallback(typeof(DestroyDelegate))]*/
        public static void Destroy(
            in EntityQuery group,
            in ComponentTypeHandle<MeshInstanceRendererID> idType, 
            ref SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>> prefabs,
            ref SharedHashMap<int, MeshInstanceRendererPrefabBuilder> builders, 
            ref EntityManager entityManager)
        {
            using (var entities = new NativeList<Entity>(Allocator.TempJob))
            {
                prefabs.lookupJobManager.CompleteReadWriteDependency();
                builders.lookupJobManager.CompleteReadWriteDependency();

                group.CompleteDependency();

                DestroyPrefabsEx destroyPrefabs;
                destroyPrefabs.idType = idType;
                destroyPrefabs.prefabs = prefabs.writer;
                destroyPrefabs.builders = builders.writer;
                destroyPrefabs.entitiesToDestroy = entities;

                destroyPrefabs.RunByRef(group);

                entityManager.DestroyEntity(entities.AsArray());
            }

            entityManager.RemoveComponent<MeshInstanceRendererID>(group);
        }

        public static void CreateEntityArchetypes(
            EntityManager entityManager,
            out EntityArchetype rootGroupArchetype,
            out EntityArchetype subGroupArchetype,
            out EntityArchetype instanceArchetype,
            out EntityArchetype lodArchetype)
        {
            using (var componentTypes = new NativeList<ComponentType>(Allocator.Temp)
            {
                ComponentType.ReadOnly<Prefab>(),
                ComponentType.ReadOnly<RenderBounds>(),
                ComponentType.ReadOnly<MeshLODGroupComponent>(),
                ComponentType.ReadOnly<MeshInstanceLODChild>(),
                ComponentType.ReadWrite<LocalToWorld>(),
                ComponentType.ReadWrite<WorldRenderBounds>()
            })
                rootGroupArchetype = entityManager.CreateArchetype(componentTypes.AsArray());

            using (var componentTypes = new NativeList<ComponentType>(Allocator.Temp)
            {
                ComponentType.ReadOnly<Prefab>(),
                ComponentType.ReadOnly<RenderBounds>(),
                ComponentType.ReadOnly<MeshLODGroupComponent>(),
                ComponentType.ReadOnly<MeshInstanceLODParentIndex>(),
                ComponentType.ReadOnly<MeshInstanceLODParent>(),
                ComponentType.ReadOnly<MeshInstanceLODChild>(),
                ComponentType.ReadWrite<LocalToWorld>(),
                ComponentType.ReadWrite<WorldRenderBounds>()
            })
                subGroupArchetype = entityManager.CreateArchetype(componentTypes.AsArray());

            using (var componentTypes = new NativeList<ComponentType>(Allocator.Temp)
            {
                ComponentType.ReadOnly<Prefab>(),
                //ComponentType.ReadOnly<MeshInstanceRendererInit>(),

                ComponentType.ReadOnly<RenderFilterSettings>(),
                ComponentType.ReadOnly<MaterialMeshInfo>(),
                ComponentType.ReadOnly<WorldToLocal_Tag>(),

                ComponentType.ReadOnly<PerInstanceCullingTag>(),
                ComponentType.ReadOnly<RenderBounds>(),
                ComponentType.ReadWrite<LocalToWorld>(),

                ComponentType.ReadWrite<WorldRenderBounds>(),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>()
            })
                instanceArchetype = entityManager.CreateArchetype(componentTypes.AsArray());

            using (var componentTypes = new NativeList<ComponentType>(Allocator.Temp)
            {
                ComponentType.ReadOnly<Prefab>(),
                //ComponentType.ReadOnly<MeshInstanceRendererInit>(),

                ComponentType.ReadOnly<WorldToLocal_Tag>(),
                ComponentType.ReadOnly<RenderFilterSettings>(),
                ComponentType.ReadOnly<MaterialMeshInfo>(),

                ComponentType.ReadOnly<PerInstanceCullingTag>(),
                ComponentType.ReadOnly<MeshLODComponent>(),
                ComponentType.ReadOnly<RenderBounds>(),

                ComponentType.ReadOnly<MeshInstanceLODParent>(),
                ComponentType.ReadOnly<MeshInstanceLODParentIndex>(),

                ComponentType.ReadWrite<LocalToWorld>(),

                ComponentType.ReadWrite<WorldRenderBounds>(),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>()
            })
                lodArchetype = entityManager.CreateArchetype(componentTypes.AsArray());

            BurstUtility.InitializeJob<Init>();
            BurstUtility.InitializeJobParallelFor<InitParallel>();
        }

        public static unsafe void Create(
            int rendererDefinitionCount, 
            int startRendererDefinitionIndex,
            int startRendererIndex, 
            int normalRendererCount,
            int lodRendererCount,
            int lodGroupCount, 
            int startLODGroupIndex, 
            in EntityArchetype rootGroupArchetype,
            in EntityArchetype subGroupArchetype,
            in EntityArchetype instanceArchetype,
            in EntityArchetype lodArchetype,
            in SingletonAssetContainer<TypeIndex>.Reader componentTypeIndices, 
            in SingletonAssetContainer<ComponentTypeSet>.Reader componentTypes, 
            ref MeshInstanceRendererDefinition definition,
            ref BlobArray<Entity> outNodeEntities,
            ref BlobArray<Entity> outObjectEntities,
            ref EntityComponentAssigner.Writer writer, 
            ref EntityManager entityManager)
        {
            //var entityManager = systemState.EntityManager;

            Entity entity;
            int numSubGroups = 0, numRootGroups = 0, i, j;
            if (lodGroupCount > 0)
            {
                for (i = 0; i < lodGroupCount; ++i)
                {
                    if (definition.objects[startLODGroupIndex + i].parent.objectIndex == -1)
                        ++numRootGroups;
                }

                NativeArray<Entity> subGroups, rootGroups;
                numSubGroups = lodGroupCount - numRootGroups;
                if (numSubGroups > 0)
                {
                    subGroups = new NativeArray<Entity>(numSubGroups, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    entityManager.CreateEntity(subGroupArchetype, subGroups);
                }
                else
                    subGroups = default;

                if (numRootGroups > 0)
                {
                    rootGroups = new NativeArray<Entity>(numRootGroups, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    entityManager.CreateEntity(rootGroupArchetype, rootGroups);
                }
                else
                    rootGroups = default;

                //outObjectEntities.ResizeUninitialized(numObjects);

                int index;
                for (i = 0; i < lodGroupCount; ++i)
                {
                    index = startLODGroupIndex + i;
                    ref var result = ref definition.objects[index];

                    entity = result.parent.objectIndex == -1 ? rootGroups[--numRootGroups] : subGroups[--numSubGroups];

                    outObjectEntities[index] = entity;
                }

                if (subGroups.IsCreated)
                    subGroups.Dispose();

                if (rootGroups.IsCreated)
                    rootGroups.Dispose();
            }

            //bool isFlipWinding;
            Entity temp;

            //var flippedWindingTags = new NativeList<Entity>(Allocator.Temp);

            var customProbeTags = new NativeList<Entity>(Allocator.Temp);
            var blendProbeTags = new NativeList<Entity>(Allocator.Temp);
            //var ambientProbeTags = new NativeList<Entity>(Allocator.Temp);

#if ENABLE_UNITY_OCCLUSION
            var occludees = new NativeList<Entity>(Allocator.Temp);
#endif

            var depthSorted = new NativeList<Entity>(Allocator.Temp);
            var meshStreamingOffsets = new NativeList<Entity>(Allocator.Temp);

            NativeArray<Entity> instances;
            if (normalRendererCount > 0)
            {
                instances = new NativeArray<Entity>(normalRendererCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                entityManager.CreateEntity(instanceArchetype, instances);
            }
            else
                instances = default;

            NativeArray<Entity> lods;
            if (lodRendererCount > 0)
            {
                lods = new NativeArray<Entity>(lodRendererCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                entityManager.CreateEntity(lodArchetype, lods);
            }
            else
                lods = default;

            //int numEntities = instanceCount + lodCount;
            //outNodeEntities.ResizeUninitialized(numEntities);

            SingletonAssetContainerHandle handle;
            handle.instanceID = definition.instanceID;

            ComponentTypeSet componentTypesTemp;

            int entityIndex = startRendererIndex, currentEntityIndex, numLODs;
            for (i = 0; i < rendererDefinitionCount; ++i)
            {
                ref var node = ref definition.nodes[startRendererDefinitionIndex + i];
                ref var renderer = ref definition.renderers[node.rendererIndex];

                //isFlipWinding = math.determinant(node.matrix) < 0.0;

                if (node.componentTypesIndex == -1)
                    componentTypesTemp = default;
                else
                {
                    handle.index = node.componentTypesIndex;

                    componentTypesTemp = componentTypes[handle];
                }

                currentEntityIndex = entityIndex++;

                numLODs = node.lods.Length;
                if (numLODs > 0)
                {
                    entity = lods[--lodRendererCount];

                    for (j = 1; j < numLODs; ++j)
                    {
                        temp = lods[--lodRendererCount];

                        if (node.componentTypesIndex != -1)
                            entityManager.AddComponent(temp, componentTypesTemp);

                        /*if (isFlipWinding)
                            flippedWindingTags.Add(temp);*/

                        __BuildLightMap(temp, ref renderer, ref customProbeTags, ref blendProbeTags/*, ref ambientProbeTags*/);

#if ENABLE_UNITY_OCCLUSION
                        if ((node.flag & MeshInstanceDefinition.Node.Flag.AllowOcclusionWhenDynamic) == MeshInstanceDefinition.Node.Flag.AllowOcclusionWhenDynamic)
                            occludees.Add(temp);
#endif

                        __BuildMaterialProperties(
                            definition.instanceID,
                            temp,
                            componentTypeIndices,
                            ref node.materialPropertyIndices,
                            ref definition.materialProperties,
                            ref writer);

                        if ((renderer.flag & MeshInstanceRendererFlag.DepthSorted) == MeshInstanceRendererFlag.DepthSorted)
                            depthSorted.Add(temp);

                        if (node.meshStreamingOffset != -1)
                            meshStreamingOffsets.Add(temp);

                        outNodeEntities[entityIndex++] = temp;
                    }
                }
                else
                    entity = instances[--normalRendererCount];

                /*if (isFlipWinding)
                    flippedWindingTags.Add(entity);*/

                if(node.componentTypesIndex != -1)
                    entityManager.AddComponent(entity, componentTypesTemp);

                __BuildLightMap(entity, ref renderer, ref customProbeTags, ref blendProbeTags/*, ref ambientProbeTags*/);

#if ENABLE_UNITY_OCCLUSION
                if ((node.flag & MeshInstanceDefinition.Node.Flag.AllowOcclusionWhenDynamic) == MeshInstanceDefinition.Node.Flag.AllowOcclusionWhenDynamic)
                    occludees.Add(entity);
#endif

                __BuildMaterialProperties(
                    definition.instanceID, 
                    entity, 
                    componentTypeIndices, 
                    ref node.materialPropertyIndices, 
                    ref definition.materialProperties, 
                    ref writer);

                if ((renderer.flag & MeshInstanceRendererFlag.DepthSorted) == MeshInstanceRendererFlag.DepthSorted)
                    depthSorted.Add(entity);

                if (node.meshStreamingOffset != -1)
                    meshStreamingOffsets.Add(entity);

                outNodeEntities[currentEntityIndex] = entity;
            }

            //UnityEngine.Assertions.Assert.AreEqual(0, numEntities);

            /*entityManager.AddComponentBurstCompatible<RenderMeshFlippedWindingTag>(flippedWindingTags);

            flippedWindingTags.Dispose();*/

            entityManager.AddComponentBurstCompatible<CustomProbeTag>(customProbeTags.AsArray());

            customProbeTags.Dispose();

            entityManager.AddComponentBurstCompatible<BlendProbeTag>(blendProbeTags.AsArray());

            blendProbeTags.Dispose();

            /*entityManager.AddComponentBurstCompatible<AmbientProbeTag>(ambientProbeTags);

            ambientProbeTags.Dispose();*/

#if ENABLE_UNITY_OCCLUSION
            entityManager.AddComponentBurstCompatible<OcclusionTest>(occludees);

            occludees.Dispose();
#endif

            entityManager.AddComponentBurstCompatible<DepthSorted_Tag>(depthSorted.AsArray());

            depthSorted.Dispose();

            entityManager.AddComponentBurstCompatible<MeshStreamingVertexOffset>(meshStreamingOffsets.AsArray());

            meshStreamingOffsets.Dispose();

            if (instances.IsCreated)
            {
                instances.Dispose();

                UnityEngine.Assertions.Assert.AreEqual(0, normalRendererCount);
            }

            if (lods.IsCreated)
            {
                lods.Dispose();

                UnityEngine.Assertions.Assert.AreEqual(0, lodRendererCount);
            }
        }

        public static unsafe void Create(
            int maxRendererDefinitionCount, 
            in EntityArchetype rootGroupArchetype,
            in EntityArchetype subGroupArchetype,
            in EntityArchetype instanceArchetype,
            in EntityArchetype lodArchetype,
            in EntityQuery group, 
            in ComponentTypeHandle<MeshInstanceRendererData> instanceType, 
            in SingletonAssetContainer<TypeIndex>.Reader componentTypeIndices,
            in SingletonAssetContainer<ComponentTypeSet>.Reader componentTypes,
            ref NativeList<MeshInstanceRendererPrefabBuilder> results,
            ref SharedHashMap<int, MeshInstanceRendererPrefabBuilder> builders,
            ref SharedHashMap<int, BlobAssetReference<MeshInstanceRendererPrefab>> prefabs,
            ref EntityComponentAssigner assigner,
            ref EntityManager entityManager)
        {
            //var entityManager = systemState.EntityManager;

            var builderWriter = builders.writer;

            int entityCount = group.CalculateEntityCount();
            if (entityCount > 0)
            {
                using (var ids = new NativeArray<MeshInstanceRendererID>(entityCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                {
                    prefabs.lookupJobManager.CompleteReadWriteDependency();
                    builders.lookupJobManager.CompleteReadWriteDependency();

                    //systemState.CompleteDependency();

                    CollectPrefabEx collectPrefab;
                    collectPrefab.baseEntityIndexArray = group.CalculateBaseEntityIndexArray(Allocator.TempJob);
                    collectPrefab.instanceType = instanceType;// systemState.GetComponentTypeHandle<MeshInstanceRendererData>(true);
                    collectPrefab.prefabs = prefabs.writer;
                    collectPrefab.builders = builderWriter;
                    collectPrefab.ids = ids;

                    collectPrefab.RunByRef(group);

                    entityManager.AddComponentDataBurstCompatible(group, ids);
                }
            }

            Build build;
            build.maxRendererDefinitionCount = maxRendererDefinitionCount;
            build.results = results;
            build.builders = builderWriter;
            build.Execute();

            assigner.CompleteDependency();

            var prefabsReader = prefabs.reader;
            var prefabWriter = prefabs.writer;
            var assignerWriter = assigner.writer;
            int length = results.Length;
            for (int i = 0; i < length; ++i)
            {
                ref var result = ref results.ElementAt(i);
                ref var definition = ref result.definition.Value;
                ref var prefab = ref prefabWriter[definition.instanceID].Value;

                Create(
                    result.rendererDefinitionCount, 
                    result.startRendererDefinitionIndex, 
                    result.startRendererIndex, 
                    result.normalRendererCount, 
                    result.lodRendererCount, 
                    result.lodGroupCount, 
                    result.startLODGroupIndex, 
                    rootGroupArchetype,
                    subGroupArchetype,
                    instanceArchetype,
                    lodArchetype,
                    componentTypeIndices, 
                    componentTypes, 
                    ref definition,
                    ref prefab.nodes,
                    ref prefab.objects,
                    ref assignerWriter, 
                    ref entityManager);

                InitSharedData(
                    result.startRendererIndex,
                    result.startRendererDefinitionIndex,
                    result.rendererDefinitionCount,
                    ref entityManager,
                    ref definition.renderers,
                    ref definition.nodes,
                    ref prefabsReader[definition.instanceID].Value.nodes);
            }
        }

        public static void InitSharedData(
            int startRendererIndex, 
            int startRendererDefinitionIndex, 
            int rendererDefinitionCount, 
            ref EntityManager entityManager,
            ref BlobArray<MeshInstanceRendererDefinition.Renderer> renderers,
            ref BlobArray<MeshInstanceRendererDefinition.Node> nodes,
            ref BlobArray<Entity> entityArray)
        {
            RenderFilterSettings renderFilterSettings;
            int entityIndex = startRendererIndex, numLODs, index, i, j;
            for (i = 0; i < rendererDefinitionCount; ++i)
            {
                index = startRendererDefinitionIndex + i;

                ref var node = ref nodes[index];

                numLODs = node.lods.Length;

                renderFilterSettings = renderers[node.rendererIndex].renderFilterSettings;
                if (numLODs > 0)
                {
                    for (j = 0; j < numLODs; ++j)
                        entityManager.SetSharedComponent(entityArray[entityIndex++], renderFilterSettings);
                }
                else
                    entityManager.SetSharedComponent(entityArray[entityIndex++], renderFilterSettings);
            }
        }

        public unsafe static JobHandle Schedule(
            int systemID,
            int innerloopBatchCount,
            in JobHandle inputDeps, 
            in NativeArray<MeshInstanceRendererPrefabBuilder> results,
            in SingletonAssetContainer<MeshInstanceMaterialAsset> materialAssets,
            in SingletonAssetContainer<MeshInstanceMeshAsset> meshAssets,
            in SharedHashMap<MeshInstanceMaterialAsset, BatchMaterialID> batchMaterialIDs,
            in SharedHashMap<MeshInstanceMeshAsset, BatchMeshID> batchMeshIDs,
            ref ComponentLookup<RenderBounds> renderBounds,
            ref ComponentLookup<LocalToWorld> localToWorlds,
            ref ComponentLookup<MaterialMeshInfo> materialMeshInfos, 
            ref ComponentLookup<MeshLODGroupComponent> meshLODGroupComponents,
            ref ComponentLookup<MeshLODComponent> meshLODComponents,
            ref ComponentLookup<MeshInstanceLODParentIndex> lodParentIndices,
            ref ComponentLookup<MeshStreamingVertexOffset> meshStreamingVertexOffsets,
            ref ComponentLookup<MeshInstanceLODParent> meshInstanceLODParents, 
            ref BufferLookup<MeshInstanceLODChild> lodChildren)
        {
            //var renderBounds = systemState.GetComponentLookup<RenderBounds>();
            //var localToWorlds = systemState.GetComponentLookup<LocalToWorld>();

            JobHandle jobHandle;
            using (var count = new NativeArray<int>(1, Allocator.Temp, NativeArrayOptions.ClearMemory))
            {
                ComputeCount computeCount;
                computeCount.results = results;
                computeCount.count = count;
                computeCount.Execute();

                ref var batchMaterialIDsJobManager = ref batchMaterialIDs.lookupJobManager;
                ref var batchMeshIDsJobManager = ref batchMeshIDs.lookupJobManager;

                InitParallel initParallel;
                initParallel.results = results;
                initParallel.materialAssets = materialAssets.reader;
                initParallel.meshAssets = meshAssets.reader;
                initParallel.batchMaterialIDs = batchMaterialIDs.reader;
                initParallel.batchMeshIDs = batchMeshIDs.reader;
                initParallel.materialMeshInfos = materialMeshInfos;// systemState.GetComponentLookup<MaterialMeshInfo>();

#if ENABLE_UNITY_OCCLUSION
                initParallel.occludees = systemState.GetComponentLookup<OcclusionTest>();
#endif

                initParallel.meshLODGroupComponents = meshLODGroupComponents;// systemState.GetComponentLookup<MeshLODGroupComponent>();
                initParallel.meshLODComponents = meshLODComponents;// systemState.GetComponentLookup<MeshLODComponent>();

                initParallel.renderBounds = renderBounds;
                initParallel.localToWorlds = localToWorlds;
                initParallel.lodParentIndices = lodParentIndices;// systemState.GetComponentLookup<MeshInstanceLODParentIndex>();
                initParallel.meshStreamingVertexOffsets = meshStreamingVertexOffsets;// systemState.GetComponentLookup<MeshStreamingVertexOffset>();

                jobHandle = initParallel.ScheduleByRef(
                    count[0]/*results.length*/, 
                    innerloopBatchCount, 
                    JobHandle.CombineDependencies(batchMaterialIDsJobManager.readOnlyJobHandle, batchMeshIDsJobManager.readOnlyJobHandle, inputDeps));

                batchMaterialIDsJobManager.AddReadOnlyDependency(jobHandle);
                batchMeshIDsJobManager.AddReadOnlyDependency(jobHandle);

                //int systemID = systemState.GetSystemID();

                materialAssets.AddDependency(systemID, jobHandle);
                meshAssets.AddDependency(systemID, jobHandle);
            }

            Init init;
            init.results = results;
            init.renderBounds = renderBounds;
            init.localToWorlds = localToWorlds;
            init.lodParents = meshInstanceLODParents;// systemState.GetComponentLookup<MeshInstanceLODParent>();
            init.lodChildren = lodChildren;// systemState.GetBufferLookup<MeshInstanceLODChild>();

            jobHandle = init.ScheduleByRef(jobHandle);

            return jobHandle;
        }

        private static void __BuildLightMap(
            in Entity entity, 
            ref MeshInstanceRendererDefinition.Renderer renderer, 
            ref NativeList<Entity> customProbeTags,
            ref NativeList<Entity> blendProbeTags/*, 
            ref NativeList<Entity> ambientProbeTags*/)
        {
            switch (renderer.staticLightingMode)
            {
                case MeshInstanceRendererDefinition.Renderer.StaticLightingMode.LightProbes:
                    switch (renderer.lightProbeUsage)
                    {
                        case LightProbeUsage.CustomProvided:
                            customProbeTags.Add(entity);
                            break;
                        case LightProbeUsage.BlendProbes:
                            blendProbeTags.Add(entity);
                            break;
                    }

                    break;
            }
        }

        private static void __BuildMaterialProperties(
            int instanceID, 
            in Entity entity,
            in SingletonAssetContainer<TypeIndex>.Reader componentTypeIndices,
            ref BlobArray<int> materialPropertyIndices, 
            ref BlobArray<MeshInstanceRendererDefinition.MaterialProperty> materialProperties, 
            ref EntityComponentAssigner.Writer writer)
        {
            int numMaterialProperites = materialPropertyIndices.Length;
            for(int i = 0; i < numMaterialProperites; ++i)
                materialProperties[materialPropertyIndices[i]].SetComponentData(
                    instanceID, 
                    entity, 
                    componentTypeIndices, 
                    ref writer);
        }
    }
}