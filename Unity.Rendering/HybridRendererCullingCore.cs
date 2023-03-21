using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine.Rendering;
using UnityEngine;

namespace ZG
{
    public enum HybridRendererCullingResult
    {
        Visible, 
        Invsible, 
        PerInstance
    }

    public interface IHybridRendererCullingTester
    {
        bool Test(int entityIndex, int splitIndex);
    }

    public interface IHybridRendererCullingClipper<T> where T : IHybridRendererCullingTester
    {
        HybridRendererCullingResult Cull(in ArchetypeChunk chunk);

        T CreatePerInstanceTester(in ArchetypeChunk chunk);
    }

    public struct HybridRendererCullingContext
    {
        public BatchCullingContext value;

        internal IndirectList<ChunkVisibilityItem> _chunkVisibilityItems;

        internal HybridRendererCullingContext(in BatchCullingContext value, ref IndirectList<ChunkVisibilityItem> chunkVisibilityItems)
        {
            this.value = value;

            _chunkVisibilityItems = chunkVisibilityItems;
        }
    }

    [BurstCompile]
    public struct HybridRendererCullingTest<TTester, TClipper> : IJobParallelForDefer
        where TTester : struct, IHybridRendererCullingTester
        where TClipper : struct, IHybridRendererCullingClipper<TTester>
    {
        public BatchCullingViewType viewType;

        public int splitCount;

        public TClipper clipper;

        [NativeDisableParallelForRestriction]
        internal IndirectList<ChunkVisibilityItem> chunkVisibilityItems;

        public unsafe void Execute(int index)
        {
            ref var visibilityItem = ref chunkVisibilityItems.ElementAt(index);

            var chunkVisibility = visibilityItem.Visibility;
            switch (clipper.Cull(visibilityItem.Chunk))
            {
                case HybridRendererCullingResult.Invsible:
                    /* Cull the whole chunk if no LODs are enabled */
                    chunkVisibility->VisibleEntities[0] = 0;
                    chunkVisibility->VisibleEntities[1] = 0;
                    break;
                case HybridRendererCullingResult.PerInstance:
                    var tester = clipper.CreatePerInstanceTester(visibilityItem.Chunk);

                    bool entityAlreadyFrustumCulled, entityVisible;
                    byte splitMask;
                    int entityIndex, tzIndex, i, j;
                    ulong pendingBitfield, newBitfield;
                    /* Each chunk is guaranteed to have no more than 128 entities. So the Entities Graphics package uses `VisibleEntities`,
               which is an array of two 64-bit integers to indicate whether each of these entities is visible. */
                    for (i = 0; i < 2; ++i)
                    {
                        /* The pending bitfield indicates which incoming entities are to be occlusion-tested.
                           - If a bit is zero, the corresponding entity is already not drawn by a previously run system; e.g. it
                           might be frustum culled. So there's no need to process it further.
                           - If a bit is one, the corresponding entity needs to be occlusion-tested. */
                        pendingBitfield = chunkVisibility->VisibleEntities[i];
                        newBitfield = pendingBitfield;

                        /* Once the whole pending bitfield is zero, we don't need to do any more occlusion tests */
                        while (pendingBitfield != 0)
                        {
                            /* Get the index of the first visible entity using tzcnt. For example:

                               pendingBitfield = ...0000 0000 0000 1010 0000
                                                 ¡ø                   ¡ø  ¡ø
                                                 ©¦                   ©¦  ©¦
                                                 `leading zeros      ©¦  `trailing zeros
                                                                     `tzcount = 5

                               Then add (j << 6) to it, which adds 64 if we're in the second bitfield, i.e. if we're covering
                               entities [65, 128].
                            */
                            tzIndex = math.tzcnt(pendingBitfield);
                            entityIndex = (i << 6) + tzIndex;

                            splitMask = chunkVisibility->SplitMasks[entityIndex];
                            for (j = 0; j < splitCount; ++j)
                            {
                                /* If the view type is a light, then we check to see whether the current entity is already culled
                                   in the current split. If the view type is not a light, we ignore the split mask and proceed to
                                   occlusion cull. */
                                entityAlreadyFrustumCulled =
                                    viewType == BatchCullingViewType.Light &&
                                    ((splitMask & (1 << j)) == 0);

                                if (!entityAlreadyFrustumCulled)
                                {
                                    entityVisible = tester.Test(entityIndex, j);
                                    if (!entityVisible)
                                    {
                                        /* Set entity's visibility according to our occlusion test */
                                        newBitfield &= ~(1ul << tzIndex);

                                        /* Set the current split's bit to zero */
                                        splitMask &= (byte)~(1 << j);

                                        if (splitMask == 0)
                                            break;
                                    }
                                }
                            }

                            chunkVisibility->SplitMasks[entityIndex] = splitMask;

                            /* Set the index we just processed to zero, indicating that it's not pending any more */
                            pendingBitfield ^= 1ul << tzIndex;
                        }

                        chunkVisibility->VisibleEntities[i] = newBitfield;
                    }
                    break;
            }
        }
    }

