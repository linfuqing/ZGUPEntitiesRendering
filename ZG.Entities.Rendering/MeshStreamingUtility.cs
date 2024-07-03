using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Jobs;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZG
{
    public interface IMeshStreamingVertex<T>
    {
        bool Combine(in T x);
    }

    [MaterialProperty("_MeshStreamingVertexOffset")]
    public struct MeshStreamingVertexOffset : IComponentData
    {
        private uint __value;

        public uint value
        {
            get => __value;//(__value.x << 0) | (__value.y << 8) | (__value.z << 16) | (__value.w << 24);
            set => __value = new MeshStreamingVertexOffset(value).__value;
        }

        public MeshStreamingVertexOffset(uint value)
        {
            __value = value;
            /*__value.x = (value >> 0) & 0xff;
            __value.y = (value >> 8) & 0xff;
            __value.z = (value >> 16) & 0xff;
            __value.w = (value >> 24) & 0xff;*/
        }
    }

    public static class MeshStreamingSharedData<T> where T : unmanaged
    {
        private struct Job : IDisposable
        {
            [Unity.Burst.BurstCompile]
            private struct Copy : IJob
            {
                public long dropOffset;

                public long dropLength;

                public long length;

                [NativeDisableUnsafePtrRestriction]
                public unsafe void* src;

                [NativeDisableUnsafePtrRestriction]
                public unsafe void* dsc;

                public unsafe void Execute()
                {
                    if(dropOffset > 0)
                        UnsafeUtility.MemCpy(dsc, src, math.min(dropOffset, length));

                    long offset = dropOffset + dropLength;
                    if(offset < length)
                        UnsafeUtility.MemCpy((byte*)dsc + offset, (byte*)src + offset, length - offset);
                }
            }

            private ulong __gcHandle;
            private JobHandle __jobHandle;

            public unsafe Job(int offset, int count, T[] src, ref NativeArray<T> dsc)
            {
                int size = UnsafeUtility.SizeOf<T>();
                
                /*fixed(void* ptr = src)
                    UnsafeUtility.MemCpy(dsc.GetUnsafePtr(), ptr, src.Length * size);

                __jobHandle = default;
                __gcHandle = 0;
                return;*/
                
                Copy copy;
                copy.dropOffset = offset * size;
                copy.dropLength = count * size;
                copy.length = src.Length * size;
                copy.src = UnsafeUtility.PinGCArrayAndGetDataAddress(src, out __gcHandle);
                copy.dsc = dsc.GetUnsafePtr();
                
                __jobHandle = copy.ScheduleByRef();
            }

            public void Dispose()
            {
                __jobHandle.Complete();

                __jobHandle = default;

                UnsafeUtility.ReleaseGCObject(__gcHandle);

                __gcHandle = 0;
            }
        }

        const int DEFAULT_SIZE = 4 * 1024 * 1024;
        const string NAME_PREFIX = "_MeshStreaming";

        public static readonly string ShaderName = $"{NAME_PREFIX}{typeof(T).Name}Data";

        public static readonly int ShaderID = Shader.PropertyToID(ShaderName);

        private static GraphicsBuffer __structBuffer;
        private static MemoryWrapper __memoryWrapper;

        private static Job? __job;

        public static bool isLocking
        {
            get;

            private set;
        }

        static MeshStreamingSharedData()
        {
            //Debug.LogError(SystemInfo.maxGraphicsBufferSize / 1024 / 1024);
            __structBuffer = __CreateGraphicsBuffer(DEFAULT_SIZE);

            Shader.SetGlobalBuffer(ShaderID, __structBuffer);

            __memoryWrapper = new MemoryWrapper();
            //__memoryWrapper.Alloc(1023);
        }

        /*[RuntimeDispose]
        public static void Dispose()
        {
            __structBuffer.Dispose();
        }*/

        public static T[] GetData(int startIndex, int count)
        {
            var values = new T[count];

            __structBuffer.GetData(values, 0, startIndex, count);

            return values;
        }
        
        public static bool BeginAlloc(int count, out int offset, out NativeArray<T> buffer, out bool isResize)
        {
            if (isLocking)
            {
                offset = 0;

                buffer = default;

                isResize = false;
                
                return false;
            }

            isLocking = true;
            
            offset = __Alloc(count, out buffer, out isResize);

            return true;
        }

        public static bool EndAlloc(int countToWritten)
        {
            if (!isLocking)
                return false;

            if (__job != null)
            {
                var job = __job.Value;
                
                countToWritten = __structBuffer.count;
                
                job.Dispose();

                __job = null;
            }
            
            __structBuffer.UnlockBufferAfterWrite<T>(countToWritten);

            isLocking = false;

            return true;
        }

        /*public static unsafe uint Alloc<U>(U[] data, int startIndex, int count) where U : unmanaged
        {
            int vertexCount = count * UnsafeUtility.SizeOf<U>() / UnsafeUtility.SizeOf<T>(),
                offset = __Alloc(vertexCount);
            fixed (void* ptr = data)
            {
                int vertexStartIndex = startIndex * UnsafeUtility.SizeOf<U>() / UnsafeUtility.SizeOf<T>();

                var values = Unsafe.CollectionUtility.ToNativeArray<T>(ptr, vertexCount);
                __structBuffer.SetData(values, vertexStartIndex, offset, vertexCount);
            }

            return (uint)offset;
        }*/

        public static bool Free(uint offset)
        {
            //EndAlloc(0);
            
            return __memoryWrapper.Free((int)offset);
        }

        private static int __Alloc(int count, out NativeArray<T> buffer, out bool isResize)
        {
            int offset = __memoryWrapper.Alloc(count),
                length = __memoryWrapper.length,
                bufferCount = __structBuffer.count;
            isResize = bufferCount < length;
            if (isResize)
            {
                Debug.LogError($"Resize Graphics Buffer {bufferCount} : {length}");
                
                var values = new T[bufferCount];
                __structBuffer.GetData(values);

                __structBuffer.Release();

                __structBuffer = __CreateGraphicsBuffer(length);

                Shader.SetGlobalBuffer(ShaderID, __structBuffer);

                //__structBuffer.SetData(values, 0, 0, bufferCount);

                buffer = __structBuffer.LockBufferForWrite<T>(0, length);

                __job = new Job(offset, count, values, ref buffer);

                buffer = buffer.GetSubArray(offset, count);

                //buffer = __structBuffer.LockBufferForWrite<T>(offset, count);
            }
            else
                buffer = __structBuffer.LockBufferForWrite<T>(offset, count);

            return offset;
        }

        private static GraphicsBuffer __CreateGraphicsBuffer(int count)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, 
                GraphicsBuffer.UsageFlags.LockBufferForWrite, 
                count, 
                UnsafeUtility.SizeOf<T>());
        }
    }

    public static class MeshStreamingUtility
    {
        public interface IVertex
        {
            float3 position { get; }

            void Clear();
        }

        public interface IMeshWrapper<T> where T : unmanaged
        {
            void GetPolygons(int subMesh, in float4x4 matrix, in Mesh.MeshData mesh, ref NativeList<T> values);
        }

        public struct SubMesh
        {
            public int index;
            public int meshIndex;
            public float4x4 matrix;
        }

        public struct Instance
        {
            public int vertexOffset;
            public MinMaxAABB aabb;
        }

        public struct Triangle<T> : IKDTreeValue where T : unmanaged, IVertex
        {
            public T x;
            public T y;
            public T z;

            public float Get(int dimension)
            {
                return ((x.position + y.position + z.position) / 3.0f)[dimension];
            }

            public void Push(ref NativeList<T> result)
            {
                result.Add(x);
                result.Add(y);
                result.Add(z);
            }
        }

        public static float BuildInstances<TVertex, TMeshWrapper>(
            int vertexCountPerInstance,
            in Mesh.MeshDataArray meshes,
            in NativeArray<SubMesh> subMeshes,
            ref NativeList<TVertex> vertices,
            ref NativeList<Instance> instances,
            ref TMeshWrapper meshWrapper)
            where TVertex : unmanaged, IVertex
            where TMeshWrapper : struct, IMeshWrapper<Triangle<TVertex>>
        {
            int i, numSubMeshes = subMeshes.Length;
            SubMesh subMesh;
            var sources = new NativeList<Triangle<TVertex>>(Allocator.Temp);
            for (i = 0; i < numSubMeshes; ++i)
            {
                subMesh = subMeshes[i];

                meshWrapper.GetPolygons(
                    subMesh.index,
                    subMesh.matrix,
                    meshes[subMesh.meshIndex],
                    ref sources);
            }

            int triangleCount = sources.Length;
            if(triangleCount < 1)
            {
                sources.Dispose();

                return 0.0f;
            }

            var destinations = new NativeKDTree<Triangle<TVertex>>(3, Allocator.Temp);

            var polygonArray = sources.AsArray();//.Reinterpret<Triangle<TVertex>>();

            destinations.Insert(ref polygonArray, KDTreeInserMethod.Variance);

            UnityEngine.Assertions.Assert.AreEqual(triangleCount, destinations.count);

            sources.Dispose();

            int j, minPolygonCountShift = (int)math.floor(math.log2(vertexCountPerInstance * 0.5f / 3.0f)),
                depth = math.max(destinations.depth - minPolygonCountShift, 0), 
                sourceVertexCount = 0,
                destinationVertexCount = 0,
                numVertices;
            Instance instance;
            Triangle<TVertex> trinagle;
            NativeKDTreeNode<Triangle<TVertex>> parent, node = destinations.root.GetBackwardLeaf(ref depth);
            UnsafeHashSet<NativeKDTreeNode<Triangle<TVertex>>> parents = default;
            while (node.isCreated)
            {
                UnityEngine.Assertions.Assert.AreEqual(depth, node.GetDepth());

                parent = node.parent;

                instance.vertexOffset = vertices.Length;

                UnityEngine.Assertions.Assert.AreEqual(0, instance.vertexOffset % vertexCountPerInstance);

                foreach (var child in node)
                {
                    trinagle = child.value;

                    trinagle.Push(ref vertices);

                    //vertices.AddRange(UnsafeUtility.AddressOf(ref trinagle), 3);
                }
                 
                numVertices = vertices.Length - instance.vertexOffset;

                UnityEngine.Assertions.Assert.AreEqual(node.CountOfChildren(), numVertices / 3);

                if (numVertices < vertexCountPerInstance)
                {
                    if (!parents.IsCreated)
                        parents = new UnsafeHashSet<NativeKDTreeNode<Triangle<TVertex>>>(1, Allocator.Temp);

                    for (i = numVertices; i < vertexCountPerInstance; i += 3)
                    {
                        while (parent.isCreated && !parents.Add(parent))
                            parent = parent.parent;

                        if (!parent.isCreated)
                            break;

                        trinagle = parent.value;

                        trinagle.Push(ref vertices);

                        //vertices.AddRange(UnsafeUtility.AddressOf(ref trinagle), 3);

                        parent = parent.parent;
                    }
                }
                else
                    UnityEngine.Assertions.Assert.IsFalse(numVertices > vertexCountPerInstance);

                numVertices = vertices.Length - instance.vertexOffset;
                if (numVertices > 0)
                {
                    instance.aabb = MinMaxAABB.Empty;
                    for (i = 0; i < numVertices; ++i)
                        instance.aabb.Encapsulate(vertices[instance.vertexOffset + i].position);

                    if (numVertices < vertexCountPerInstance)
                    {
                        var vertex = vertices[instance.vertexOffset];
                        for (i = numVertices; i < vertexCountPerInstance; ++i)
                        {
                            vertices.Add(vertex);

                            vertices.ElementAt(vertices.Length - 1).Clear();
                        }
                    }
                    else
                        UnityEngine.Assertions.Assert.IsFalse(numVertices > vertexCountPerInstance);

                    instances.Add(instance);

                    sourceVertexCount += numVertices;

                    UnityEngine.Assertions.Assert.IsTrue(triangleCount * 3 >= sourceVertexCount);

                    destinationVertexCount += vertexCountPerInstance;
                }

                node = node.siblingForward;
            }

            if (depth > 0)
            {
                instance.vertexOffset = vertices.Length;

                UnityEngine.Assertions.Assert.AreEqual(0, instance.vertexOffset % vertexCountPerInstance);
                for (i = depth - 1; i >= 0; --i)
                {
                    node = destinations.root.GetBackwardLeaf(ref i);

                    while (node.isCreated)
                    {
                        UnityEngine.Assertions.Assert.AreEqual(i, node.GetDepth());

                        if (/*node.isLeaf && */parents.Add(node))
                        {
                            //UnityEngine.Assertions.Assert.IsTrue(node.isLeaf);

                            trinagle = node.value;

                            trinagle.Push(ref vertices);

                            //vertices.AddRange(UnsafeUtility.AddressOf(ref trinagle), 3);

                            numVertices = vertices.Length - instance.vertexOffset;
                            if (numVertices >= vertexCountPerInstance)
                            {
                                UnityEngine.Assertions.Assert.AreEqual(vertexCountPerInstance, numVertices);

                                instance.aabb = MinMaxAABB.Empty;
                                for (j = 0; j < numVertices; ++j)
                                    instance.aabb.Encapsulate(vertices[instance.vertexOffset + j].position);

                                instances.Add(instance);

                                sourceVertexCount += numVertices;

                                UnityEngine.Assertions.Assert.IsTrue(triangleCount * 3 >= sourceVertexCount);

                                destinationVertexCount += vertexCountPerInstance;

                                instance.vertexOffset += numVertices;
                            }
                        }

                        node = node.siblingForward;
                    }
                }

                numVertices = vertices.Length - instance.vertexOffset;
                if (numVertices > 0)
                {
                    instance.aabb = MinMaxAABB.Empty;
                    for (i = 0; i < numVertices; ++i)
                        instance.aabb.Encapsulate(vertices[instance.vertexOffset + i].position);

                    if (numVertices < vertexCountPerInstance)
                    {
                        var vertex = vertices[instance.vertexOffset];
                        for (i = numVertices; i < vertexCountPerInstance; ++i)
                        {
                            vertices.Add(vertex);

                            vertices.ElementAt(vertices.Length - 1).Clear();
                        }
                    }
                    else
                        UnityEngine.Assertions.Assert.IsFalse(numVertices > vertexCountPerInstance);

                    instances.Add(instance);

                    sourceVertexCount += numVertices;

                    UnityEngine.Assertions.Assert.IsTrue(triangleCount * 3 >= sourceVertexCount);

                    destinationVertexCount += vertexCountPerInstance;
                }
            }

            if (parents.IsCreated)
                parents.Dispose();

            destinations.Dispose();

            UnityEngine.Assertions.Assert.AreEqual(triangleCount * 3, sourceVertexCount);

            return destinationVertexCount * 1.0f / sourceVertexCount;
        }

        public static void BuildInstancedMesh(
            ushort vertexCountPerInstance,
            ref Mesh.MeshData mesh)
        {
            var attributes =
                new NativeArray<VertexAttributeDescriptor>(0, Allocator.Temp, NativeArrayOptions.ClearMemory);
            //attributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 1, 0);
            mesh.SetVertexBufferParams(vertexCountPerInstance, attributes);
            attributes.Dispose();

            mesh.SetIndexBufferParams(vertexCountPerInstance, IndexFormat.UInt16);
            var indices = mesh.GetIndexData<ushort>();

            for (ushort i = 0; i < vertexCountPerInstance; ++i)
                indices[i] = i;

            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCountPerInstance), MeshUpdateFlags.DontRecalculateBounds);
        }
    }
}
