using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

namespace ZG
{
    /*public struct MeshInstanceRendererInit : IComponentData
    {
    }*/

    public struct MeshInstanceRendererDisabled : IComponentData
    {

    }

    public struct MeshInstanceRendererDirty : IComponentData
    {

    }

    public struct MeshInstanceRendererID : ICleanupComponentData
    {
        public int value;
    }

    public struct MeshInstanceStatic : IComponentData
    {
    }

    public struct MeshInstanceNode : ICleanupBufferElementData
    {
        public Entity entity;
    }

    public struct MeshInstanceObject : ICleanupBufferElementData
    {
        public Entity entity;
    }

    /*public struct MeshInstanceLODGroupRootDisabled : IComponentData
    {
    }

    public struct MeshInstanceLODGroupRoot : IComponentData
    {
    }

    public struct MeshInstanceLODGroup : IComponentData
    {
        public float3 referencePosition;
    }

    public struct MeshInstanceLODWorldGroup : IComponentData
    {
        public float3 referencePosition;
    }*/

    public struct MeshInstanceLODParentIndex : IComponentData
    {
        public int value;
    }

    public struct MeshInstanceLODParent : IComponentData
    {
        public int childIndex;
        public Entity entity;
    }

    public struct MeshInstanceLODChild : IBufferElementData
    {
        public Entity entity;

        public float minDistance;
        public float maxDistance;
    }

    /*public struct MeshInstanceLOD : IComponentData
    {
        public int mask;
    }*/

    /*public class MeshInstanceMaterialPropertyAttribute : MaterialPropertyAttribute
    {
        public string[] keywordMasks;

        public MeshInstanceMaterialPropertyAttribute(
            string materialPropertyName,
            short overrideSizeGPU = -1,
            params string[] keywordMasks) :
            base(materialPropertyName, overrideSizeGPU)
        {
            this.keywordMasks = keywordMasks;
        }
    }*/

    [EntityComponent(typeof(MeshInstanceRendererData))]
    //[EntityComponent(typeof(MeshInstanceTransformSource))]
    //[EntityComponent(typeof(MeshInstanceTransformDestination))]
    public class MeshInstanceRendererComponent : EntityProxyComponent, IEntityComponent, IEntityRuntimeComponentDefinition, IMaterialModifier
    {
        private static readonly List<MeshInstanceRendererMaterialModifier> MaterialModifiers = new List<MeshInstanceRendererMaterialModifier>();

        private Dictionary<Material, MeshInstanceMaterialAsset> __materialAssets;

        [SerializeField]
        internal MeshInstanceRendererDatabase _database;

        public MeshInstanceRendererDatabase database
        {
            get
            {
                return _database;
            }

            set
            {
                if (_database == value)
                    return;

                _database = value;

                var gameObjectEntity = base.gameObjectEntity;
                if (gameObjectEntity.isCreated && gameObjectEntity.world.IsCreated)
                {
                    MeshInstanceRendererData instance;
                    instance.definition = value.definition;
                    this.SetComponentData(instance);

                    this.AddComponent<MeshInstanceRendererDirty>();
                }

                //OnValidate();
            }
        }

        public bool isActive
        {
            get
            {
                if (enabled)
                {
                    var transform = base.transform;
                    while (transform.gameObject.activeSelf)
                    {
                        transform = transform.parent;
                        if (transform == null)
                            return true;
                    }
                }

                return false;
            }
        }

        public ComponentType[] runtimeComponentTypes
        {
            get
            {
                if (!isActive)
                    return new ComponentType[] { ComponentType.ReadOnly<MeshInstanceRendererDisabled>() };

                return null;
            }
        }

