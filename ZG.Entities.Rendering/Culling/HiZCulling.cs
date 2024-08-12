using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine.Rendering;
using UnityEngine;
using ZG.Unsafe;

[assembly: RegisterGenericJobType(typeof(ZG.HybridRendererCullingTest<ZG.HiZCullingTester, ZG.HiZCullingClipper>))]

/*#if HYBRID_RENDERER_CULL_OVERRIDE
[assembly: HybridRendererCulling(typeof(ZG.HiZCulling))]
#endif*/

namespace ZG
{
    public static class HiZUtility
    {
        private struct IsActive
        {
            public readonly static SharedStatic<bool> Value = SharedStatic<bool>.GetOrCreate<IsActive>(); 
        }

        private struct ViewProjectionMatrix
        {

            public readonly static SharedStatic<Matrix4x4> Value = SharedStatic<Matrix4x4>.GetOrCreate<ViewProjectionMatrix>();
        }

        private struct CurrentDepthMap
        {
            public readonly static SharedStatic<HiZDepthMap> Value = SharedStatic<HiZDepthMap>.GetOrCreate<CurrentDepthMap>();
        }

        private static Dictionary<int, Queue<HiZDepthTexture>> __depthTexturePool;
        private static HiZDepthTexture __currentDepthTexture;

        public static bool isActive => IsActive.Value.Data;

        public static ref Matrix4x4 viewProjectionMatrix => ref ViewProjectionMatrix.Value.Data;

        public static ref HiZDepthMap currentDepthMap => ref CurrentDepthMap.Value.Data;

        public static HiZDepthTexture currentDepthTexture
        {
            get
            {
                if (isActive)
                    return __currentDepthTexture;

                return null;
            }
        }

        public static void CleanupDepthTexture()
        {
            IsActive.Value.Data = false;
        }

        public static void ConfigureDepthTexture(int pixelWidth)
        {
            GetDepthWidthFromScreen(pixelWidth, out int width, out int height, out int mipLevels);

            var depthTexture = __currentDepthTexture;
            if(depthTexture == null)
            {
                if (__depthTexturePool == null)
                    __depthTexturePool = new Dictionary<int, Queue<HiZDepthTexture>>();
            }
            else
            {
                int currentWidth = depthTexture.width;
                if (currentWidth != width)
                {
                    __depthTexturePool[currentWidth].Enqueue(depthTexture);

                    depthTexture = null;
                }
            }

            if (depthTexture == null)
            {
                if (!__depthTexturePool.TryGetValue(width, out var depthTextureQueue))
                {
                    depthTextureQueue = new Queue<HiZDepthTexture>();

                    __depthTexturePool[width] = depthTextureQueue;
                }

                depthTexture = depthTextureQueue.Count > 0 ? depthTextureQueue.Dequeue() : new HiZDepthTexture(width, height, mipLevels);
            }

            __currentDepthTexture = depthTexture;//.isReadbackCompleted ? depthTexture : null;

            if (__currentDepthTexture != null)
                CurrentDepthMap.Value.Data = __currentDepthTexture.GetMap();

            IsActive.Value.Data = true;
        }

        public static void GetDepthWidthFromScreen(int pixelWidth, out int width, out int height, out int mipLevels)
        {
            if (pixelWidth >= 2048)
            {
                width = 1024;
                height = 512;
                mipLevels = 10;
            }
            else if (pixelWidth >= 1024)
            {
                width = 512;
                height = 256;
                mipLevels = 9;
            }
            else
            {
                width = 256;
                height = 128;
                mipLevels = 8;
            }
        }

        [RuntimeDispose]
        public static void Dispose()
        {
            if (__currentDepthTexture != null)
            {
                __currentDepthTexture.Dispose();

                __currentDepthTexture = null;
            }

            if (__depthTexturePool != null)
            {
                foreach(var depthTextureQueue in __depthTexturePool.Values)
                {
                    foreach (var depthTexture in depthTextureQueue)
                        depthTexture.Dispose();
                }

                __depthTexturePool = null;
            }
        }

        /*public static int GetSizeOf(int width, int height, int mipLevels)
        {
            int size = 0;
            for (int i = 0; i < mipLevels; ++i)
                size += math.max(1, width >> i) * math.max(1, height >> i);

            return size;
        }*/
    }

    public struct HiZDepthMapData
    {
        public struct Lite
        {
            //public const int MAX_MIP_LEVELS = 10;

