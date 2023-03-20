using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Networking;
using static ZG.MeshStreamingUtility;

namespace ZG
{
    public interface IMeshInstanceStreamingManager
    {
        uint vertexOffset { get; }

        IEnumerator Load(string path, long pathOffset, int vertexCount, byte[] md5Hash);

        void Unload();
    }

    public abstract class MeshInstanceStreamingSettingsBase : ScriptableObject
    {
#if UNITY_EDITOR
        public const int VERSION = 0;

        public Mesh instancedMesh;

        public Mesh[] maskMeshes;

        public Shader[] supportShaders;

        public string persistentPath => Path.Combine(Application.persistentDataPath, name);

        public string streamingPath => Path.Combine(Application.streamingAssetsPath, name);

        public static unsafe void Save<T>(
            T[] vertices, 
            string path, 
            out long pathOffset, 
            out byte[] md5Hash) where T : unmanaged
        {
            var directory = Application.streamingAssetsPath;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var fileStream = File.OpenWrite(Path.Combine(directory, path)))
            {
                pathOffset = fileStream.Length;

                fileStream.Position = pathOffset;

                fixed (void* ptr = vertices)
                {
                    using (var stream = new UnmanagedMemoryStream((byte*)ptr, sizeof(T) * vertices.Length))
                    {
                        using (var md5 = new MD5CryptoServiceProvider())
                        {
                            stream.Position = 0L;

                            md5Hash = md5.ComputeHash(stream);
                        }

                        stream.Position = 0L;
                        stream.CopyTo(fileStream);
                    }
                }
            }
        }

        public abstract void Save(string path, out long pathOffset, out int vertexCount, out byte[] md5Hash);

        public bool Override(
            in Matrix4x4 matrix,
            Material material, 
            ref Mesh mesh,
            ref int submeshIndex,
            Action<MeshInstanceRendererDatabase.Instance> instances)
        {
            if (mesh == instancedMesh)
                return false;

            if (instancedMesh == null ||
                maskMeshes != null && Array.IndexOf(maskMeshes, mesh) != -1 ||
                supportShaders != null && Array.IndexOf(supportShaders, material.shader) == -1)
                return false;

            if (!_Override(instancedMesh.vertexCount, submeshIndex, mesh, matrix, instances))
                return false;

            submeshIndex = 0;
            mesh = instancedMesh;

            return true;
        }

        protected abstract bool _Override(
            int vertexCountPerInstance,
            int submeshIndex,
            Mesh mesh,
            in Matrix4x4 matrix,
            Action<MeshInstanceRendererDatabase.Instance> instances);
#endif

        public abstract IMeshInstanceStreamingManager CreateManager();

