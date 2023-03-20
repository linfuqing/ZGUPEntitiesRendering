using System.Collections.Generic;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Rendering;
using UnityEngine;

#if CLIP_MATERIAL_PROPERTY
namespace ZG.Effects
{
    public struct ClipMaterialProperty : IComponentData
    {
        public float distance;
        public float near;
        public float far;
    }

    [MeshInstanceMaterialProperty("_ClipInvDist", MaterialPropertyFormat.Float, -1, "CLIP_GLOBAL")]
    public struct ClipInvDist : IComponentData
    {
        public float value;
    }

    [MeshInstanceMaterialProperty("_ClipNearDivDist", MaterialPropertyFormat.Float, -1, "CLIP_GLOBAL")]
    public struct ClipNearDivDist : IComponentData
    {
        public float value;
    }

    [MeshInstanceMaterialProperty("_ClipFarDivDist", MaterialPropertyFormat.Float, -1, "CLIP_GLOBAL")]
    public struct ClipFarDivDist : IComponentData
    {
        public float value;
    }

    public static class ClipMaterialTypes
    {
        public static readonly ComponentType Dist = ComponentType.ReadWrite<ClipInvDist>();
        public static readonly ComponentType Near = ComponentType.ReadWrite<ClipNearDivDist>();
        public static readonly ComponentType Far = ComponentType.ReadWrite<ClipFarDivDist>();
    }

    [EntityComponent(typeof(ClipInvDist))]
    [EntityComponent(typeof(ClipNearDivDist))]
    [EntityComponent(typeof(ClipFarDivDist))]
    public class ClipMaterialPropertyComponent : MonoBehaviour
    {

    }

    [UpdateInGroup(typeof(InitializationSystemGroup)), UpdateAfter(typeof(EntityObjectSystemGroup))]
    public partial class ClipMaterialPropertyUpdateSystem : SystemBase
    {
        private Entity __entity;

        protected override void OnCreate()
        {
            base.OnCreate();

            __entity = EntityManager.CreateEntity(typeof(ClipMaterialProperty));

            /*var componentType = TransformAccessArrayEx.componentType;

            __distGroup = GetEntityQuery(ClipMaterialTypes.Dist, componentType, ComponentType.Exclude<RenderMesh>());
            __distGroup.SetChangedVersionFilter(ClipMaterialTypes.Dist);

            __nearGroup = GetEntityQuery(ClipMaterialTypes.Near, componentType, ComponentType.Exclude<RenderMesh>());
            __nearGroup.SetChangedVersionFilter(ClipMaterialTypes.Near);

            __farGroup = GetEntityQuery(ClipMaterialTypes.Far, componentType, ComponentType.Exclude<RenderMesh>());
            __farGroup.SetChangedVersionFilter(ClipMaterialTypes.Far);

            __renderers = new List<Renderer>();
            __materials = new List<Material>();*/
        }

        protected override void OnDestroy()
        {
            EntityManager.DestroyEntity(__entity);

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            var clip = Clip.instance;
            if (clip != null)
            {
                ClipMaterialProperty property;
                property.distance = clip.distance;
                property.near = clip.near;
                property.far = clip.far;
                SetSingleton(property);
            }
        }
    }

    [BurstCompile]
    public partial struct ClipMaterialPropertySystem : ISystem
    {
        [BurstCompile]
        private struct SetValues : IJobChunk
        {
            public const int SIZE = sizeof(float);

            public float value;

            public DynamicComponentTypeHandle typeHandle;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var values = batchInChunk.GetDynamicComponentDataArrayReinterpret<float>(typeHandle, SIZE);
                int count = batchInChunk.Count;
                for(int i  = 0; i < count; ++i)
                    values[i] = value;
            }
        }

        private ClipMaterialProperty __property;
        private EntityQuery __propertyGroup;

        private EntityQuery __distGroup;
        private EntityQuery __nearGroup;
        private EntityQuery __farGroup;

        private EntityQuery __distChangedGroup;
        private EntityQuery __nearChangedGroup;
        private EntityQuery __farChangedGroup;

        public void OnCreate(ref SystemState state)
        {
            __propertyGroup = state.GetEntityQuery(ComponentType.ReadOnly<ClipMaterialProperty>());

            __distGroup = state.GetEntityQuery(ClipMaterialTypes.Dist);
            __nearGroup = state.GetEntityQuery(ClipMaterialTypes.Near);
            __farGroup = state.GetEntityQuery(ClipMaterialTypes.Far);

            __distChangedGroup = state.GetEntityQuery(ClipMaterialTypes.Dist);
            __distChangedGroup.SetChangedVersionFilter(ClipMaterialTypes.Dist);

            __nearChangedGroup = state.GetEntityQuery(ClipMaterialTypes.Near);
            __nearChangedGroup.SetChangedVersionFilter(ClipMaterialTypes.Near);

            __farChangedGroup = state.GetEntityQuery(ClipMaterialTypes.Far);
            __farChangedGroup.SetChangedVersionFilter(ClipMaterialTypes.Far);
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var property = __propertyGroup.GetSingleton<ClipMaterialProperty>();

            float distance = 1.0f / property.distance;

            SetValues setValues;
            setValues.value = distance;
            setValues.typeHandle = state.GetDynamicComponentTypeHandle(ClipMaterialTypes.Dist);

            bool isNearDirty, isFarDirty;
            JobHandle inputDeps = state.Dependency, distJobHandle;
            if (property.distance == __property.distance)
            {
                distJobHandle = setValues.ScheduleParallel(__distChangedGroup, inputDeps);

                isNearDirty = property.near != __property.near;
                isFarDirty = property.far != __property.far;
            }
            else
            {
                distJobHandle = setValues.ScheduleParallel(__distGroup, inputDeps);

                isNearDirty = true;
                isFarDirty = true;
            }

            setValues.value = property.near * distance;
            setValues.typeHandle = state.GetDynamicComponentTypeHandle(ClipMaterialTypes.Near);

            JobHandle nearJobHandle;
            if (isNearDirty)
                nearJobHandle = setValues.ScheduleParallel(__nearGroup, inputDeps);
            else
                nearJobHandle = setValues.ScheduleParallel(__nearChangedGroup, inputDeps);

            setValues.value = property.far * distance;
            setValues.typeHandle = state.GetDynamicComponentTypeHandle(ClipMaterialTypes.Far);

            JobHandle farJobHandle;
            if (isFarDirty)
                farJobHandle = setValues.ScheduleParallel(__farGroup, inputDeps);
            else
                farJobHandle = setValues.ScheduleParallel(__farChangedGroup, inputDeps);

            __property = property;

            state.Dependency = JobHandle.CombineDependencies(distJobHandle, nearJobHandle, farJobHandle);
        }
    }
}
#endif