            private NativeArrayLite<half> _0;

            private NativeArrayLite<half> _1;

            private NativeArrayLite<half> _2;

            private NativeArrayLite<half> _3;

            private NativeArrayLite<half> _4;

            private NativeArrayLite<half> _5;

            private NativeArrayLite<half> _6;

            private NativeArrayLite<half> _7;

            private NativeArrayLite<half> _8;

            private NativeArrayLite<half> _9;

            public NativeArray<half> this[int mipLevel]
            {
                get
                {
                    switch (mipLevel)
                    {
                        case 0:
                            return _0;
                        case 1:
                            return _1;
                        case 2:
                            return _2;
                        case 3:
                            return _3;
                        case 4:
                            return _4;
                        case 5:
                            return _5;
                        case 6:
                            return _6;
                        case 7:
                            return _7;
                        case 8:
                            return _8;
                        case 9:
                            return _9;
                    }

                    throw new IndexOutOfRangeException();
                }
            }

            public bool isCreated => _0.isCreated;

            public Lite(int width, int height, Allocator allocator)
            {
                _0 = new NativeArrayLite<half>(math.max(1, width >> 0) * math.max(1, height >> 0), allocator, NativeArrayOptions.ClearMemory);
                _1 = new NativeArrayLite<half>(math.max(1, width >> 1) * math.max(1, height >> 1), allocator, NativeArrayOptions.ClearMemory);
                _2 = new NativeArrayLite<half>(math.max(1, width >> 2) * math.max(1, height >> 2), allocator, NativeArrayOptions.ClearMemory);
                _3 = new NativeArrayLite<half>(math.max(1, width >> 3) * math.max(1, height >> 3), allocator, NativeArrayOptions.ClearMemory);
                _4 = new NativeArrayLite<half>(math.max(1, width >> 4) * math.max(1, height >> 4), allocator, NativeArrayOptions.ClearMemory);
                _5 = new NativeArrayLite<half>(math.max(1, width >> 5) * math.max(1, height >> 5), allocator, NativeArrayOptions.ClearMemory);
                _6 = new NativeArrayLite<half>(math.max(1, width >> 6) * math.max(1, height >> 6), allocator, NativeArrayOptions.ClearMemory);
                _7 = new NativeArrayLite<half>(math.max(1, width >> 7) * math.max(1, height >> 7), allocator, NativeArrayOptions.ClearMemory);
                _8 = new NativeArrayLite<half>(math.max(1, width >> 8) * math.max(1, height >> 8), allocator, NativeArrayOptions.ClearMemory);
                _9 = new NativeArrayLite<half>(math.max(1, width >> 9) * math.max(1, height >> 9), allocator, NativeArrayOptions.ClearMemory);
            }

            public void Dispose()
            {
                _0.Dispose();
                _1.Dispose();
                _2.Dispose();
                _3.Dispose();
                _4.Dispose();
                _5.Dispose();
                _6.Dispose();
                _7.Dispose();
                _8.Dispose();
                _9.Dispose();
            }

            public AsyncGPUReadbackRequest ReadbackFrom(Texture texture, int mipLevel)
            {
                var values = this[mipLevel];
                return AsyncGPUReadback.RequestIntoNativeArray(ref values, texture, mipLevel);
            }

            public void ReadbackFrom(CommandBuffer commandBuffer, Texture texture, int mipLevel, Action<AsyncGPUReadbackRequest> callback)
            {
                var values = this[mipLevel];
                commandBuffer.RequestAsyncReadbackIntoNativeArray(ref values, texture, mipLevel, callback);
            }

            public void ReadbackFrom<T>(CommandBuffer commandBuffer, ref T renderTexture, int mipLevel) where T : IHiZRenderTexture
            {
                var values = this[mipLevel];

                renderTexture.Readback(ref values, commandBuffer);
            }

            public static implicit operator HiZDepthMapData(Lite value)
            {
                HiZDepthMapData result;
                result._0 = value._0;
                result._1 = value._1;
                result._2 = value._2;
                result._3 = value._3;
                result._4 = value._4;
                result._5 = value._5;
                result._6 = value._6;
                result._7 = value._7;
                result._8 = value._8;
                result._9 = value._9;

                return result;
            }
        }

        //1024*512
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _0;

        //512*256
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _1;

        //256*128
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _2;

        //128*64
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _3;

        //64*32
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _4;

        //32*16
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _5;

        //16*8
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _6;