        public int Replace(Material source, Material destination)
        {
            UnityEngine.Assertions.Assert.IsNotNull(destination, transform.root.name);
            if (destination == null)
                return 0;
            
            _database.Init();

            if (__materialAssets == null)
                __materialAssets = new Dictionary<Material, MeshInstanceMaterialAsset>();

            SingletonAssetContainerHandle handle;
            handle.instanceID = _database.instanceID;
            handle.index = 0;

            var instance = SingletonAssetContainer<MeshInstanceMaterialAsset>.instance;

            MaterialModifiers.Clear();

            int count = 0;
            MeshInstanceRendererMaterialModifier materialModifier;
            var materials = _database.materials;
            foreach (var material in materials)
            {
                if (material != source)
                {
                    ++handle.index;

                    continue;
                }

                materialModifier.source = instance[handle];

                if (!__materialAssets.TryGetValue(destination, out materialModifier.destination))
                {
                    materialModifier.destination = MeshInstanceRendererSharedUtility.RegisterMaterial(destination);

                    __materialAssets[destination] = materialModifier.destination;
                }

                MaterialModifiers.Add(materialModifier);

                ++handle.index;
                ++count;
            }

            this.AppendBuffer<MeshInstanceRendererMaterialModifier, List<MeshInstanceRendererMaterialModifier>>(MaterialModifiers);

            return count;
        }

#if UNITY_EDITOR
        void Awake()
        {
            if (!_database.isVail)
                Debug.LogError("The Database Is Invail", this);
        }
#endif

        /*public void Transform(in float4x4 value)
        {
            MeshInstanceTransformDestination destination;
            destination.matrix = value;
            this.SetComponentData(destination);
        }*/

        protected void OnEnable()
        {
            this.RemoveComponent<MeshInstanceRendererDisabled>();
        }

        protected void OnDisable()
        {
            this.AddComponent<MeshInstanceRendererDisabled>();
        }

        protected void OnDestroy()
        {
            if (__materialAssets != null)
            {
                foreach (var materialAsset in __materialAssets.Values)
                    MeshInstanceRendererSharedUtility.UnregisterMaterial(materialAsset);

                __materialAssets = null;
            }
        }

#if UNITY_EDITOR

        private static List<MeshInstanceNode> __renderersTemp = new List<MeshInstanceNode>();

        private static List<Color> __colorsTemp = new List<Color>();

        protected void OnDrawGizmosSelected()
        {
            if (!gameObjectEntity.isCreated)
                return;

            __renderersTemp.Clear();

            WriteOnlyListWrapper<MeshInstanceNode, List<MeshInstanceNode>> wrapper;

            if (this.TryGetBuffer<MeshInstanceNode, List<MeshInstanceNode>, WriteOnlyListWrapper<MeshInstanceNode, System.Collections.Generic.List<MeshInstanceNode>>>(ref __renderersTemp, ref wrapper))
            {
                int numRenderers = __renderersTemp.Count, numColors = __colorsTemp.Count;
                for (int i = numColors; i < numRenderers; ++i)
                    __colorsTemp.Add(UnityEngine.Random.ColorHSV());

                for(int i = 0; i < numRenderers; ++i)
                {
                    if (this.TryGetComponentData<WorldRenderBounds>(__renderersTemp[i].entity, out var worldRenderBounds))
                    {
                        Gizmos.color = __colorsTemp[i];

                        Gizmos.DrawWireCube(worldRenderBounds.Value.Center, worldRenderBounds.Value.Size);
                    }
                }
            }
        }
#endif

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            /*if (_type != MeshInstanceType.Static)
            {
                MeshInstanceData value;
                value.type = _type;

                assigner.SetSharedComponentData(value);
            }*/

            MeshInstanceRendererData instance;
            instance.definition = _database.definition;
            assigner.SetComponentData(entity, instance);

            /*MeshInstanceTransformSource source;
            source.matrix = float4x4.identity;
            assigner.SetComponentData(entity, source);

            MeshInstanceTransformDestination destination;
            destination.matrix = float4x4.identity;
            assigner.SetComponentData(entity, destination);*/
        }
    }
}