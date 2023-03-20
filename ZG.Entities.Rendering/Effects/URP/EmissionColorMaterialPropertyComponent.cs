using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Rendering;
using Unity.Mathematics;
using UnityEngine;
using ZG;

[assembly: RegisterGenericComponentType(typeof(MaterialPropertyOverrideInit<URPMaterialPropertyEmissionColor>))]
[assembly: RegisterGenericComponentType(typeof(MaterialPropertyOverride<URPMaterialPropertyEmissionColor>))]
[assembly: RegisterGenericJobType(typeof(MaterialPropertyChange<URPMaterialPropertyEmissionColor>))]

namespace ZG
{
    [BurstCompile, UpdateInGroup(typeof(MaterialPropertyInitSystemGroup))]
    public partial struct EmissionColorMaterialPropertyInitSystem : ISystem
    {
        private MaterialPropertyInitSystem<URPMaterialPropertyEmissionColor> __instance;

        public void OnCreate(ref SystemState state)
        {
            __instance.OnCreate(ref state);
        }

        public void OnDestroy(ref SystemState state)
        {
            __instance.OnDestroy(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            __instance.OnUpdate(ref state);
        }
    }

    [BurstCompile]
    public partial struct EmissionColorMaterialPropertyChangeSystem : ISystem
    {
        private MaterialPropertyChangeSystem<URPMaterialPropertyEmissionColor> __instance;

        public void OnCreate(ref SystemState state)
        {
            __instance.OnCreate(ref state);
        }

        public void OnDestroy(ref SystemState state)
        {
            __instance.OnDestroy(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            __instance.OnUpdate(ref state);
        }
    }

    [EntityComponent(typeof(MaterialPropertyOverride<URPMaterialPropertyEmissionColor>))]
    public class EmissionColorMaterialPropertyComponent : EntityProxyComponent, IEntityComponent
    {
        [ColorUsage(true, true)]
        public Color color;
        private Color __color;

        public void Update()
        {
            if (!gameObjectEntity.isCreated)
                return;

            if (color == __color)
                return;

            MaterialPropertyOverride<URPMaterialPropertyEmissionColor> value;
            value.value.Value = math.float4(color.r, color.g, color.b, color.a);
            this.SetComponentData(value);
        }

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            MaterialPropertyOverride<URPMaterialPropertyEmissionColor> value;
            value.value.Value = math.float4(color.r, color.g, color.b, color.a);
            assigner.SetComponentData(entity, value);
        }
    }
}