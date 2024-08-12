using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace ZG
{
    public struct FrustumPlanes
    {
        public enum IntersectResult
        {
            /// <summary>
            /// The object is completely outside of the planes.
            /// </summary>
            Out,

            /// <summary>
            /// The object is completely inside of the planes.
            /// </summary>
            In,

            /// <summary>
            /// The object is partially intersecting the planes.
            /// </summary>
            Partial
        };

        public float4 _0;
        public float4 _1;
        public float4 _2;
        public float4 _3;
        public float4 _4;
        public float4 _5;

        private static readonly UnityEngine.Vector3[] Conrners = new UnityEngine.Vector3[4];

        public static AABB Include(in AABB aabb, in float3 point)
        {
            float3 min = math.min(aabb.Min, point), 
                max =math.max(aabb.Max, point);

            AABB result;
            result.Center = (min + max) * 0.5f;
            result.Extents = (max - min) * 0.5f;

            return result;
        }

        public static AABB CreateAABB(UnityEngine.Camera camera, float nearClipPlaneOverride = 0.0f, float farClipPlaneOverride = 0.0f)
        {
            var rect = camera.rect;
            var eye = camera.stereoActiveEye;

            camera.CalculateFrustumCorners(rect, nearClipPlaneOverride > math.FLT_MIN_NORMAL ? nearClipPlaneOverride : camera.nearClipPlane, eye, Conrners);

            AABB aabb;
            aabb.Center = Conrners[1];
            aabb.Extents = float3.zero;

            for (int i = 1; i < 4; ++i)
                aabb = Include(aabb, Conrners[i]);

            camera.CalculateFrustumCorners(rect, farClipPlaneOverride > math.FLT_MIN_NORMAL ? farClipPlaneOverride : camera.farClipPlane, eye, Conrners);
            for (int i = 0; i < 4; ++i)
                aabb = Include(aabb, Conrners[i]);

            aabb = AABB.Transform(camera.transform.localToWorldMatrix, aabb);

            return aabb;
        }

        public FrustumPlanes(UnityEngine.Camera camera)
        {
            this = default;

            Unity.Rendering.FrustumPlanes.FromCamera(camera, AsArray());
        }

        public unsafe NativeArray<float4> AsArray()
        {
            return CollectionHelper.ConvertExistingDataToNativeArray<float4>(UnsafeUtility.AddressOf(ref this), 6, Allocator.None, true);
        }

        public IntersectResult Intersect(in float3 center, in float3 extents)
        {
            AABB aabb;
            aabb.Center = center;
            aabb.Extents = extents;
            return (IntersectResult)Unity.Rendering.FrustumPlanes.Intersect(AsArray(), aabb);
        }
    }

    public struct MainCameraForward : IComponentData
    {
        public float3 value;
    }

    public struct MainCameraFrustum : IComponentData
    {
        public float3 center;
        public float3 extents;
        public FrustumPlanes frustumPlanes;
    }
}
