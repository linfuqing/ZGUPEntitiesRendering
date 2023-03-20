using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZG
{
    public class LayerDistanceCullingFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public struct LayerDistanceOverride
        {
            [Layer]
            public int index;
            public float value;
        }

        [SerializeField]
        internal float _layerDistanceDefault;

        [SerializeField]
        internal LayerDistanceOverride[] _layerDistanceOverrides;

        private float[] __layerCullDistances = new float[32];

        private float __maxDistance;

        public static BlobAssetReference<LayerDistanceCullingDefinition> Create(float layerDistanceDefault, LayerDistanceOverride[] layerDistanceOverrides)
        {
            using (var blobBuilder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref blobBuilder.ConstructRoot<LayerDistanceCullingDefinition>();

                var values = blobBuilder.Allocate(ref root.values, 32);

                for (int i = 0; i < 32; ++i)
                    values[i] = layerDistanceDefault * layerDistanceDefault;

                if(layerDistanceOverrides != null)
                {
                    foreach (var layerDistanceOverride in layerDistanceOverrides)
                        values[layerDistanceOverride.index] = layerDistanceOverride.value * layerDistanceOverride.value;
                }

                return blobBuilder.CreateBlobAssetReference<LayerDistanceCullingDefinition>(Allocator.Persistent);
            }
        }

        /// <inheritdoc/>
        public override void Create()
        {
            RenderPipelineManager.beginCameraRendering -= __OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += __OnBeginCameraRendering;

            RenderPipelineManager.endCameraRendering -= __OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += __OnEndCameraRendering;

            if (!LayerDistanceCullingSettings.globalDefinition.IsCreated)
                LayerDistanceCullingSettings.globalDefinition = Create(_layerDistanceDefault, _layerDistanceOverrides);

            __CalculateMaxDistance();
        }

        // Here you can inject one or multiple render passes in the renderer.
        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
        }

        protected override void Dispose(bool disposing)
        {
            /*if (disposing && LayerDistanceCullingSettings.globalDefinition.IsCreated)
            {
                LayerDistanceCullingSettings.CompleteReadWriteDependency();

                var globalDefinition = LayerDistanceCullingSettings.globalDefinition;

                LayerDistanceCullingSettings.globalDefinition = BlobAssetReference<LayerDistanceCullingDefinition>.Null;

                globalDefinition.Dispose();
            }*/

            RenderPipelineManager.beginCameraRendering -= __OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= __OnEndCameraRendering;
        }

        private void __CalculateMaxDistance()
        {
            __maxDistance = _layerDistanceDefault;

            foreach (var layerDistanceOverride in _layerDistanceOverrides)
                __maxDistance = Mathf.Max(__maxDistance, layerDistanceOverride.value);
        }

        private void __OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (false == (LayerDistanceCullingSettings.isActive = isActive))
                return;

            float farClipPlane = camera.farClipPlane;
            if (farClipPlane < __maxDistance)
            {
                LayerDistanceCullingSettings.farClipPlane = farClipPlane;

                camera.farClipPlane = __maxDistance;
            }
            else
                LayerDistanceCullingSettings.farClipPlane = 0.0f;

            float layerDistance = _layerDistanceDefault > Mathf.Epsilon ? _layerDistanceDefault : farClipPlane;
            for (int i = 0; i < 32; ++i)
                __layerCullDistances[i] = layerDistance;

            if (_layerDistanceOverrides != null)
            {
                foreach (var layerDistanceOverride in _layerDistanceOverrides)
                    __layerCullDistances[layerDistanceOverride.index] = layerDistanceOverride.value > Mathf.Epsilon ? layerDistanceOverride.value : farClipPlane;
            }

            camera.layerCullDistances = __layerCullDistances;
        }

        private void __OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            float farClipPlane = LayerDistanceCullingSettings.farClipPlane;
            if (farClipPlane > Mathf.Epsilon)
                camera.farClipPlane = farClipPlane;
        }

        void OnValidate()
        {
            Create();

            ref var values = ref LayerDistanceCullingSettings.globalDefinition.Value.values;
            for (int i = 0; i < 32; ++i)
                values[i] = _layerDistanceDefault * _layerDistanceDefault;

            if (_layerDistanceOverrides != null)
            {
                foreach (var layerDistanceOverride in _layerDistanceOverrides)
                    values[layerDistanceOverride.index] = layerDistanceOverride.value * layerDistanceOverride.value;
            }

            __CalculateMaxDistance();
        }
    }
}