    public struct HybridRendererCullingState : IComponentData
    {
        public HybridRendererCullingContext context;
        public JobHandle cullingJobDependency;

        public static EntityQuery GetEntityQuery(ref SystemState state)
        {
            return state.GetEntityQuery(new EntityQueryDesc()
            {
                All = new ComponentType[]
                {
                    typeof(HybridRendererCullingState)
                }, 
                Options = EntityQueryOptions.IncludeSystems
            });
        }
    }

    public struct HybridRendererCullingSystemGroup : ISystem
#if HYBRID_RENDERER_CULL_OVERRIDE
        , IEntitiesGraphicsCulling
#endif
    {
        private SystemHandle __sysetmHandle;
        private SystemGroup __systemGroup;

        public void OnCreate(ref SystemState state)
        {
            var world = state.World;
            __systemGroup = SystemGroupUtility.GetOrCreateSystemGroup(world, typeof(HybridRendererCullingSystemGroup));

            __sysetmHandle = world.GetExistingSystem<EntitiesGraphicsSystem>();
            if (__sysetmHandle != SystemHandle.Null)
                state.EntityManager.AddComponent<HybridRendererCullingState>(__sysetmHandle);
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        public void OnUpdate(ref SystemState state)
        {

        }

#if HYBRID_RENDERER_CULL_OVERRIDE
        JobHandle IEntitiesGraphicsCulling.Cull(
            ref EntityManager entityManager,
            ref IndirectList<ChunkVisibilityItem> chunkVisibilityItems,
            in BatchCullingContext batchCullingContext,
            in JobHandle cullingJobDependency)
        {
            HybridRendererCullingState cullingState;
            cullingState.context = new HybridRendererCullingContext(batchCullingContext, ref chunkVisibilityItems);
            cullingState.cullingJobDependency = cullingJobDependency;

            entityManager.SetComponentData(__sysetmHandle, cullingState);

            var world = entityManager.WorldUnmanaged;
            __systemGroup.Update(ref world);

            return entityManager.GetComponentData<HybridRendererCullingState>(__sysetmHandle).cullingJobDependency;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void __Init()
        {
            EntitiesGraphicsSystem.onCullingCreate += (ref EntityManager entityManager) =>
            {
                var handle = entityManager.World.GetOrCreateSystem<HybridRendererCullingSystemGroup>();
                return entityManager.WorldUnmanaged.GetUnsafeSystemRef<HybridRendererCullingSystemGroup>(handle);
            };
        }
#endif
    }

    public static class HybridRendererCullingUtility
    {
        public static JobHandle Cull<TTester, TClipper>(
            ref this HybridRendererCullingContext context, 
            ref TClipper clipper,
            in JobHandle cullingJobDependency)
            where TTester : unmanaged, IHybridRendererCullingTester
            where TClipper : unmanaged, IHybridRendererCullingClipper<TTester>
        {
            HybridRendererCullingTest<TTester, TClipper> test;
            test.viewType = context.value.viewType;
            test.splitCount = context.value.cullingSplits.Length;
            test.clipper = clipper;
            test.chunkVisibilityItems = context._chunkVisibilityItems;

            return test.ScheduleWithIndirectList(context._chunkVisibilityItems, 1, cullingJobDependency);
        }
    }
}