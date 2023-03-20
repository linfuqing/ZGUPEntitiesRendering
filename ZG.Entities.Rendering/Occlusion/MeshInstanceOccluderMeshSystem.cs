#if ENABLE_UNITY_OCCLUSION && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Burst;
using Unity.Rendering.Occlusion;

namespace ZG
{
    public struct MeshInstanceOccluderMesh : ISystemStateComponentData
    {
        public BlobAssetReference<float4> transformedVertexData;
    }

    [BurstCompile, UpdateInGroup(typeof(MeshInstanceSystemGroup), OrderLast = true)]
    public partial struct MeshInstanceOccluderMeshSystem : ISystem
    {
        [BurstCompile]
        private struct Init : IJobParallelFor
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> entityArray;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<OcclusionMesh> destinations;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceOccluderMesh> sources;

            public unsafe void Execute(int index)
            {
                Entity entity = entityArray[index];

                var destination = destinations[entity];
                if (destination.transformedVertexData.IsCreated)
                    return;

                MeshInstanceOccluderMesh source;
                source.transformedVertexData = 
                    destination.transformedVertexData = 
                    BlobAssetReference<float4>.Create(destination.vertexData.GetUnsafePtr(), UnsafeUtility.SizeOf<float4>() * destination.vertexCount);

                sources[entity] = source;
                destinations[entity] = destination;
            }
        }

        public static readonly int InnerloopBatchCount = 1;

        private EntityQuery __groupToDestroy;
        private EntityQuery __groupToCreate;

        public void OnCreate(ref SystemState state)
        {
            BurstUtility.InitializeJobParallelFor<Init>();

            __groupToDestroy = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceOccluderMesh>()
                    }, 
                    None = new ComponentType[]
                    {
                        typeof(OcclusionMesh)
                    },
                    Options = EntityQueryOptions.IncludeDisabled
                });

            __groupToCreate = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<OcclusionMesh>()
                    },
                    None = new ComponentType[]
                    {
                        typeof(MeshInstanceOccluderMesh)
                    },
                    Options = EntityQueryOptions.IncludeDisabled
                });
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            state.CompleteDependency();

            if (!__groupToDestroy.IsEmptyIgnoreFilter)
            {
                using (var meshes = __groupToDestroy.ToComponentDataArrayBurstCompatible<MeshInstanceOccluderMesh>(
                    state.GetDynamicComponentTypeHandle(ComponentType.ReadOnly<MeshInstanceOccluderMesh>()), Allocator.TempJob))
                {
                    BlobAssetReference<float4> transformedVertexData;
                    int numMeshes = meshes.Length;
                    for (int i = 0; i < numMeshes; ++i)
                    {
                        transformedVertexData = meshes[i].transformedVertexData;
                        if (!transformedVertexData.IsCreated)
                            continue;

                        transformedVertexData.Dispose();
                    }
                }

                entityManager.RemoveComponent<MeshInstanceOccluderMesh>(__groupToDestroy);
            }

            if(!__groupToCreate.IsEmptyIgnoreFilter)
            {
                var entityArray = __groupToCreate.ToEntityArrayBurstCompatible(state.GetEntityTypeHandle(), Allocator.TempJob);

                entityManager.AddComponent<MeshInstanceOccluderMesh>(__groupToCreate);

                Init init;
                init.entityArray = entityArray;
                init.destinations = state.GetComponentLookup<OcclusionMesh>();
                init.sources = state.GetComponentLookup<MeshInstanceOccluderMesh>();

                state.Dependency = init.Schedule(entityArray.Length, InnerloopBatchCount, state.Dependency);
            }
        }
    }
}
#endif