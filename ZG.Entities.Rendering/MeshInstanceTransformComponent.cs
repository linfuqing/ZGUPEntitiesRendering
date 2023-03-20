using Unity.Mathematics;
using Unity.Entities;
using UnityEngine;

namespace ZG
{
    public struct MeshInstanceTransform : IComponentData
    {
        public float3 translation;
        public quaternion rotation;
        public float3 scale;

        public float4x4 matrix => float4x4.TRS(translation, rotation, scale);
    }

    [EntityComponent(typeof(MeshInstanceTransform))]
    public class MeshInstanceTransformComponent : MonoBehaviour, IEntityComponent
    {
        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            MeshInstanceTransform transform;
            Transform offset = base.transform;

            transform.translation = offset.localPosition;
            transform.rotation = offset.localRotation;
            transform.scale = offset.localScale;
            assigner.SetComponentData(entity, transform);
        }
    }
}