using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using HybridRenderer = UnityEngine.Renderer;

namespace ZG
{
    [CreateAssetMenu(fileName = "Mesh Instance Renderer Database", menuName = "ZG/Mesh Instance/Renderer Database")]
    public partial class MeshInstanceRendererDatabase : MeshInstanceDatabase<MeshInstanceRendererDatabase>, ISerializationCallbackReceiver
    {
        [Flags]
        public enum InitType
        {
            Materials = 0x01,
            Meshes = 0x02,
            TypeIndices = 0x04,
            ComponentTypes = 0x08
        }

        [Serializable]
        public struct ComponentTypeWrapper : IEquatable<ComponentTypeWrapper>
        {
            public int[] typeIndices;

            public ComponentTypeWrapper(int[] typeIndices)
            {
                this.typeIndices = typeIndices;
            }

            public ComponentTypeSet ToComponentTypes(TypeIndex[] typeIndices)
            {
                int numTypeIndices = this.typeIndices.Length;
                ComponentType[] componentTypes = new ComponentType[numTypeIndices];
                for (int i = 0; i < numTypeIndices; ++i)
                    componentTypes[i].TypeIndex = typeIndices[this.typeIndices[i]];

                return new ComponentTypeSet(componentTypes);
            }

            public bool Equals(ComponentTypeWrapper other)
            {
                bool isContains;
                foreach (int sourceTypeIndex in typeIndices)
                {
                    isContains = false;
                    foreach (int destinationTypeIndex in other.typeIndices)
                    {
                        if (destinationTypeIndex == sourceTypeIndex)
                        {
                            isContains = true;

                            break;
                        }
                    }

                    if (!isContains)
                        return false;
                }

                return true;
            }

            public override int GetHashCode()
            {
                return (typeIndices == null ? 0 : typeIndices.Length);
            }
        }

        public const int VERSION = 0;

        [SerializeField, HideInInspector]
        private byte[] __bytes;

        [SerializeField]
        internal Material[] _materials;

        [SerializeField]
        internal Mesh[] _meshes;

        [SerializeField]
        internal string[] _types;

        [SerializeField]
        internal ComponentTypeWrapper[] _componentTypeWrappers;

        private InitType __initType;

        private TypeIndex[] __typeIndices;

        private BlobAssetReference<MeshInstanceRendererDefinition> __definition;

        public override int instanceID => __definition.IsCreated ? __definition.Value.instanceID : 0;

        public BlobAssetReference<MeshInstanceRendererDefinition> definition
        {
            get
            {
                Init();

                //UnityEngine.Debug.Log($"{name} : {__definition.Value.instanceID}");

                return __definition;
            }
        }

        public IReadOnlyList<Mesh> meshes => _meshes;

        public IReadOnlyList<Material> materials => _materials;

        public bool isVail
        {
            get
            {
                foreach (var mesh in _meshes)
                {
                    if (mesh == null)
                        return false;
                }

                foreach (var material in _materials)
                {
                    if (material == null || material.shader == null)
                        return false;
                }

                return true;
            }
        }

        public static bool IsMaterialTransparent(Material material)
        {
            if (material == null)
                return false;

#if HDRP_10_0_0_OR_NEWER
            // Material.GetSurfaceType() is not public, so we try to do what it does internally.
            const int kSurfaceTypeTransparent = 1; // Corresponds to non-public SurfaceType.Transparent
            if (material.HasProperty(kSurfaceTypeHDRPNameID))
                return (int) material.GetFloat(kSurfaceTypeHDRPNameID) == kSurfaceTypeTransparent;
            else
                return false;
#elif URP_10_0_0_OR_NEWER
            const int kSurfaceTypeTransparent = 1; // Corresponds to SurfaceType.Transparent
            if (material.HasProperty(kSurfaceTypeURPNameID))
                return (int) material.GetFloat(kSurfaceTypeURPNameID) == kSurfaceTypeTransparent;
            else
                return false;
#else
            return false;
#endif
        }

        protected override void _Init()
        {
            __InitMaterials();
            __InitMeshes();
            __InitTypeIndices();
            __InitComponentTypes();
        }