        public virtual Mesh CreateMesh(MeshInstanceStreamingDatabase database)
        {
            return null;
        }
    }

    public abstract class MeshInstanceStreamingSettings<TVertex, TMeshWrapper> : MeshInstanceStreamingSettingsBase
        where TVertex : unmanaged, IVertex
        where TMeshWrapper : struct, IMeshWrapper<Triangle<TVertex>>
    {
        private static class Shared
        {
            public static string overridePath;
            public static long overridePathOffset;
        }

        public class Manager : IMeshInstanceStreamingManager
        {
            public uint vertexOffset
            {
                get;

                private set;
            } = uint.MaxValue;

            public uint indexOffset
            {
                get;

                private set;
            } = uint.MaxValue;

            public IEnumerator Load(string path, long pathOffset, int vertexCount, byte[] md5Hash)
            {
                string persistentDataPath = Shared.overridePath ?? Path.Combine(Application.persistentDataPath, path);
                bool isExists = File.Exists(persistentDataPath);
                ///

                if (!isExists)
                {
                    using (var www = new UnityWebRequest(
                        AssetManager.GetPlatformPath(Path.Combine(Application.streamingAssetsPath, path)),
                        UnityWebRequest.kHttpVerbGET,
                        new DownloadHandlerFile(persistentDataPath),
                        null))
                    {
                        yield return www.SendWebRequest();

                        string error = www.error;
                        if (!string.IsNullOrEmpty(error))
                        {
                            Debug.LogError(error);

                            yield break;
                        }
                    }
                }

                int length = vertexCount * UnsafeUtility.SizeOf<TVertex>();
                var vertices = new byte[length];

                using (var fileStream = File.OpenRead(persistentDataPath))
                {
                    fileStream.Position = pathOffset + Shared.overridePathOffset;

                    using (var task = fileStream.ReadAsync(vertices, 0, length))
                    {
                        do
                        {
                            yield return null;

                            var exception = task.Exception;
                            if (exception != null)
                            {
                                Debug.LogException(exception.InnerException ?? exception);

                                yield break;
                            }
                        } while (!task.IsCompleted);
                    }
                }

                this.vertexOffset = MeshStreamingSharedData<TVertex>.Alloc(vertices, 0, length);
            }

            public IEnumerator Load(
                string path, 
                long pathVertexOffset,
                long pathIndexOffset,
                int vertexCount, 
                int indexCount, 
                byte[] md5Hash)
            {
                string persistentDataPath = Shared.overridePath ?? Path.Combine(Application.persistentDataPath, path);
                bool isExists = File.Exists(persistentDataPath);
                ///

                if (!isExists)
                {
                    using (var www = new UnityWebRequest(
                        AssetManager.GetPlatformPath(Path.Combine(Application.streamingAssetsPath, path)),
                        UnityWebRequest.kHttpVerbGET,
                        new DownloadHandlerFile(persistentDataPath),
                        null))
                    {
                        yield return www.SendWebRequest();

                        string error = www.error;
                        if (!string.IsNullOrEmpty(error))
                        {
                            Debug.LogError(error);

                            yield break;
                        }
                    }
                }

                long overridePathOffset = Shared.overridePathOffset;

                using (var fileStream = File.OpenRead(persistentDataPath))
                {
                    fileStream.Position = pathVertexOffset + overridePathOffset;

                    int vertexLength = vertexCount * UnsafeUtility.SizeOf<TVertex>();
                    var vertices = new byte[vertexLength];
                    using (var task = fileStream.ReadAsync(vertices, 0, vertexLength))
                    {
                        do
                        {
                            yield return null;

                            var exception = task.Exception;
                            if (exception != null)
                            {
                                Debug.LogException(exception.InnerException ?? exception);

                                yield break;
                            }
                        } while (!task.IsCompleted);
                    }
                    this.vertexOffset = MeshStreamingSharedData<TVertex>.Alloc(vertices, 0, vertexLength);

                    fileStream.Position = pathIndexOffset + overridePathOffset;

                    int indexLength = indexCount * UnsafeUtility.SizeOf<UInt32>();
                    var indices = new byte[indexLength];

                    using (var task = fileStream.ReadAsync(indices, 0, indexLength))
                    {
                        do
                        {
                            yield return null;

                            var exception = task.Exception;
                            if (exception != null)
                            {
                                Debug.LogException(exception.InnerException ?? exception);

                                yield break;
                            }
                        } while (!task.IsCompleted);
                    }

                    this.indexOffset = MeshStreamingSharedData<UInt32>.Alloc(indices, 0, indexLength);
                }
            }

            public void Unload()
            {
                /*if (__gcHandle != 0)
                {
                    UnsafeUtility.ReleaseGCObject(__gcHandle);

                    __gcHandle = 0;
                }*/

                if (MeshStreamingSharedData<TVertex>.Free(vertexOffset))
                    vertexOffset = uint.MaxValue;

                if(indexOffset < uint.MaxValue && MeshStreamingSharedData<UInt32>.Free(indexOffset))
                    indexOffset = uint.MaxValue;
            }
        }

        public static string overridePath
        {
            get => Shared.overridePath;

            set => Shared.overridePath = value;
        }

        public static long overridePathOffset
        {
            get => Shared.overridePathOffset;

            set => Shared.overridePathOffset = value;
        }

#if UNITY_EDITOR
        private NativeList<TVertex> __vertices;

        public abstract ref TMeshWrapper meshWrapper { get; }

        public override void Save(
            string path, 
            out long pathOffset, 
            out int vertexCount,
            out byte[] md5Hash)
        {
            if (!__vertices.IsCreated)
            {
                pathOffset = -1L;
                vertexCount = 0;
                md5Hash = null;

                return;
            }

            vertexCount = __vertices.Length;

            Save(__vertices.AsArray().ToArray(), path, out pathOffset, out md5Hash);

            __vertices.Dispose();
        }

        protected override bool _Override(
            int vertexCountPerInstance, 
            int submeshIndex, 
            Mesh mesh,
            in Matrix4x4 matrix, 
            Action<MeshInstanceRendererDatabase.Instance> results)
        {
            if (!__vertices.IsCreated)
                __vertices = new NativeList<TVertex>(Allocator.Persistent);

            var meshArray = Mesh.AcquireReadOnlyMeshData(mesh);

            SubMesh subMesh;
            subMesh.index = submeshIndex;
            subMesh.meshIndex = 0;
            subMesh.matrix = matrix;

            var subMeshes = new NativeArray<SubMesh>(1, Allocator.Temp);
            subMeshes[0] = subMesh;

            var instances = new NativeList<Instance>(Allocator.Temp);

            float rate = BuildInstances(
                vertexCountPerInstance, 
                meshArray,
                subMeshes, 
                ref __vertices, 
                ref instances,
                ref meshWrapper);

            MeshInstanceRendererDatabase.Instance result;
            result.bounds = default;
            result.matrix = Matrix4x4.identity;
            foreach (var instance in instances)
            {
                result.meshStreamingOffset = instance.vertexOffset;

                result.bounds.SetMinMax(instance.aabb.Min, instance.aabb.Max);

                results(result);
            }

            instances.Dispose();
            subMeshes.Dispose();
            meshArray.Dispose();

            Debug.Log($"Rate: {rate}", this);

            return rate > Mathf.Epsilon;
        }
#endif

        public unsafe static void Compress(UInt32 version, Stream inputStream, Stream outputStream)
        {
            int byteCount = sizeof(TVertex);
            var bytes = new byte[byteCount];

            var verticesIndices = new Dictionary<TVertex, int>();
            var indices = new List<uint>();
            int vertexIndex;
            TVertex vertex;
            long position = inputStream.Position, length = inputStream.Length;
            while (length > inputStream.Position)
            {
                inputStream.Read(bytes, 0, byteCount);

                fixed (void* ptr = bytes)
                {
                    vertex = *(TVertex*)ptr;
                    if (!verticesIndices.TryGetValue(vertex, out vertexIndex))
                    {
                        vertexIndex = verticesIndices.Count;
                        verticesIndices[vertex] = vertexIndex;
                    }

                    indices.Add((uint)vertexIndex);
                }
            }

            int numIndices = indices.Count;
            UInt32[] indicesResult = new UInt32[numIndices + 2];
            indicesResult[0] = version;
            indicesResult[1] = (UInt32)numIndices;
            indices.CopyTo(indicesResult, 1);

            fixed (void* ptr = indicesResult)
            {
                using (var stream = new UnmanagedMemoryStream((byte*)ptr, sizeof(UInt32) * indicesResult.Length))
                {
                    stream.CopyTo(outputStream);
                }
            }

            var vertices = new TVertex[verticesIndices.Count];
            foreach (var pair in verticesIndices)
                vertices[pair.Value] = pair.Key;

            fixed (void* ptr = vertices)
            {
                using (var stream = new UnmanagedMemoryStream((byte*)ptr, sizeof(TVertex) * vertices.Length))
                {
                    stream.CopyTo(outputStream);
                }
            }
        }

        public unsafe static void Decompress(
            Stream inputStream, 
            Stream outputStream, 
            out UInt32 vertexCount, 
            out UInt32 i)
        {
            int uintByteCount = sizeof(UInt32), vertexByteCount = sizeof(TVertex);
            byte[] bytes = new byte[Math.Max(uintByteCount, vertexByteCount)];

            inputStream.Read(bytes, 0, uintByteCount);
            outputStream.Write(bytes, 0, uintByteCount);

            inputStream.Read(bytes, 0, uintByteCount);
            fixed(void* ptr = bytes)
            {
                vertexCount = *(UInt32*)ptr;
            }

            long position;
            UInt32 vertexOffset = (vertexCount + 1) * sizeof(UInt32), index;
            for(i = 0; i < vertexCount; ++i)
            {
                inputStream.Read(bytes, 0, uintByteCount);
                fixed (void* ptr = bytes)
                {
                    index = *(UInt32*)ptr;
                }

                position = inputStream.Position;
                inputStream.Position = index * sizeof(TVertex) + vertexOffset;

                inputStream.CopyTo(outputStream, vertexByteCount);

                inputStream.Position = position;
            }
        }

        public static void Decompress(
            string inputPath, 
            string outputPath, 
            out uint vertexCount, 
            out uint i)
        {
            const string PATH_TEMP = ".tmp";

            string tempPath = outputPath + PATH_TEMP;
            while (File.Exists(tempPath))
                tempPath = tempPath + PATH_TEMP;

            using(var inputStream = File.OpenRead(inputPath))
            using (var outputStream = File.OpenWrite(tempPath))
                Decompress(inputStream, outputStream, out vertexCount, out i);

            File.Copy(tempPath, outputPath, true);
            File.Delete(tempPath);
        }

        public static bool IsDecompressed(uint version, string path)
        {
            if (File.Exists(path))
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadUInt32() == version)
                        return true;
                }
            }

            return false;
        }

        public override IMeshInstanceStreamingManager CreateManager() => new Manager();

        private static unsafe UnmanagedMemoryStream __ConvertTo<T>(T[] values, out ulong gcHandle) where T : unmanaged
        {
            void* ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(values, out gcHandle);

            return new UnmanagedMemoryStream((byte*)ptr, 0, sizeof(T) * values.Length, FileAccess.Write);
        }
    }
}