        //8*4
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _7;

        //4*2
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _8;

        //2*1
        [ReadOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<half> _9;

        public NativeArray<half> this[int level]
        {
            get
            {
                switch(level)
                {
                    case 0: 
                        return _0;
                    case 1:
                        return _1;
                    case 2:
                        return _2;
                    case 3:
                        return _3;
                    case 4:
                        return _4;
                    case 5:
                        return _5;
                    case 6:
                        return _6;
                    case 7:
                        return _7;
                    case 8:
                        return _8;
                    case 9:
                        return _9;
                }

                __Throw();

                return default;
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void __Throw()
        {
            throw new IndexOutOfRangeException();
        }
    }

    public struct HiZDepthMap
    {
        [Flags]
        public enum Flag
        {
            ReversedZ = 0x01,
            UVStartsAtTop = 0x02
        }

        public struct Lite
        {
            public readonly int Width;
            public readonly int Height;
            public readonly int MipLevels;

            private Flag __flag;

            private HiZDepthMapData.Lite __values;

            private unsafe LookupJobManager* __lookupJobManager;

            public unsafe ref LookupJobManager lookupJobManager => ref *__lookupJobManager;

            public bool isCreated => __values.isCreated;

            public unsafe Lite(int width, int height, int mipLevels)
            {
                Width = width;
                Height = height;
                MipLevels = mipLevels;

                Flag flag = 0;
                if (SystemInfo.usesReversedZBuffer)
                    flag |= Flag.ReversedZ;

                if (SystemInfo.graphicsUVStartsAtTop)
                    flag |= Flag.UVStartsAtTop;

                __flag = flag;

                __values = new HiZDepthMapData.Lite(width, height, Allocator.Persistent);

                __lookupJobManager = AllocatorManager.Allocate<LookupJobManager>(Allocator.Persistent);
                *__lookupJobManager = default;
            }

            public unsafe void Dispose()
            {
                if (__lookupJobManager == null)
                    return;

                lookupJobManager.CompleteReadWriteDependency();

                __values.Dispose();

                AllocatorManager.Free(Allocator.Persistent, __lookupJobManager);

                __lookupJobManager = null;
            }

            public AsyncGPUReadbackRequest ReadbackFrom(Texture texture, int mipLevel)
            {
                return __values.ReadbackFrom(texture, mipLevel);
            }

            public void ReadbackFrom(CommandBuffer commandBuffer, Texture texture, int mipLevel, Action<AsyncGPUReadbackRequest> callback)
            {
                __values.ReadbackFrom(commandBuffer, texture, mipLevel, callback);
            }

            public void ReadbackFrom<T>(CommandBuffer commandBuffer, ref T renderTexture, int mipLevel) where T : IHiZRenderTexture
            {
                __values.ReadbackFrom(commandBuffer, ref renderTexture, mipLevel);
            }


            public static implicit operator HiZDepthMap(Lite value)
            {
                return new HiZDepthMap(value.Width, value.Height, value.MipLevels, value.__flag, value.__values);
            }
        }

        public readonly int Width;
        public readonly int Height;
        public readonly int MipLevels;

        private Flag __flag;

        private HiZDepthMapData __values;

        public bool isReversedZ => (__flag & Flag.ReversedZ) == Flag.ReversedZ;

        public bool isUVStartsAtTop => (__flag & Flag.UVStartsAtTop) == Flag.UVStartsAtTop;

        private HiZDepthMap(int width, int height, int mipLevels, Flag flag, HiZDepthMapData values)
        {
            Width = width;
            Height = height;
            MipLevels = mipLevels;

            __flag = flag;

            __values = values;
        }

        public float SampleLevel(float2 uv, int level)
        {
            level = math.clamp(level, 0, MipLevels - 1);

            int2 size = math.int2(math.max(1, Width >> level), math.max(1, Height >> level)), 
                xy = (int2)math.round(uv * size);
            xy = math.clamp(xy, int2.zero, size - 1);

            return __values[level][xy.y * size.x + xy.x];
        }
    }

    public struct HiZCullingTester : IHybridRendererCullingTester
    {
        public static readonly float3[] AggressiveExtents = new float3[8]
        {
                math.float3(1, 1, 1),
                math.float3(1, 1, -1),
                math.float3(1, -1, 1),
                math.float3(1, -1, -1),
                math.float3(-1, 1, 1),
                math.float3(-1, 1, -1),
                math.float3(-1, -1, 1),
                math.float3(-1, -1, -1)
        };

        public BatchCullingViewType batchCullingViewType;

        public float4x4 viewProjection;

        public HiZDepthMap depthMap;

        [ReadOnly]
        public NativeArray<CullingSplit> splits;

        [ReadOnly]
        public NativeArray<LocalToWorld> localToWorlds;

        [ReadOnly]
        public NativeArray<RenderBounds> renderBounds;

        public static bool TestDepth(in float3 screenMin, in float3 screenMax, in HiZDepthMap depthMap)
        {
            float4 boxUVs = math.float4(screenMin.xy, screenMax.xy);
            boxUVs = math.saturate(boxUVs * 0.5f + 0.5f);
            float2 size = (boxUVs.zw - boxUVs.xy) * math.float2(depthMap.Width, depthMap.Height);
            float mip = math.log2(math.max(size.x, size.y));
            // 离得太近，或者物体太大，占了满屏
            if (math.round(mip) >= depthMap.MipLevels)
                return true;

            int level = (int)math.ceil(mip);
            level = math.min(level, depthMap.MipLevels - 1);
            int levelLower = math.max(level - 1, 0);
            float2 scale = math.exp2(-levelLower) * math.float2(depthMap.Width, depthMap.Height);
            float2 a = math.floor(boxUVs.xy * scale);
            float2 b = math.ceil(boxUVs.zw * scale);
            float2 dims = b - a;

            // Use the lower level if we only touch <= 2 texels in both dimensions
            if (dims.x <= 2.0f && dims.y <= 2.0f)
                level = levelLower;

            if (depthMap.isUVStartsAtTop)
                boxUVs = math.float4(boxUVs.x, 1.0f - boxUVs.y, boxUVs.z, 1.0f - boxUVs.w);

            float4 depth = math.float4(
                depthMap.SampleLevel(boxUVs.xy, level),
                depthMap.SampleLevel(boxUVs.zy, level),
                depthMap.SampleLevel(boxUVs.xw, level),
                depthMap.SampleLevel(boxUVs.zw, level));

            if (depthMap.isReversedZ)
            {
                depth.xy = math.min(depth.xy, depth.zw);
                depth.x = math.min(depth.x, depth.y);
                return screenMax.z >= depth.x;
            }

            depth.xy = math.max(depth.xy, depth.zw);
            depth.x = math.max(depth.x, depth.y);
            return screenMin.z <= depth.x;
        }

        public bool Test(int entityIndex, int splitIndex)
        {
            if (batchCullingViewType != BatchCullingViewType.Camera)
                return true;

            float3 screenMin = float.MaxValue, screenMax = -float.MaxValue, center;
            float4 clipPos;
            var mvp = math.mul(viewProjection/*splits[splitIndex].cullingMatrix*/, localToWorlds[entityIndex].Value);
            var aabb = renderBounds[entityIndex].Value;
            for (int i = 0; i < 8; ++i)
            {
                center = aabb.Center + aabb.Extents * AggressiveExtents[i];
                clipPos = math.mul(mvp, math.float4(center, 1.0f));
                //clipPos.xyz /= clipPos.w;

                //clipPos.z = (clipPos.z + 1.0f) * 0.5f;
                /*if (depthMap.isReversedZ)
                    clipPos.z = 1.0f - clipPos.z;*/

                screenMin = math.min(screenMin, clipPos.xyz);
                screenMax = math.max(screenMax, clipPos.xyz);
            }

            return TestDepth(screenMin, screenMax, depthMap);
        }
    }

    public struct HiZCullingClipper : IHybridRendererCullingClipper<HiZCullingTester>
    {
        public BatchCullingViewType batchCullingViewType;

        public float4x4 viewProjection;

        public HiZDepthMap depthMap;

        [ReadOnly]
        public NativeArray<CullingSplit> splits;

        [ReadOnly]
        public ComponentTypeHandle<RenderBounds> renderBoundType;

        [ReadOnly]
        public ComponentTypeHandle<LocalToWorld> localToWorldType;

        public HybridRendererCullingResult Cull(in ArchetypeChunk chunk)
        {
            if (!chunk.Has(ref renderBoundType) || !chunk.Has(ref localToWorldType))
                return HybridRendererCullingResult.Visible;

            return HybridRendererCullingResult.PerInstance;
        }

        public HiZCullingTester CreatePerInstanceTester(in ArchetypeChunk chunk)
        {
            HiZCullingTester tester;
            tester.batchCullingViewType = batchCullingViewType;
            tester.viewProjection = viewProjection;
            tester.depthMap = depthMap;
            tester.splits = splits;
            tester.renderBounds = chunk.GetNativeArray(ref renderBoundType);
            tester.localToWorlds = chunk.GetNativeArray(ref localToWorldType);

            return tester;
        }
    }

    public struct HiZCullingCore
    {
        private ComponentTypeHandle<RenderBounds> __renderBoundType;
        private ComponentTypeHandle<LocalToWorld> __localToWorldType;

        public HiZCullingCore(ref EntityManager entityManager)
        {
            __renderBoundType = entityManager.GetComponentTypeHandle<RenderBounds>(true);
            __localToWorldType = entityManager.GetComponentTypeHandle<LocalToWorld>(true);
        }

        public JobHandle Cull(
            ref EntityManager entityManager,
            ref HybridRendererCullingContext cullingContext,
            in float4x4 viewProjection, 
            in HiZDepthMap depthMap,
            in JobHandle cullingJobDependency)
        {
            HiZCullingClipper clipper;
            clipper.batchCullingViewType = cullingContext.value.viewType;
            clipper.viewProjection = viewProjection;
            clipper.depthMap = depthMap;
            clipper.splits = cullingContext.value.cullingSplits;
            clipper.renderBoundType = __renderBoundType.UpdateAsRef(entityManager);
            clipper.localToWorldType = __localToWorldType.UpdateAsRef(entityManager);

            return cullingContext.Cull<HiZCullingTester, HiZCullingClipper>(ref clipper, cullingJobDependency);
        }
    }

    [BurstCompile, UpdateInGroup(typeof(HybridRendererCullingSystemGroup))]
    public partial struct HiZCullingSystem : ISystem
    {
        private EntityQuery __group;
        private HiZCullingCore __core;

        public void OnCreate(ref SystemState state)
        {
            __group = HybridRendererCullingState.GetEntityQuery(ref state);

            var entityManager = state.EntityManager;

            __core = new HiZCullingCore(ref entityManager);
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!HiZUtility.isActive)
                return;

            var entityManager = state.EntityManager;
            ref var cullingState = ref __group.GetSingletonRW<HybridRendererCullingState>().ValueRW;

            cullingState.cullingJobDependency = __core.Cull(
                ref entityManager,
                ref cullingState.context,
                HiZUtility.viewProjectionMatrix,
                HiZUtility.currentDepthMap, 
                JobHandle.CombineDependencies(state.Dependency, cullingState.cullingJobDependency));

            state.Dependency = cullingState.cullingJobDependency;
        }
    }

    public interface IHiZRenderTexture
    {
        void Readback(ref NativeArray<half> output, CommandBuffer commandBuffer);
    }

    public interface IHiZRenderTextureFactory<T> where T : IHiZRenderTexture
    {
        T Create(int width, int height);
    }

    public interface IHiZDepthGenerater<T> where T : IHiZRenderTexture
    {
        void Execute(in T renderTarget, CommandBuffer cmd);
    }

    public interface IHiZMipmapGenerater<T> where T : IHiZRenderTexture
    {
        void Execute(
            in T source,
            in T destination,
            CommandBuffer cmd);
    }

    /*public class HiZDMipmapGenerater : IHiZMipmapGenerater
    {
        private readonly int SrcTexID = Shader.PropertyToID("_SourceTex");
        private readonly int DestTexID = Shader.PropertyToID("_DestTex");
        private readonly int DepthRTSize = Shader.PropertyToID("_DepthRTSize");

        private ComputeShader __computeShader;
        private int __kernelIndex;

        public HiZDMipmapGenerater()
        {
            __computeShader = Resources.Load<ComputeShader>("GenerateMipmap");
            __kernelIndex = __computeShader.FindKernel("Main");
        }

        public void Execute(
            in IHiZRenderTexture source,
            in IHiZRenderTexture destination,
            CommandBuffer cmd)
        {
            cmd.SetComputeTextureParam(__computeShader, __kernelIndex, SrcTexID, source);
            cmd.SetComputeTextureParam(__computeShader, __kernelIndex, DestTexID, destination);

            cmd.SetComputeVectorParam(__computeShader, DepthRTSize, new Vector4(width, height, 0f, 0f));

            cmd.DispatchCompute(__computeShader, __kernelIndex, math.max(1, width >> 3), math.max(1, height >> 3), 1);
        }
    }*/

    public class HiZRenderTextureManager
    {
        private class RenderTextureWrapper : IHiZRenderTexture
        {
            private RenderTexture __value;
            private HiZRenderTextureManager __manager;

            public RenderTexture value => __value;

            public static RenderTextureWrapper Create(int width, int height, HiZRenderTextureManager manager)
            {
                RenderTextureWrapper result;
                if (manager.__pool == null || manager.__pool.Count < 1)
                    result = new RenderTextureWrapper();
                else
                    result = manager.__pool.Dequeue();

                result.__value = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RHalf);
                result.__manager = manager;

                return result;
            }

            public void Release()
            {
                RenderTexture.ReleaseTemporary(__value);

                __value = null;

                if (__manager.__pool == null)
                    __manager.__pool = new Queue<RenderTextureWrapper>();

                __manager.__pool.Enqueue(this);
            }

            public void Readback(ref NativeArray<half> output, CommandBuffer commandBuffer)
            {
                ++__manager.__readbackRequestCount;

                commandBuffer.RequestAsyncReadbackIntoNativeArray(ref output, __value, __ReadbackComplete);
            }

            private void __ReadbackComplete(AsyncGPUReadbackRequest request)
            {
                --__manager.__readbackRequestCount;

                Release();
            }
        }

        private Queue<RenderTextureWrapper> __pool;

        private int __readbackRequestCount;

        public bool isReadbackCompleted => __readbackRequestCount == 0;

        public IHiZRenderTexture Create(int width, int height) => RenderTextureWrapper.Create(width, height, this);
    }