        protected override void _Destroy()
        {
            if (!__definition.IsCreated)
                return;

            int instanceID = __definition.Value.instanceID;

            if ((__initType & InitType.Materials) == InitType.Materials)
            {
                var instance = SingletonAssetContainer<MeshInstanceMaterialAsset>.instance;

                SingletonAssetContainerHandle handle;
                handle.instanceID = instanceID;

                int numMaterials = _materials.Length;
                for (int i = 0; i < numMaterials; ++i)
                {
                    handle.index = i;

                    MeshInstanceRendererSharedUtility.UnregisterMaterial(instance[handle]);

                    instance.Delete(handle);
                }
            }

            if ((__initType & InitType.Meshes) == InitType.Meshes)
            {
                var instance = SingletonAssetContainer<MeshInstanceMeshAsset>.instance;

                SingletonAssetContainerHandle handle;
                handle.instanceID = instanceID;

                int numMeshes = _meshes.Length;
                for (int i = 0; i < numMeshes; ++i)
                {
                    handle.index = i;

                    MeshInstanceRendererSharedUtility.UnregisterMesh(instance[handle]);

                    instance.Delete(handle);
                }
            }

            if ((__initType & InitType.TypeIndices) == InitType.TypeIndices)
            {
                int length = __typeIndices == null ? 0 : __typeIndices.Length;
                if (length > 0)
                {
                    var container = SingletonAssetContainer<int>.instance;

                    SingletonAssetContainerHandle handle;
                    handle.instanceID = instanceID;

                    for (int i = 0; i < length; ++i)
                    {
                        handle.index = i;

                        container.Delete(handle);
                    }
                }

                __typeIndices = null;
            }

            if ((__initType & InitType.ComponentTypes) == InitType.ComponentTypes)
            {
                int length = _componentTypeWrappers == null ? 0 : _componentTypeWrappers.Length;
                if (length > 0)
                {
                    var container = SingletonAssetContainer<ComponentTypeSet>.instance;

                    SingletonAssetContainerHandle handle;
                    handle.instanceID = instanceID;

                    for (int i = 0; i < length; ++i)
                    {
                        handle.index = i;

                        container.Delete(handle);
                    }
                }
            }

            __initType = 0;
        }

        protected override void _Dispose()
        {
            if (__definition.IsCreated)
            {
                __definition.Dispose();

                __definition = BlobAssetReference<MeshInstanceRendererDefinition>.Null;
            }
        }

        private void __InitMaterials()
        {
            if ((__initType & InitType.Materials) != InitType.Materials)
            {
                __initType |= InitType.Materials;

                SingletonAssetContainerHandle handle;
                handle.instanceID = __definition.Value.instanceID;

                var instance = SingletonAssetContainer<MeshInstanceMaterialAsset>.instance;

                int numMaterials = _materials.Length;
                for (int i = 0; i < numMaterials; ++i)
                {
                    handle.index = i;
                    instance[handle] = MeshInstanceRendererSharedUtility.RegisterMaterial(_materials[i]);
                }
            }
        }

        private void __InitMeshes()
        {
            if ((__initType & InitType.Meshes) != InitType.Meshes)
            {
                __initType |= InitType.Meshes;

                SingletonAssetContainerHandle handle;
                handle.instanceID = __definition.Value.instanceID;

                var instance = SingletonAssetContainer<MeshInstanceMeshAsset>.instance;

                int numMeshes = _meshes.Length;
                for (int i = 0; i < numMeshes; ++i)
                {
                    handle.index = i;
                    instance[handle] = MeshInstanceRendererSharedUtility.RegisterMesh(_meshes[i]);
                }
            }
        }

        private void __InitTypeIndices()
        {
            if ((__initType & InitType.TypeIndices) != InitType.TypeIndices)
            {
                __initType |= InitType.TypeIndices;

                int numTypes = _types == null ? 0 : _types.Length;
                if (numTypes > 0)
                {
                    var instance = SingletonAssetContainer<TypeIndex>.instance;

                    SingletonAssetContainerHandle handle;
                    handle.instanceID = __definition.Value.instanceID;

                    TypeIndex typeIndex;
                    __typeIndices = new TypeIndex[numTypes];
                    for (int i = 0; i < numTypes; ++i)
                    {
                        typeIndex = TypeManager.GetTypeIndex(Type.GetType(_types[i]));

                        __typeIndices[i] = typeIndex;

                        handle.index = i;

                        instance[handle] = typeIndex;
                    }
                }
            }
        }

        private void __InitComponentTypes()
        {
            if ((__initType & InitType.ComponentTypes) != InitType.ComponentTypes)
            {
                __initType |= InitType.ComponentTypes;

                __InitTypeIndices();

                int numComponentTypeWrappers = _componentTypeWrappers == null ? 0 : _componentTypeWrappers.Length;
                if (numComponentTypeWrappers > 0)
                {
                    var instance = SingletonAssetContainer<ComponentTypeSet>.instance;

                    SingletonAssetContainerHandle handle;
                    handle.instanceID = __definition.Value.instanceID;

                    for (int i = 0; i < numComponentTypeWrappers; ++i)
                    {
                        handle.index = i;

                        instance[handle] = _componentTypeWrappers[i].ToComponentTypes(__typeIndices);
                    }
                }
            }
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (__bytes != null && __bytes.Length > 0)
            {
                if (__definition.IsCreated)
                    __definition.Dispose();

                unsafe
                {
                    fixed (byte* ptr = __bytes)
                    {
                        using (var reader = new MemoryBinaryReader(ptr, __bytes.LongLength))
                        {
                            int version = reader.ReadInt();

                            UnityEngine.Assertions.Assert.AreEqual(VERSION, version);

                            __definition = reader.Read<MeshInstanceRendererDefinition>();
                        }
                    }
                }

                __bytes = null;
            }

            __initType = 0;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (__definition.IsCreated)
            {
                using (var writer = new MemoryBinaryWriter())
                {
                    writer.Write(VERSION);
                    writer.Write(__definition);

                    __bytes = writer.GetContentAsNativeArray().ToArray();
                }
            }
        }
    }
}