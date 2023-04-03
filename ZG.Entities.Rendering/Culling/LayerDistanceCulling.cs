using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Rendering;
using Unity.Entities.Graphics;
using UnityEngine.Rendering;
using UnityEditor.Experimental;

[assembly: RegisterGenericJobType(typeof(ZG.HybridRendererCullingTest<ZG.LayerDistanceCullingTester, ZG.LayerDistanceCullingClipper>))]
/*#if HYBRID_RENDERER_CULL_OVERRIDE
[assembly: HybridRendererCulling(typeof(ZG.LayerDistanceCulling))]
#endif*/

namespace ZG
{
    public struct LayerDistanceCullingDefinition
    {
        public BlobArray<float> values;
    }

    public struct LayerDistanceCullingTester : IHybridRendererCullingTester
    {
        public float distanceSq;

        public float3 cameraPosition;

        [ReadOnly]
        public NativeArray<WorldRenderBounds> worldRenderBounds;

        public bool Test(int entityIndex, int splitIndex)
        {
            return worldRenderBounds[entityIndex].Value.DistanceSq(cameraPosition) <= distanceSq;
        }
    }

    public struct LayerDistanceCullingClipper : IHybridRendererCullingClipper<LayerDistanceCullingTester>
    {
        public float farClipPlaneSq;

        public float3 cameraPosition;

        public BlobAssetReference<LayerDistanceCullingDefinition> definition;

        [ReadOnly]
        public SharedComponentTypeHandle<RenderFilterSettings> renderFilterSettingsType;

        [ReadOnly]
        public ComponentTypeHandle<ChunkWorldRenderBounds> chunkWorldRenderBoundType;

        [ReadOnly]
        public ComponentTypeHandle<WorldRenderBounds> worldRenderBoundType;

        public HybridRendererCullingResult Cull(in ArchetypeChunk chunk)
        {
            if (!chunk.Has(renderFilterSettingsType))
                return HybridRendererCullingResult.Invsible;

            float distanceSq = definition.Value.values[chunk.GetSharedComponent(renderFilterSettingsType).Layer];
            if (distanceSq <= math.FLT_MIN_NORMAL)
                distanceSq = farClipPlaneSq;

            if (distanceSq > math.FLT_MIN_NORMAL)
            {
                if (!chunk.Has(ref chunkWorldRenderBoundType) || 
                    distanceSq < chunk.GetChunkComponentData(ref chunkWorldRenderBoundType).Value.DistanceSq(cameraPosition))
                    return HybridRendererCullingResult.Invsible;

                if (!chunk.Has(ref worldRenderBoundType))
                    return HybridRendererCullingResult.Visible;

                return HybridRendererCullingResult.PerInstance;
            }

            return HybridRendererCullingResult.Visible;
        }

        public LayerDistanceCullingTester CreatePerInstanceTester(in ArchetypeChunk chunk)
        {
            LayerDistanceCullingTester tester;
            tester.distanceSq = definition.Value.values[chunk.GetSharedComponent(renderFilterSettingsType).Layer];
            if (tester.distanceSq <= math.FLT_MIN_NORMAL)
                tester.distanceSq = farClipPlaneSq;

            tester.cameraPosition = cameraPosition;
            tester.worldRenderBounds = chunk.GetNativeArray(ref worldRenderBoundType);

            return tester;
        }
    }

    public static class LayerDistanceCullingSettings
    {
        private struct JobManager
        {

            public static readonly SharedStatic<LookupJobManager> value = SharedStatic<LookupJobManager>.GetOrCreate<JobManager>();
        }

        private struct Active
        {

            public static readonly SharedStatic<bool> value = SharedStatic<bool>.GetOrCreate<Active>();
        }

        private struct FarClipPlane
        {

            public static readonly SharedStatic<float> value = SharedStatic<float>.GetOrCreate<FarClipPlane>();
        }

        private struct GlobalDefinition
        {

