using Unity.Entities;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities.Graphics;
using Unity.Jobs;
using ZG;

public struct MeshInstanceRendererLayerOverride : IComponentData
{
    public int sourceLayer;
    public int destinationLayer;
}

[BurstCompile]
public partial struct MeshInstanceRendererLayerOverrideSystem : ISystem
{
    private struct LayerOverride
    {
        [ReadOnly]
        public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader rendererBuilders;
        [ReadOnly] 
        public NativeArray<Entity> entityArray;
        [ReadOnly]
        public NativeArray<MeshInstanceRendererLayerOverride> inputs;
        [ReadOnly]
        public NativeArray<MeshInstanceRendererData> rendererDefinitions;
        [ReadOnly]
        public BufferAccessor<MeshInstanceNode> renderers;
        
        public NativeQueue<EntityData<int>>.ParallelWriter outputs;

        public void Execute(int index)
        {
            ref var definition = ref rendererDefinitions[index].definition.Value;
            int numDefinitions = definition.nodes.Length;
            if (numDefinitions < 1)
                return;
            
            Entity entity = entityArray[index];
            if (rendererBuilders.ContainsKey(entity))
                return;

            var renderers = this.renderers[index];
            int definitionIndex = 0, definitionCount, layer;
            {
                ref var rendererDefinition = ref definition.nodes[definitionIndex];

                definitionCount = rendererDefinition.lods.Length;
                definitionCount = definitionCount > 0 ? definitionCount : 1;

                layer = definition.renderers[rendererDefinition.rendererIndex].layer;
            }

            EntityData<int> output;
            var input = inputs[index];
            foreach (var renderer in renderers)
            {
                if (input.sourceLayer == layer)
                {
                    output.value = input.destinationLayer;

                    output.entity = renderer.entity;
                    
                    outputs.Enqueue(output);
                }

                if (--definitionCount < 1)
                {
                    if (++definitionIndex < numDefinitions)
                    {
                        ref var rendererDefinition = ref definition.nodes[definitionIndex];

                        definitionCount = rendererDefinition.lods.Length;
                        definitionCount = definitionCount > 0 ? definitionCount : 1;

                        layer = definition.renderers[rendererDefinition.rendererIndex].layer;
                    }
                    else
                        break;
                }
            }
        }
    }
    
    [BurstCompile]
    private struct LayerOverrideEx : IJobChunk
    {
        [ReadOnly]
        public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader rendererBuilders;
        [ReadOnly] 
        public EntityTypeHandle entityType;
        [ReadOnly]
        public ComponentTypeHandle<MeshInstanceRendererLayerOverride> inputType;
        [ReadOnly]
        public ComponentTypeHandle<MeshInstanceRendererData> rendererDefinitionType;
        [ReadOnly]
        public BufferTypeHandle<MeshInstanceNode> rendererType;
        
        public NativeQueue<EntityData<int>>.ParallelWriter outputs;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            LayerOverride layerOverride;
            layerOverride.rendererBuilders = rendererBuilders;
            layerOverride.entityArray = chunk.GetNativeArray(entityType);
            layerOverride.inputs = chunk.GetNativeArray(ref inputType);
            layerOverride.rendererDefinitions = chunk.GetNativeArray(ref rendererDefinitionType);
            layerOverride.renderers = chunk.GetBufferAccessor(ref rendererType);
            layerOverride.outputs = outputs;

            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
                layerOverride.Execute(i);
        }
    }
    
    private EntityQuery __group;
    
    private EntityTypeHandle __entityType;
    private ComponentTypeHandle<MeshInstanceRendererLayerOverride> __inputType;
    private ComponentTypeHandle<MeshInstanceRendererData> __rendererDefinitionType;
    private BufferTypeHandle<MeshInstanceNode> __rendererType;

    private SharedHashMap<Entity, MeshInstanceRendererBuilder> __rendererBuilders;
    
    private NativeQueue<EntityData<int>> __results;
    
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        using (var builder = new EntityQueryBuilder(Allocator.Temp))
            __group = builder
                .WithAll<MeshInstanceRendererLayerOverride, MeshInstanceRendererData, MeshInstanceNode>()
                .Build(ref state);
        
        __group.SetChangedVersionFilter(ComponentType.ReadOnly<MeshInstanceRendererLayerOverride>());

        __entityType = state.GetEntityTypeHandle();

        __inputType = state.GetComponentTypeHandle<MeshInstanceRendererLayerOverride>(true);

        __rendererDefinitionType = state.GetComponentTypeHandle<MeshInstanceRendererData>(true);

        __rendererType = state.GetBufferTypeHandle<MeshInstanceNode>(true);

        __rendererBuilders = state.WorldUnmanaged.GetExistingSystemUnmanaged<MeshInstanceRendererSystem>().builders;

        __results = new NativeQueue<EntityData<int>>(Allocator.Persistent);
    }

    public void OnDestroy(ref SystemState state)
    {
        __results.Dispose();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        RenderFilterSettings renderFilterSettings;
        var entityManager = state.EntityManager;
        while(__results.TryDequeue(out var result))
        {
            if(!entityManager.HasComponent<RenderFilterSettings>(result.entity))
                continue;
            
            renderFilterSettings = entityManager.GetSharedComponent<RenderFilterSettings>(result.entity);
            renderFilterSettings.Layer = result.value;
            entityManager.SetSharedComponent(result.entity, renderFilterSettings);
        }

        LayerOverrideEx layerOverride;
        layerOverride.rendererBuilders = __rendererBuilders.reader;
        layerOverride.entityType = __entityType.UpdateAsRef(ref state);
        layerOverride.inputType = __inputType.UpdateAsRef(ref state);
        layerOverride.rendererDefinitionType = __rendererDefinitionType.UpdateAsRef(ref state);
        layerOverride.rendererType = __rendererType.UpdateAsRef(ref state);
        layerOverride.outputs = __results.AsParallelWriter();

        ref var rendererBuilderJobManager = ref __rendererBuilders.lookupJobManager;
        var jobHandle = layerOverride.ScheduleParallelByRef(__group,
            JobHandle.CombineDependencies(rendererBuilderJobManager.readOnlyJobHandle, state.Dependency));
        
        rendererBuilderJobManager.AddReadOnlyDependency(jobHandle);

        state.Dependency = jobHandle;
    }
}