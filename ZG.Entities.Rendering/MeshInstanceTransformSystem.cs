/*using System;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace ZG
{
    [Serializable]
    public struct MeshInstanceTransformSource : IComponentData
    {
        public float4x4 matrix;
    }

    [Serializable]
    public struct MeshInstanceTransformDestination : IComponentData
    {
        public float4x4 matrix;
    }

    [Serializable]
    public struct MeshInstanceTransform : IComponentData
    {
        public uint version;
        public float4x4 matrix;
    }

    [Serializable]
    public struct MeshInstanceTransformVersion : IComponentData
    {
        public uint value;
    }

    public interface IMeshInstanceTransformEnumerator
    {
        bool MoveNext(int index, out NativeArray<Entity> entities);
    }

    public interface IMeshInstanceTransformEnumerable<T> where T : struct, IMeshInstanceTransformEnumerator
    {
        void Init(ref SystemState state);

        T GetEnumerator(in ArchetypeChunk chunk);
    }

    [BurstCompile]
    public struct MeshInstanceTransformJob<TEnumerator, TEnumerable> : IJobChunk
        where TEnumerator : struct, IMeshInstanceTransformEnumerator
        where TEnumerable : struct, IMeshInstanceTransformEnumerable<TEnumerator>
    {
        private struct Executor
        {
            public uint version;

            [ReadOnly]
            public NativeArray<Entity> entityArray;
            [ReadOnly]
            public NativeArray<MeshInstanceTransform> transforms;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToWorld> localToWorlds;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalToParent> localToParents;
            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceTransformSource> results;

            public TEnumerator enumerator;

            public void Mul(in float4x4 matrix, in Entity entity)
            {
                var localToWorld = localToWorlds[entity];
                localToWorld.Value = math.mul(matrix, localToWorld.Value);
                localToWorlds[entity] = localToWorld;

                if (localToParents.HasComponent(entity))
                {
                    var localToParent = localToParents[entity];
                    localToParent.Value = math.mul(matrix, localToParent.Value);
                    localToParents[entity] = localToParent;
                }
            }

            public void Mul(in float4x4 matrix, in NativeArray<Entity> entities)
            {
                int length = entities.Length;
                for (int i = 0; i < length; ++i)
                    Mul(matrix, entities[i]);
            }

            public void Execute(int index)
            {
                var transform = transforms[index];
                if (transform.version != version)
                    return;

                while (enumerator.MoveNext(index, out var entityArray))
                    Mul(transform.matrix, entityArray);
            }
        }

        public uint lastSystemVersion;

        [ReadOnly]
        public EntityTypeHandle entityType;
        [ReadOnly]
        public ComponentTypeHandle<MeshInstanceTransformVersion> versionType;
        [ReadOnly]
        public ComponentTypeHandle<MeshInstanceTransform> transformType;

        [NativeDisableParallelForRestriction]
        public ComponentLookup<LocalToWorld> localToWorlds;
        [NativeDisableParallelForRestriction]
        public ComponentLookup<LocalToParent> localToParents;
        [NativeDisableContainerSafetyRestriction]
        public ComponentLookup<MeshInstanceTransformSource> results;

        public TEnumerable enumerable;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            if (!chunk.DidChange(versionType, lastSystemVersion))
                return;

            Executor executor;
            executor.version = chunk.GetChunkComponentData(versionType).value;
            executor.entityArray = chunk.GetNativeArray(entityType);
            executor.transforms = chunk.GetNativeArray(transformType);
            executor.localToWorlds = localToWorlds;
            executor.localToParents = localToParents;
            executor.results = results;
            executor.enumerator = enumerable.GetEnumerator(chunk);

            int count = chunk.Count;
            for (int i = 0; i < count; ++i)
                executor.Execute(i);
        }
    }

    public struct MeshInstanceTransformManager<TEnumerator, TEnumerable> 
        where TEnumerator : struct, IMeshInstanceTransformEnumerator
        where TEnumerable : struct, IMeshInstanceTransformEnumerable<TEnumerator>
    {
        private EntityQuery __group;

        public MeshInstanceTransformManager(ref SystemState state, params ComponentType[] componentTypes)
        {
            var results = new System.Collections.Generic.List<ComponentType>();
            results.Add(ComponentType.ReadWrite<MeshInstanceTransformSource>());
            results.Add(ComponentType.ReadOnly<MeshInstanceTransformDestination>());
            results.AddRange(componentTypes);

            __group = state.GetEntityQuery(results.ToArray());
        }

        public void Update(ref SystemState state)
        {
            TEnumerable enumerable = default;
            enumerable.Init(ref state);

            MeshInstanceTransformJob<TEnumerator, TEnumerable> transform;
            transform.lastSystemVersion = state.LastSystemVersion;
            transform.entityType = state.GetEntityTypeHandle();
            transform.versionType = state.GetComponentTypeHandle<MeshInstanceTransformVersion>(true);
            transform.transformType = state.GetComponentTypeHandle<MeshInstanceTransform>(true);
            transform.localToWorlds = state.GetComponentLookup<LocalToWorld>();
            transform.localToParents = state.GetComponentLookup<LocalToParent>();
            transform.results = state.GetComponentLookup<MeshInstanceTransformSource>();
            transform.enumerable = enumerable;

            state.Dependency = transform.ScheduleParallel(__group, state.Dependency);
        }
    }

    [BurstCompile, UpdateInGroup(typeof(MeshInstanceTransformGroup), OrderFirst = true)]
    public partial struct MeshInstanceTransformSystem : ISystem
    {
        private struct Transform
        {
            public uint version;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<MeshInstanceTransformSource> sources;

            [ReadOnly]
            public NativeArray<MeshInstanceTransformDestination> destinations;

            public NativeArray<MeshInstanceTransform> transforms;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceTransformSource> results;

            public bool Execute(int index)
            {
                var source = sources[index].matrix;
                var destination = destinations[index].matrix;
                if (source.Equals(destination))
                    return false;

                MeshInstanceTransformSource result;
                result.matrix = destination;
                results[entityArray[index]] = result;

                MeshInstanceTransform transform;
                transform.version = version;
                transform.matrix = math.mul(destination, math.inverse(source));

                transforms[index] = transform;

                return true;
            }
        }

        [BurstCompile]
        private struct TransformEx : IJobChunk
        {
            public uint lastSystemVersion;

            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceTransformSource> sourceType;

            [ReadOnly]
            public ComponentTypeHandle<MeshInstanceTransformDestination> destinationType;

            public ComponentTypeHandle<MeshInstanceTransform> transformType;

            public ComponentTypeHandle<MeshInstanceTransformVersion> versionType;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<MeshInstanceTransformSource> results;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask, int _)
            {
                if (!batchInChunk.DidChange(sourceType, lastSystemVersion) &&
                    !batchInChunk.DidChange(destinationType, lastSystemVersion))
                    return;

                var version = batchInChunk.GetChunkComponentData(versionType);
                ++version.value;

                Transform transform;
                transform.version = version.value;
                transform.entityArray = batchInChunk.GetNativeArray(entityType);
                transform.sources = batchInChunk.GetNativeArray(sourceType);
                transform.destinations = batchInChunk.GetNativeArray(destinationType);
                transform.transforms = batchInChunk.GetNativeArray(transformType);
                transform.results = results;

                bool isDirty = false;
                int count = batchInChunk.Count;
                for (int i = 0; i < count; ++i)
                    isDirty = transform.Execute(i) || isDirty;

                if(isDirty)
                    batchInChunk.SetChunkComponentData(versionType, version);
            }
        }

        private EntityQuery __versionGroup;
        private EntityQuery __changeGroup;

        public void OnCreate(ref SystemState state)
        {
            __versionGroup = state.GetEntityQuery(
                ComponentType.ReadOnly<MeshInstanceTransform>(),
                ComponentType.ChunkComponentExclude<MeshInstanceTransformVersion>());

            __changeGroup = state.GetEntityQuery(
                ComponentType.ChunkComponent<MeshInstanceTransformVersion>(), 
                ComponentType.ReadWrite<MeshInstanceTransform>(),
                ComponentType.ReadWrite<MeshInstanceTransformSource>(),
                ComponentType.ReadOnly<MeshInstanceTransformDestination>());
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.AddComponent(__versionGroup, ComponentType.ChunkComponent<MeshInstanceTransformVersion>());

            TransformEx transform;
            transform.lastSystemVersion = state.LastSystemVersion;
            transform.entityType = state.GetEntityTypeHandle();
            transform.sourceType = state.GetComponentTypeHandle<MeshInstanceTransformSource>(true);
            transform.destinationType = state.GetComponentTypeHandle<MeshInstanceTransformDestination>(true);
            transform.transformType = state.GetComponentTypeHandle<MeshInstanceTransform>();
            transform.versionType = state.GetComponentTypeHandle<MeshInstanceTransformVersion>();
            transform.results = state.GetComponentLookup<MeshInstanceTransformSource>();

            state.Dependency = transform.ScheduleParallel(__changeGroup, state.Dependency);
        }
    }

    [UpdateInGroup(typeof(TransformSystemGroup))]
    public class MeshInstanceTransformGroup : ComponentSystemGroup
    {

    }
}*/