            public static readonly SharedStatic<BlobAssetReference<LayerDistanceCullingDefinition>> value = SharedStatic<BlobAssetReference<LayerDistanceCullingDefinition>>.GetOrCreate<GlobalDefinition>();
        }

        public static BlobAssetReference<LayerDistanceCullingDefinition> globalDefinition
        {
            get => GlobalDefinition.value.Data;

            set
            {
                GlobalDefinition.value.Data = value;
            }
        }

        public static ref float farClipPlane => ref FarClipPlane.value.Data;

        public static ref bool isActive => ref Active.value.Data;

        public static void AddReadOnlyDependency(in JobHandle jobHandle) => JobManager.value.Data.AddReadOnlyDependency(jobHandle);

        public static void CompleteReadWriteDependency()
        {
            JobManager.value.Data.CompleteReadWriteDependency();
        }
    }

    public struct LayerDistanceCullingCore
    {
        private SharedComponentTypeHandle<RenderFilterSettings> __renderFilterSettingsType;
        private ComponentTypeHandle<ChunkWorldRenderBounds> __chunkWorldRenderBoundType;
        private ComponentTypeHandle<WorldRenderBounds> __worldRenderBoundType;

        public LayerDistanceCullingCore(ref EntityManager entityManager)
        {
            __renderFilterSettingsType = entityManager.GetSharedComponentTypeHandle<RenderFilterSettings>();
            __chunkWorldRenderBoundType = entityManager.GetComponentTypeHandle<ChunkWorldRenderBounds>(true);
            __worldRenderBoundType = entityManager.GetComponentTypeHandle<WorldRenderBounds>(true);
        }

        public JobHandle Cull(
            ref EntityManager entityManager,
            ref HybridRendererCullingContext cullingContext,
            in JobHandle cullingJobDependency)
        {
            LayerDistanceCullingSettings.CompleteReadWriteDependency();

            if (!LayerDistanceCullingSettings.isActive)
                return cullingJobDependency;

            var definition = LayerDistanceCullingSettings.globalDefinition;
            if (!definition.IsCreated)
                return cullingJobDependency;

            LayerDistanceCullingClipper clipper;

            float farClipPlane = LayerDistanceCullingSettings.farClipPlane;

            clipper.farClipPlaneSq = farClipPlane * farClipPlane;
            clipper.cameraPosition = cullingContext.value.lodParameters.cameraPosition;
            clipper.definition = definition;
            clipper.renderFilterSettingsType = __renderFilterSettingsType.UpdateAsRef(entityManager);
            clipper.chunkWorldRenderBoundType = __chunkWorldRenderBoundType.UpdateAsRef(entityManager);
            clipper.worldRenderBoundType = __worldRenderBoundType.UpdateAsRef(entityManager);

            var jobHandle = cullingContext.Cull<LayerDistanceCullingTester, LayerDistanceCullingClipper>(ref clipper, cullingJobDependency);

            LayerDistanceCullingSettings.AddReadOnlyDependency(jobHandle);

            return jobHandle;
        }
    }

    [BurstCompile, UpdateInGroup(typeof(HybridRendererCullingSystemGroup))]
    public partial struct LayerDistanceCullingSystem : ISystem
    {
        private EntityQuery __group;
        private LayerDistanceCullingCore __core;

        public void OnCreate(ref SystemState state)
        {
            __group = HybridRendererCullingState.GetEntityQuery(ref state);

            var entityManager = state.EntityManager;

            __core = new LayerDistanceCullingCore(ref entityManager);
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            ref var cullingState = ref __group.GetSingletonRW<HybridRendererCullingState>().ValueRW;

            cullingState.cullingJobDependency = __core.Cull(
                ref entityManager,
                ref cullingState.context,
                JobHandle.CombineDependencies(state.Dependency, cullingState.cullingJobDependency));

            state.Dependency = cullingState.cullingJobDependency;
        }
    }
}