    public class HiZDepthTexture
    {
        public struct RenderPass<T> where T : IHiZRenderTexture
        {
            private HiZDepthTexture __parent;

            public readonly T[] RenderTextures;

            public RenderPass(HiZDepthTexture parent, IHiZRenderTextureFactory<T> factory)
            {
                __parent = parent;

                RenderTextures = new T[__parent.__map.MipLevels];

                int width, height;
                for (int i = 0; i < __parent.__map.MipLevels; ++i)
                {
                    width = math.max(1, __parent.__map.Width >> i);
                    height = math.max(1, __parent.__map.Height >> i);

                    RenderTextures[i] = factory.Create(width, height);
                }
            }

            public void Execute<TDepthGenerater, TMipmapGenerater>(
                CommandBuffer commandBuffer,
                in Matrix4x4 viewProjectionMatrix,
                TDepthGenerater depthGenerater,
                TMipmapGenerater mipmapGenerater) 
                where TDepthGenerater : IHiZDepthGenerater<T>
                where TMipmapGenerater : IHiZMipmapGenerater<T>
            {
                HiZUtility.viewProjectionMatrix = viewProjectionMatrix;
                //__parent.viewProjectionMatrix = viewProjectionMatrix;

                var source = RenderTextures[0];

                depthGenerater.Execute(source, commandBuffer);

                __parent.__map.lookupJobManager.CompleteReadWriteDependency();

                T destination;
                for (int i = 1; i < __parent.__map.MipLevels; ++i)
                {
                    destination = RenderTextures[i];

                    mipmapGenerater.Execute(
                        source,
                        destination,
                        commandBuffer);
                    
                    __parent.__map.ReadbackFrom(commandBuffer, ref source, i - 1);

                    source = destination;
                }

                __parent.__map.ReadbackFrom(commandBuffer, ref source, __parent.__map.MipLevels > 0 ? __parent.__map.MipLevels - 1 : 0);
            }
        }

        private HiZDepthMap.Lite __map;

        public int width => __map.Width;

        public int height => __map.Height;

        public int mipLevels => __map.MipLevels;

        public HiZDepthTexture(int width, int height, int mipLevels)
        {
            __map = new HiZDepthMap.Lite(width, height, mipLevels);

            //__target = null;

            //viewProjectionMatrix = Matrix4x4.identity;
        }

        public void Dispose()
        {
            __map.Dispose();

            /*if (__target != null)
            {
                if (__target.IsCreated())
                    __target.Release();

                __target = null;
            }*/
        }

        public HiZDepthMap GetMap()
        {
            //CompleteReadbackRequests();

            return __map;
        }

        public void AddReadOnlyDependency(in JobHandle jobHandle)
        {
            __map.lookupJobManager.AddReadOnlyDependency(jobHandle);
        }
    }
}