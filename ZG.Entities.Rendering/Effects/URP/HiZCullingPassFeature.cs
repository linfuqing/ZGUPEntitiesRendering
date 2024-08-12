using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZG
{
    public class HiZCullingPassFeature : ScriptableRendererFeature
    {
        private class RenderTextureWrapper : Object, IHiZRenderTexture
        {
            private RTHandle __value;
            private RenderTextureFactory __factory;

            public RTHandle value => __value;

            private readonly System.Action<AsyncGPUReadbackRequest> __callback;

            public static RenderTextureWrapper Create(int width, int height, RenderTextureFactory factory)
            {
                var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.RHalf, 0, 0);
                descriptor.useMipMap = false;

                var target = Object.Create<RenderTextureWrapper>();
                target.__value = RTHandles.Alloc(descriptor);
                target.__factory = factory;

                return target;
            }

            public RenderTextureWrapper()
            {
                __callback = __ReadbackComplete;
            }

            public void Release()
            {
                RTHandles.Release(__value);

                __value = null;

                Dispose();
            }

            public void Readback(ref NativeArray<half> output, CommandBuffer commandBuffer)
            {
                ++__factory._readbackRequestCount;

                commandBuffer.RequestAsyncReadbackIntoNativeArray(ref output, __value, __callback);
            }

            private void __ReadbackComplete(AsyncGPUReadbackRequest request)
            {
                --__factory._readbackRequestCount;
            }
        }

        private class RenderTextureFactory : IHiZRenderTextureFactory<RenderTextureWrapper>
        {
            internal int _readbackRequestCount;

            public bool isReadbackCompleted => _readbackRequestCount == 0;

            public RenderTextureWrapper Create(int width, int height) => RenderTextureWrapper.Create(width, height, this);
        }

        private class CullingPass : ScriptableRenderPass, IHiZDepthGenerater<RenderTextureWrapper>, IHiZMipmapGenerater<RenderTextureWrapper>
        {
            private ProfilingSampler __profilingSampler = new ProfilingSampler("HiZ");
            //private readonly int _MainTex = Shader.PropertyToID("_MainTex");
            //private readonly int _CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

            private RTHandle __cameraColorTargetHandle;

            private Shader __generateDepthShader;

            private Material __generateDepthMaterial;

            private HiZDepthTexture __depthTexture;

            private RenderTextureFactory __renderTextureFactory;

            private HiZDepthTexture.RenderPass<RenderTextureWrapper> __renderPass;

            public CullingPass(Shader generateDepth)
            {
                __generateDepthShader = generateDepth;
            }

            public bool Init()
            {
                var depthTexture = HiZUtility.currentDepthTexture;// HiZUtility.ConfigureDepthTexture(renderingData.cameraData.camera.pixelWidth);
                if (depthTexture == null)
                    return false;
                
                if (depthTexture != __depthTexture)
                {
                    if (__renderTextureFactory == null || __renderTextureFactory.isReadbackCompleted)
                    {
                        __depthTexture = depthTexture;

                        if (__renderPass.RenderTextures != null)
                        {
                            foreach (var renderTexture in __renderPass.RenderTextures)
                                renderTexture.Release();
                        }

                        __renderTextureFactory = new RenderTextureFactory();
                        __renderPass = new HiZDepthTexture.RenderPass<RenderTextureWrapper>(__depthTexture, __renderTextureFactory);
                    }
                    else
                        return false;
                }

                return __renderTextureFactory.isReadbackCompleted;
            }

            // This method is called before executing the render pass.
            // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
            // When empty this render pass will render to the active camera render target.
            // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
            // The render pipeline will ensure target setup and clearing happens in an performance manner.
            /*public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
            {
                var depthTexture = HiZUtility.currentDepthTexture;// HiZUtility.ConfigureDepthTexture(renderingData.cameraData.camera.pixelWidth);
                if (depthTexture != __depthTexture)
                {
                    if (__renderTextureFactory == null || __renderTextureFactory.isReadbackCompleted)
                    {
                        __depthTexture = depthTexture;

                        if (__renderPass.RenderTextures != null)
                        {
                            foreach (var renderTexture in __renderPass.RenderTextures)
                                renderTexture.Release();
                        }

                        __renderTextureFactory = new RenderTextureFactory();
                        __renderPass = new HiZDepthTexture.RenderPass<RenderTextureWrapper>(__depthTexture, __renderTextureFactory);
                    }
                    else
                        return;
                }
            }*/

            // This method is called before executing the render pass.
            // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
            // When empty this render pass will render to the active camera render target.
            // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
            // The render pipeline will ensure target setup and clearing happens in a performant manner.
            /*public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
            }*/

            // Here you can implement the rendering logic.
            // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
            // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
            // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                /*if (__renderTextureFactory == null || !__renderTextureFactory.isReadbackCompleted)
                    return;*/

                if (__generateDepthMaterial == null)
                    __generateDepthMaterial = new Material(__generateDepthShader);

                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, __profilingSampler))
                {
                    Matrix4x4 worldToCameraMatrix = renderingData.cameraData.camera.worldToCameraMatrix, projectionMatrix = renderingData.cameraData.camera.projectionMatrix;

                    var projectionMatrixEx = GL.GetGPUProjectionMatrix(projectionMatrix, true);

                    //cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);

                    __cameraColorTargetHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;

                    __renderPass.Execute(cmd, projectionMatrixEx * worldToCameraMatrix, this, this);

                    //cmd.SetViewProjectionMatrices(worldToCameraMatrix, projectionMatrix);
                }

                context.ExecuteCommandBuffer(cmd);

                CommandBufferPool.Release(cmd);
            }

            public void Execute(
                in RenderTextureWrapper target,
                CommandBuffer cmd)
            {
                /*CoreUtils.SetRenderTarget(cmd, target.value);
                CoreUtils.DrawFullScreen(cmd, __generateDepthMaterial);*/
                Blit(cmd, __cameraColorTargetHandle, target.value, __generateDepthMaterial, 0);

                /*cmd.SetRenderTarget(target);

                cmd.SetGlobalTexture(_MainTex, Shader.GetGlobalTexture(_CameraDepthTexture));

                cmd.DrawMesh(
                    RenderingUtils.fullscreenMesh,
                    Matrix4x4.identity,
                    __generateDepthMaterial);*/
            }

            public void Execute(
                in RenderTextureWrapper source,
                in RenderTextureWrapper destination,
                CommandBuffer cmd)
            {
                Blit(cmd, source.value, destination.value, __generateDepthMaterial, 1);

                /*cmd.SetRenderTarget(destination);

                cmd.SetGlobalTexture(_MainTex, source);

                cmd.DrawMesh(
                    RenderingUtils.fullscreenMesh,
                    Matrix4x4.identity,
                    __generateDepthMaterial);*/
            }

            // Cleanup any allocated resources that were created during the execution of this render pass.
            /*public override void OnCameraCleanup(CommandBuffer cmd)
            {
                __renderPass.Dispose();
                __renderPass = default;
            }

            private RTHandle __GetOrCreate(RenderTexture target)
            {
                if (!__rtHandles.TryGetValue(target, out var rtHandle))
                {
                    rtHandle = RTHandles.Alloc(target);

                    __rtHandles[target] = rtHandle;
                }

                return rtHandle;
            }*/
        }

        [SerializeField]
        internal Shader _generateDepth;

        private CullingPass __cullingPass;

        /// <inheritdoc/>
        public override void Create()
        {
            RenderPipelineManager.beginCameraRendering -= __OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += __OnBeginCameraRendering;

            RenderPipelineManager.endCameraRendering -= __OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += __OnEndCameraRendering;

            if (__cullingPass == null)
            {
                __cullingPass = new CullingPass(_generateDepth);

                // Configures where the render pass should be injected.
                __cullingPass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            }
        }

        // Here you can inject one or multiple render passes in the renderer.
        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!__cullingPass.Init())
                return;

            __cullingPass.ConfigureInput(ScriptableRenderPassInput.Depth);

            renderer.EnqueuePass(__cullingPass);
        }

        protected override void Dispose(bool disposing)
        {
            //??
            /*if(disposing)
                HiZUtility.Dispose();*/

            RenderPipelineManager.beginCameraRendering -= __OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= __OnEndCameraRendering;
        }

        private void __OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if(isActive && Application.isPlaying)
                HiZUtility.ConfigureDepthTexture(camera.pixelWidth);
        }

        private void __OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (isActive)
                HiZUtility.CleanupDepthTexture();
        }
    }
}