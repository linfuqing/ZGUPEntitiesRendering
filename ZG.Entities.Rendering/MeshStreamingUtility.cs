using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
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
        public uint value;
    }

    public static class MeshStreamingSharedData<T> where T : struct
    {
        const int DEFAULT_SIZE = 2048;
        const string NAME_PREFIX = "_MeshStreaming";

        public static readonly string ShaderName = $"{NAME_PREFIX}{typeof(T).Name}Data";

        public static readonly int ShaderID = Shader.PropertyToID(ShaderName);

        private static ComputeBuffer __structBuffer;
        private static MemoryWrapper __memoryWrapper;

        static MeshStreamingSharedData()
        {
            __structBuffer = new ComputeBuffer(DEFAULT_SIZE, UnsafeUtility.SizeOf<T>(), ComputeBufferType.Default);

            Shader.SetGlobalBuffer(ShaderID, __structBuffer);

            __memoryWrapper = new MemoryWrapper();
            //__memoryWrapper.Alloc(1023);
        }

        public static T[] GetData(int startIndex, int count)
        {
            var values = new T[count];

            __structBuffer.GetData(values, 0, startIndex, count);

            return values;
        }

        public static unsafe uint Alloc<U>(U[] data, int startIndex, int count) where U : unmanaged
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
        }

        public static bool Free(uint offset)
        {
            return __memoryWrapper.Free((int)offset);
        }

        private static int __Alloc(int count)
        {
            int offset = __memoryWrapper.Alloc(count),
                length = __memoryWrapper.length,
                bufferCount = __structBuffer.count;
            if (bufferCount < length)
            {
                var values = new T[bufferCount];
                __structBuffer.GetData(values);

                __structBuffer.Dispose();

                __structBuffer = new ComputeBuffer(length, UnsafeUtility.SizeOf<T>(), ComputeBufferType.Default);

                Shader.SetGlobalBuffer(ShaderID, __structBuffer);

                __structBuffer.SetData(values, 0, 0, bufferCount);
            }

            return offset;
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

        public struct Triangle<T> : IKDTreeValue where T : IVertex
        {
            public T x;
            public T y;
            public T z;

            public float Get(int dimension)
            {
                return ((x.position + y.position + z.position) / 3.0f)[dimension];
            }
        }

        public static unsafe float BuildInstances<TVertex, TMeshWrapper>(
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

            var polygonArray = sources.AsArray().Reinterpret<Triangle<TVertex>>();

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
            NativeParallelHashSet<NativeKDTreeNode<Triangle<TVertex>>> parents = default;
            while (node.isCreated)
            {
                UnityEngine.Assertions.Assert.AreEqual(depth, node.GetDepth());

                parent = node.parent;

                instance.vertexOffset = vertices.Length;

                UnityEngine.Assertions.Assert.AreEqual(0, instance.vertexOffset % vertexCountPerInstance);

                foreach (var child in node)
                {
                    trinagle = child.value;

                    vertices.AddRange(UnsafeUtility.AddressOf(ref trinagle), 3);
                }
                 
                numVertices = vertices.Length - instance.vertexOffset;

                UnityEngine.Assertions.Assert.AreEqual(node.CountOfChildren(), numVertices / 3);

                if (numVertices < vertexCountPerInstance)
                {
                    if (!parents.IsCreated)
                        parents = new NativeParallelHashSet<NativeKDTreeNode<Triangle<TVertex>>>(1, Allocator.Temp);

                    for (i = numVertices; i < vertexCountPerInstance; i += 3)
                    {
                        while (parent.isCreated && !parents.Add(parent))
                            parent = parent.parent;

                        if (!parent.isCreated)
                            break;

                        trinagle = parent.value;

                        vertices.AddRange(UnsafeUtility.AddressOf(ref trinagle), 3);

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
                        ref var vertex = ref vertices.ElementAt(instance.vertexOffset);
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

                            vertices.AddRange(UnsafeUtility.AddressOf(ref trinagle), 3);

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
                        ref var vertex = ref vertices.ElementAt(instance.vertexOffset);
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
            using (var attributes = new NativeArray<VertexAttributeDescriptor>(0, Allocator.Temp, NativeArrayOptions.ClearMemory))
                //attributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 1, 0);
                mesh.SetVertexBufferParams(vertexCountPerInstance, attributes);
            //attributes.Dispose();

            mesh.SetIndexBufferParams(vertexCountPerInstance, IndexFormat.UInt16);
            var indices = mesh.GetIndexData<ushort>();

            for (ushort i = 0; i < vertexCountPerInstance; ++i)
                indices[i] = i;

            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCountPerInstance), MeshUpdateFlags.DontRecalculateBounds);
        }
    }
}
