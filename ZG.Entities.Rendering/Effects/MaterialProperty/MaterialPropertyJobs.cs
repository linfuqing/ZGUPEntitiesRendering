using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Collections;

namespace ZG
{
    [BurstCompile]
    public struct MaterialPropertyCollect : IJobChunk
    {
        private struct Executor
        {
            [ReadOnly]
            public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader builders;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public BufferAccessor<MeshInstanceNode> renderers;

            public NativeList<Entity> entitiesToCreate;

            public NativeList<Entity> entitiesToInit;

            public void Execute(int index)
            {
                Entity entity = entityArray[index];
                if (builders.ContainsKey(entity))
                    return;

                entitiesToCreate.AddRange(renderers[index].Reinterpret<Entity>().AsNativeArray());

                entitiesToInit.Add(entity);
            }
        }

        [ReadOnly]
        public SharedHashMap<Entity, MeshInstanceRendererBuilder>.Reader builders;

        [ReadOnly]
        public EntityTypeHandle entityType;

        [ReadOnly]
        public BufferTypeHandle<MeshInstanceNode> rendererType;

        public NativeList<Entity> entitiesToCreate;

        public NativeList<Entity> entitiesToInit;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Executor executor;
            executor.builders = builders;
            executor.entityArray = chunk.GetNativeArray(entityType);
            executor.renderers = chunk.GetBufferAccessor(ref rendererType);
            executor.entitiesToCreate = entitiesToCreate;
            executor.entitiesToInit = entitiesToInit;

            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out int i))
                executor.Execute(i);
        }
    }

    [BurstCompile]
    public struct MaterialPropertyChange<T> : IJobChunk where T : unmanaged, IComponentData
    {
        private struct Executor
        {
            [ReadOnly]
            public NativeArray<MaterialPropertyOverride<T>> overrides;

            [ReadOnly]
            public BufferAccessor<MeshInstanceNode> nodes;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<T> values;

            public void Execute(int index)
            {
                var value = overrides[index].value;

                var nodes = this.nodes[index];
                int numNodes = nodes.Length;
                for (int i = 0; i < numNodes; ++i)
                    values[nodes[i].entity] = value;
            }
        }

        [ReadOnly]
        public ComponentTypeHandle<MaterialPropertyOverride<T>> overrideType;
        [ReadOnly]
        public BufferTypeHandle<MeshInstanceNode> nodeType;

        [NativeDisableParallelForRestriction]
        public ComponentLookup<T> values;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Executor executor;
            executor.overrides = chunk.GetNativeArray(ref overrideType);
            executor.nodes = chunk.GetBufferAccessor(ref nodeType);
            executor.values = values;

            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out int i))
                executor.Execute(i);
        }
    }
}