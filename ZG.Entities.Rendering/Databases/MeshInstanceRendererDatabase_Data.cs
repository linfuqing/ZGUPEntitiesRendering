using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using HybridRenderer = UnityEngine.Renderer;

namespace ZG
{
    public partial class MeshInstanceRendererDatabase
    {
        public delegate Material MaterialPropertyOverride(Material material, Action<string, Type, ShaderPropertyType, Vector4> propertyValues);

        public delegate bool MeshStreamingOverride(
            in Matrix4x4 matrix,
            Material material,
            ref Mesh mesh,
            ref int submeshIndex,
            Action<Instance> instances);

        [Serializable]
        public struct LOD
        {
            public int objectIndex;
            public int mask;
        }

        [Serializable]
        public struct MaterialProperty : IEquatable<MaterialProperty>
        {
            public string name;

            public int typeIndex;

            public float[] values;

            public bool Equals(MaterialProperty other)
            {
                if (typeIndex != other.typeIndex)
                    return false;

                int numValues = values == null ? 0 : values.Length;
                if (numValues != (other.values == null ? 0 : other.values.Length))
                    return false;

                for (int i = 0; i < numValues; ++i)
                {
                    if (values[i] != other.values[i])
                        return false;
                }

                return true;
            }

            public override int GetHashCode()
            {
                return typeIndex ^ (values == null ? 0 : values.Length);
            }

        }

        [Serializable]
        public struct Renderer : IEquatable<Renderer>
        {
            public string name;

            public MeshInstanceRendererFlag flag;

            public MotionVectorGenerationMode motionVectorGenerationMode;

            public ShadowCastingMode shadowCastingMode;

            public LightProbeUsage lightProbeUsage;

            public int lightMapIndex;

            public int submeshIndex;

            public int meshIndex;

            public int materialIndex;

            [LayerField]
            public int layer;

            public uint renderingLayerMask;

            public bool Equals(Renderer other)
            {
                return flag == other.flag &&
                    motionVectorGenerationMode == other.motionVectorGenerationMode &&
                    shadowCastingMode == other.shadowCastingMode &&
                    lightProbeUsage == other.lightProbeUsage &&
                    lightMapIndex == other.lightMapIndex &&
                    submeshIndex == other.submeshIndex &&
                    meshIndex == other.meshIndex &&
                    materialIndex == other.materialIndex &&
                    layer == other.layer &&
                    renderingLayerMask == other.renderingLayerMask;
            }

            public override int GetHashCode()
            {
                return (int)flag ^
                    (int)motionVectorGenerationMode ^
                    (int)shadowCastingMode ^
                    (int)lightProbeUsage ^
                    lightMapIndex ^
                    submeshIndex ^
                    meshIndex ^
                    materialIndex ^
                    layer ^
                    (int)renderingLayerMask;
            }

        }

        [Serializable]
        public struct Instance
        {
            public int meshStreamingOffset;

            public Bounds bounds;
            public Matrix4x4 matrix;
        }

        [Serializable]
        public struct Node
        {
            public string name;

            public int rendererIndex;
            public int componentTypesIndex;

            public Instance instance;

            public int[] materialPropertyIndices;
            public LOD[] lods;
        }

        [Serializable]
        public struct Object
        {
            public string name;

            public Vector3 localReferencePoint;

            public Matrix4x4 matrix;

            public LOD parent;

            public float[] distances;
        }

        /*[Serializable]
        public struct LightMap
        {
            public Texture2D color;
            public Texture2D direction;
            public Texture2D shadowMask;
        }*/

        [Serializable]
        public class NodeMap : Dictionary<NodeMap.Key, NodeMap.Value>, ISerializationCallbackReceiver
        {
            [Serializable]
            public struct Key : IEquatable<Key>
            {
                public string rendererPath;
                public Material material;

                public bool isVail => material != null;

                public bool Equals(Key other)
                {
                    return rendererPath == other.rendererPath && material == other.material;
                }

                public override int GetHashCode()
                {
                    return (rendererPath == null ? 0 : rendererPath.GetHashCode()) ^ (material == null ? 0 : material.GetInstanceID());
                }

                public override string ToString()
                {
                    return $"NodeMap.Key({rendererPath})";
                }
            }

            [Serializable]
            public struct Value
            {
                public int[] nodeIndices;

                public void Push(int nodeIndex)
                {
                    int length = nodeIndices == null ? 0 : nodeIndices.Length;
                    Array.Resize(ref nodeIndices, length + 1);
                    nodeIndices[length] = nodeIndex;
                }
            }

            [SerializeField]
            internal UnityEngine.Object _target;
            
            [SerializeField]
            internal Key[] _keys;
            [SerializeField]
            internal Value[] _values;

            void ISerializationCallbackReceiver.OnBeforeSerialize()
            {
                int count = Count;
                
                _keys = new Key[count];
                _values = new Value[count];

                int index = 0;
                foreach (var pair in this)
                {
                    _keys[index] = pair.Key;
                    _values[index] = pair.Value;
                }
            }

            void ISerializationCallbackReceiver.OnAfterDeserialize()
            {
                Clear();

                int count = Mathf.Min(_keys == null ? 0 : _keys.Length, _values == null ? 0 : _values.Length);
                for (int i = 0; i < count; ++i)
                {
                    ref var key = ref _keys[i];
                    if(!key.isVail || !TryAdd(key, _values[i]))
                        Debug.LogError($"Error Key {key}", _target);
                }
            }
        }

        [Serializable]
        public struct Data
        {
#if UNITY_EDITOR
            public static bool isShowProgressBar = true;
#endif
            public MaterialProperty[] materialProperties;

            public Renderer[] renderers;

            public Node[] nodes;

            public Object[] objects;

            public NodeMap nodeMap;

            //public LightMap[] lightMaps;

            public BlobAssetReference<MeshInstanceRendererDefinition> ToAsset(int instanceID)
            {
                int numRenderers = renderers.Length;

                using (var blobBuilder = new BlobBuilder(Allocator.Temp))
                {
                    ref var root = ref blobBuilder.ConstructRoot<MeshInstanceRendererDefinition>();
                    root.instanceID = instanceID;

                    int i, j;
                    int numMaterialPropertyValues, numMaterialPropertices = this.materialProperties == null ? 0 : this.materialProperties.Length;
                    BlobBuilderArray<float> materialPropertyValues;
                    var materialProperties = blobBuilder.Allocate(ref root.materialProperties, numMaterialPropertices);
                    for (i = 0; i < numMaterialPropertices; ++i)
                    {
                        ref readonly var sourceMaterialProperty = ref this.materialProperties[i];
                        ref var destinationMaterialProperty = ref materialProperties[i];

                        destinationMaterialProperty.typeIndex = sourceMaterialProperty.typeIndex;

                        numMaterialPropertyValues = sourceMaterialProperty.values == null ? 0 : sourceMaterialProperty.values.Length;

                        materialPropertyValues = blobBuilder.Allocate(ref destinationMaterialProperty.values, numMaterialPropertyValues);
                        for (j = 0; j < numMaterialPropertyValues; ++j)
                            materialPropertyValues[j] = sourceMaterialProperty.values[j];
                    }

                    var renderers = blobBuilder.Allocate(ref root.renderers, numRenderers);
                    for (i = 0; i < numRenderers; ++i)
                    {
                        ref readonly var sourceRenderer = ref this.renderers[i];

                        ref var destinationRenderer = ref renderers[i];

                        destinationRenderer.flag = sourceRenderer.flag;

                        destinationRenderer.motionVectorGenerationMode = sourceRenderer.motionVectorGenerationMode;

                        destinationRenderer.shadowCastingMode = sourceRenderer.shadowCastingMode;

                        destinationRenderer.lightProbeUsage = sourceRenderer.lightProbeUsage;

                        destinationRenderer.lightMapIndex = sourceRenderer.lightMapIndex;

                        destinationRenderer.submeshIndex = sourceRenderer.submeshIndex;
                        destinationRenderer.meshIndex = sourceRenderer.meshIndex;
                        destinationRenderer.materialIndex = sourceRenderer.materialIndex;

                        destinationRenderer.layer = sourceRenderer.layer;

                        destinationRenderer.renderingLayerMask = sourceRenderer.renderingLayerMask;
                    }

                    int numNodes = this.nodes == null ? 0 : this.nodes.Length, numMaterialPropertyIndices, numLODs;
                    var nodes = blobBuilder.Allocate(ref root.nodes, numNodes);
                    BlobBuilderArray<int> materialPropertyIndices;
                    BlobBuilderArray<MeshInstanceRendererDefinition.LOD> lods;
                    for (i = 0; i < numNodes; ++i)
                    {
                        ref readonly var sourceNode = ref this.nodes[i];

                        ref var destinationNode = ref nodes[i];

                        destinationNode.rendererIndex = sourceNode.rendererIndex;
                        destinationNode.componentTypesIndex = sourceNode.componentTypesIndex;

                        destinationNode.meshStreamingOffset = sourceNode.instance.meshStreamingOffset;

                        /*bounds = source.renderMesh.mesh.bounds;
                        destination.bounds.Center = bounds.center;
                        destination.bounds.Extents = bounds.extents;*/

                        destinationNode.bounds.Center = sourceNode.instance.bounds.center;
                        destinationNode.bounds.Extents = sourceNode.instance.bounds.extents;

                        destinationNode.matrix = sourceNode.instance.matrix;

                        numMaterialPropertyIndices = sourceNode.materialPropertyIndices == null ? 0 : sourceNode.materialPropertyIndices.Length;
                        materialPropertyIndices = blobBuilder.Allocate(ref destinationNode.materialPropertyIndices, numMaterialPropertyIndices);
                        for (j = 0; j < numMaterialPropertyIndices; ++j)
                            materialPropertyIndices[j] = sourceNode.materialPropertyIndices[j];

                        numLODs = sourceNode.lods == null ? 0 : sourceNode.lods.Length;
                        lods = blobBuilder.Allocate(ref destinationNode.lods, numLODs);
                        for (j = 0; j < numLODs; ++j)
                        {
                            ref readonly var sourceLOD = ref sourceNode.lods[j];

                            ref var destinationLOD = ref lods[j];

                            destinationLOD.objectIndex = sourceLOD.objectIndex;
                            destinationLOD.mask = sourceLOD.mask;
                        }
                    }

                    int numObjects = this.objects == null ? 0 : this.objects.Length, numDistances;
                    var objects = blobBuilder.Allocate(ref root.objects, numObjects);
                    BlobBuilderArray<float> distances;
                    for (i = 0; i < numObjects; ++i)
                    {
                        ref readonly var sourceObject = ref this.objects[i];

                        ref var destinationObject = ref objects[i];

                        destinationObject.localReferencePoint = sourceObject.localReferencePoint;
                        destinationObject.matrix = sourceObject.matrix;
                        destinationObject.parent.objectIndex = sourceObject.parent.objectIndex;
                        destinationObject.parent.mask = sourceObject.parent.mask;

                        numDistances = sourceObject.distances == null ? 0 : sourceObject.distances.Length;
                        distances = blobBuilder.Allocate(ref destinationObject.distances, numDistances);
                        for (j = 0; j < numDistances; ++j)
                            distances[j] = sourceObject.distances[j];
                    }

                    return blobBuilder.CreateBlobAssetReference<MeshInstanceRendererDefinition>(Allocator.Persistent);
                }
            }

            public static void GetDistance(int mask, float[] distances, out float min, out float max)
            {
                min = float.MaxValue;
                max = 0.0f;

                int length = 32 - math.lzcnt(mask);
                for (int i = 0; i < length; ++i)
                {
                    if ((mask & (1 << i)) == 0)
                        continue;

                    min = i > 0 ? math.min(min, distances[i - 1]) : 0.0f;
                    max = math.max(max, distances[i]);
                }
            }

            public static Object[] Create(Transform root, LODGroup[] lodGroups, Node[] nodes, NodeMap nodeMap)
            {
                int count = lodGroups == null ? 0 : lodGroups.Length;
                if (count < 1)
                    return null;

                //bool isInit;
#if UNITY_EDITOR
                int index = 0;
#endif
                int numLods, numObjects, objectIndex, i, j;
                //Vector3 center;
                float size;
                //Bounds bounds = default(Bounds);
                LOD lod;
                Object result;
                Transform transform;
                NodeMap.Key key;
                NodeMap.Value value;
                List<Object> results = null;
                UnityEngine.LOD[] lods;
                HybridRenderer[] renderers;
                Material[] materials;
                Dictionary<LODGroup, int> resultIndices = null;
                foreach (var lodGroup in lodGroups)
                {
                    transform = lodGroup == null ? null : lodGroup.transform;
                    if (transform == null)
                        continue;

                    result.name = lodGroup.name;

#if UNITY_EDITOR
                    if (isShowProgressBar)
                        UnityEditor.EditorUtility.DisplayProgressBar("Building Objects..", lodGroup.name, (index++ * 1.0f) / count);
#endif

                    result.parent.mask = 0;
                    result.parent.objectIndex = -1;

                    result.matrix = transform.localToWorldMatrix;

                    size = LODGroupExtensions.GetWorldSpaceSize(lodGroup);

                    result.localReferencePoint = lodGroup.localReferencePoint;

                    lods = lodGroup.GetLODs();
                    numLods = lods == null ? 0 : lods.Length;

                    result.distances = new float[numLods];
                    for (i = 0; i < numLods; ++i)
                        result.distances[i] = size / lods[i].screenRelativeTransitionHeight;

                    if (results == null)
                        results = new List<Object>();

                    objectIndex = results.Count;

                    if (nodeMap != null)
                    {
                        //isInit = false;
                        for (i = 0; i < numLods; ++i)
                        {
                            renderers = lods[i].renderers;
                            if (renderers != null)
                            {
                                foreach (var renderer in renderers)
                                {
                                    if (renderer == null/* || !nodeIndices.TryGetValue(renderer, out nodeIndex)*/)
                                        continue;

                                    /*if (isInit)
                                        bounds.Encapsulate(renderer.bounds);
                                    else
                                    {
                                        bounds = renderer.bounds;

                                        isInit = true;
                                    }*/

                                    materials = renderer.sharedMaterials;
                                    foreach (var material in materials)
                                    {
                                        key.rendererPath = renderer.transform.GetPath(root);
                                        key.material = material;
                                        if (!nodeMap.TryGetValue(key, out value) || value.nodeIndices == null)
                                            continue;

                                        foreach (int nodeIndex in value.nodeIndices)
                                        {
                                            ref var node = ref nodes[nodeIndex];

                                            numObjects = node.lods == null ? 0 : node.lods.Length;
                                            for (j = 0; j < numObjects; ++j)
                                            {
                                                if (node.lods[j].objectIndex == objectIndex)
                                                {
                                                    node.lods[j].mask |= 1 << i;

                                                    break;
                                                }
                                            }

                                            if (j == numObjects)
                                            {
                                                Array.Resize(ref node.lods, numObjects + 1);

                                                lod.objectIndex = objectIndex;
                                                lod.mask = 1 << i;

                                                node.lods[numObjects] = lod;
                                            }

                                            //nodes[nodeIndex/*++*/] = node;
                                        }
                                    }
                                }
                            }
                        }

                        /*if (isInit)
                            result.center = transform.InverseTransformPoint(bounds.center);*/
                    }

                    if (resultIndices == null)
                        resultIndices = new Dictionary<LODGroup, int>();

                    resultIndices.Add(lodGroup, results.Count);

                    results.Add(result);
                }

                Transform parent;
                LODGroup parentLodGroup;
                foreach (LODGroup lodGroup in lodGroups)
                {
                    transform = lodGroup == null ? null : lodGroup.transform;
                    parent = transform == null ? null : transform.parent;
                    parentLodGroup = parent == null ? null : parent.GetComponentInParent<LODGroup>();
                    if (parentLodGroup == null)
                        continue;

                    if (!resultIndices.TryGetValue(lodGroup, out objectIndex))
                        continue;

                    result = results[objectIndex];
                    if (!resultIndices.TryGetValue(parentLodGroup, out result.parent.objectIndex))
                        continue;

                    parent = parentLodGroup.transform;
                    if (parent != null)
                        result.parent.mask |= 1 << parent.GetChildIndex(transform);

                    results[objectIndex] = result;
                }

#if UNITY_EDITOR
                if (isShowProgressBar)
                    UnityEditor.EditorUtility.ClearProgressBar();
#endif

                return results == null ? null : results.ToArray();
            }

            public void Create(
                Transform root, 
                HybridRenderer[] hybridRenderers,
                LODGroup[] lodGroups,
                MaterialPropertyOverride materialPropertyOverride,
                MeshStreamingOverride meshStreamingOverride,
                List<Material> materials,
                List<Mesh> meshes,
                out Type[] materialPropertyTypes,
                out ComponentTypeWrapper[] componentTypeWrappers)
            {
                //lightMaps = null;
                materialPropertyTypes = null;
                componentTypeWrappers = null;

                int count = hybridRenderers == null ? 0 : hybridRenderers.Length;
                if (count < 1)
                    return;

                /*var lightmaps = LightmapSettings.lightmaps;
                int numLightmaps = lightmaps == null ? 0 : lightmaps.Length, i;
                if (numLightmaps > 0)
                {
                    lightMaps = new LightMap[numLightmaps];

                    for(i = 0; i < numLightmaps; ++i)
                    {
                        ref readonly var lightmap = ref lightmaps[i];

                        ref var lightMap = ref lightMaps[i];

                        lightMap.color = lightmap.lightmapColor;
                        lightMap.direction = lightmap.lightmapDir;
                        lightMap.shadowMask = lightmap.shadowMask;
                    }
                }*/
                
                if(nodeMap != null)
                    nodeMap.Clear();

                Instance instance;
                instance.meshStreamingOffset = -1;

#if UNITY_EDITOR
                int index = 0;
#endif
                int i, j, k, numMaterials, submeshIndex, materialPropertyIndex, numMaterialProperties, numInstances;
                Type materialPropertyType;
                MaterialProperty materialProperty;
                ComponentTypeWrapper componentTypeWrapper;
                Node node;
                Renderer renderer;
                Material materialSource, materialDestination;
                Mesh meshSource, meshDestination, bakedMesh;
                MeshFilter meshFilter;
                MeshRenderer meshRenderer;
                SkinnedMeshRenderer skinnedMeshRenderer;
                Transform transform;
                GameObject target;
                NodeMap.Key key;
                NodeMap.Value value;
                int[] typeIndices;
                Material[] sharedMaterials;
                MaterialOverride[] materialOverrides;
                List<Instance> instances = null;
                List<Node> nodes = null;
                Dictionary<Renderer, int> rendererIndices = null;
                Dictionary<Mesh, int> meshIndices = null;
                Dictionary<Material, int> materialIndices = null;
                Dictionary<ComponentTypeWrapper, int> componentTypeWrapperIndices = null;
                Dictionary<MaterialProperty, int> materialPropertyIndices = null;
                Dictionary<Type, int> materialPropertyTypeIndics = null;
                Dictionary<Type, float[]> materialPropertyValues = null;
                foreach (var hybridRenderer in hybridRenderers)
                {
                    target = hybridRenderer == null ? null : hybridRenderer.gameObject;
                    if (target == null)
                        continue;

                    meshRenderer = hybridRenderer as MeshRenderer;
                    if (meshRenderer == null)
                    {
                        skinnedMeshRenderer = hybridRenderer as SkinnedMeshRenderer;
                        if (skinnedMeshRenderer == null)
                            continue;

                        meshSource = skinnedMeshRenderer.sharedMesh;

                        bakedMesh = Bake(skinnedMeshRenderer);

                        bakedMesh.RecalculateBounds();

                        submeshIndex = 0;
                    }
                    else
                    {
                        meshFilter = hybridRenderer.GetComponent<MeshFilter>();
                        meshSource = meshFilter == null ? null : meshFilter.sharedMesh;
                        bakedMesh = meshSource;

                        submeshIndex = meshRenderer.subMeshStartIndex;
                    }

                    if (meshSource == null)
                        continue;

                    transform = hybridRenderer.transform;
                    if (transform == null)
                        continue;

                    renderer.name = hybridRenderer.name;

#if UNITY_EDITOR
                    if (isShowProgressBar)
                        UnityEditor.EditorUtility.DisplayProgressBar("Building Renderers..", renderer.name, (index++ * 1.0f) / count);
#endif

                    renderer.flag = 0;
                    if (hybridRenderer.receiveShadows)
                        renderer.flag |= MeshInstanceRendererFlag.ReceiveShadows;

                    if (hybridRenderer.staticShadowCaster)
                        renderer.flag |= MeshInstanceRendererFlag.StaticShadowCaster;

                    if (hybridRenderer.allowOcclusionWhenDynamic)
                        renderer.flag |= MeshInstanceRendererFlag.AllowOcclusionWhenDynamic;

                    renderer.motionVectorGenerationMode = hybridRenderer.motionVectorGenerationMode;
                    renderer.shadowCastingMode = hybridRenderer.shadowCastingMode;
                    renderer.lightProbeUsage = hybridRenderer.lightProbeUsage;
                    renderer.lightMapIndex = hybridRenderer.lightmapIndex;

                    renderer.layer = target.layer;
                    renderer.renderingLayerMask = hybridRenderer.renderingLayerMask;

                    instance.matrix = transform.localToWorldMatrix;

                    node.name = renderer.name;
                    node.lods = null;

                    sharedMaterials = hybridRenderer.sharedMaterials;
                    numMaterials = sharedMaterials == null ? 0 : sharedMaterials.Length;
                    if (numMaterials > 0)
                    {
                        materialOverrides = hybridRenderer.GetComponents<MaterialOverride>();

                        if (nodes == null)
                            nodes = new List<Node>();

                        for (i = 0; i < numMaterials; ++i)
                        {
                            node.materialPropertyIndices = null;

                            typeIndices = null;

                            materialSource = sharedMaterials[i];

                            foreach (var materialOverride in materialOverrides)
                            {
                                if (materialOverride.overrideAsset == null || materialOverride.overrideAsset.material != materialSource || materialOverride.overrideList == null)
                                    continue;

                                numMaterialProperties = materialOverride.overrideList.Count;

                                node.materialPropertyIndices = new int[numMaterialProperties];

                                typeIndices = new int[numMaterialProperties];

                                for (j = 0; j < numMaterialProperties; ++j)
                                {
                                    var overrideData = materialOverride.overrideList[j];
                                    switch (overrideData.type)
                                    {
                                        case ShaderPropertyType.Int:
                                        case ShaderPropertyType.Float:
                                        case ShaderPropertyType.Range:
                                            materialProperty.values = new float[1];
                                            materialProperty.values[0] = overrideData.value.x;

                                            break;
                                        case ShaderPropertyType.Color:
                                        case ShaderPropertyType.Vector:
                                            materialProperty.values = new float[4];
                                            for (k = 0; k < 4; ++k)
                                                materialProperty.values[k] = overrideData.value[k];

                                            break;
                                        default:
                                            materialProperty.values = null;
                                            break;
                                    }

                                    if (materialProperty.values == null)
                                        continue;

                                    materialPropertyType = materialOverride.overrideAsset.GetTypeFromAttrs(overrideData);

                                    if (materialPropertyTypeIndics == null)
                                        materialPropertyTypeIndics = new Dictionary<Type, int>();

                                    if (!materialPropertyTypeIndics.TryGetValue(materialPropertyType, out materialProperty.typeIndex))
                                    {
                                        materialProperty.typeIndex = materialPropertyTypeIndics.Count;

                                        materialPropertyTypeIndics[materialPropertyType] = materialProperty.typeIndex;
                                    }

                                    typeIndices[j] = materialProperty.typeIndex;

                                    materialProperty.name = materialPropertyType.AssemblyQualifiedName;

                                    if (materialPropertyIndices == null)
                                        materialPropertyIndices = new Dictionary<MaterialProperty, int>();

                                    if (!materialPropertyIndices.TryGetValue(materialProperty, out materialPropertyIndex))
                                    {
                                        materialPropertyIndex = materialPropertyIndices.Count;

                                        materialPropertyIndices[materialProperty] = materialPropertyIndex;
                                    }

                                    node.materialPropertyIndices[j++] = materialPropertyIndex;
                                }

                                break;
                            }

                            materialDestination = materialSource;
                            if (materialDestination != null && materialPropertyOverride != null && typeIndices == null)
                            {
                                if (materialPropertyValues == null)
                                    materialPropertyValues = new Dictionary<Type, float[]>();
                                else
                                    materialPropertyValues.Clear();

                                materialDestination = materialPropertyOverride(materialDestination, (x, y, z, w) =>
                                {
                                    float[] values;
                                    switch(z)
                                    {
                                        case ShaderPropertyType.Int:
                                        case ShaderPropertyType.Float:
                                        case ShaderPropertyType.Range:
                                            values = new float[1];
                                            values[0] = w.x;
                                            break;
                                        case ShaderPropertyType.Color:
                                        case ShaderPropertyType.Vector:
                                            values = new float[4];
                                            values[0] = w.x;
                                            values[1] = w.y;
                                            values[2] = w.z;
                                            values[3] = w.w;
                                            break;
                                        default:
                                            return;
                                    }
                                    materialPropertyValues.Add(y, values);
                                });

                                numMaterialProperties = materialPropertyValues.Count;
                                if (numMaterialProperties > 0)
                                {
                                    node.materialPropertyIndices = new int[numMaterialProperties];

                                    if (materialPropertyTypeIndics == null)
                                        materialPropertyTypeIndics = new Dictionary<Type, int>();

                                    if (materialPropertyIndices == null)
                                        materialPropertyIndices = new Dictionary<MaterialProperty, int>();

                                    typeIndices = new int[numMaterialProperties];

                                    j = 0;
                                    foreach (var pair in materialPropertyValues)
                                    {
                                        materialPropertyType = pair.Key;
                                        materialProperty.name = materialPropertyType.AssemblyQualifiedName;

                                        if (!materialPropertyTypeIndics.TryGetValue(materialPropertyType, out materialProperty.typeIndex))
                                        {
                                            materialProperty.typeIndex = materialPropertyTypeIndics.Count;

                                            materialPropertyTypeIndics[materialPropertyType] = materialProperty.typeIndex;
                                        }

                                        typeIndices[j] = materialProperty.typeIndex;

                                        materialProperty.values = pair.Value;

                                        if (!materialPropertyIndices.TryGetValue(materialProperty, out materialPropertyIndex))
                                        {
                                            materialPropertyIndex = materialPropertyIndices.Count;

                                            materialPropertyIndices[materialProperty] = materialPropertyIndex;
                                        }

                                        node.materialPropertyIndices[j++] = materialPropertyIndex;
                                    }
                                }
                            }

                            if (typeIndices == null)
                                node.componentTypesIndex = -1;
                            else
                            {
                                componentTypeWrapper = new ComponentTypeWrapper(typeIndices);

                                if (componentTypeWrapperIndices == null)
                                    componentTypeWrapperIndices = new Dictionary<ComponentTypeWrapper, int>();

                                if (!componentTypeWrapperIndices.TryGetValue(componentTypeWrapper, out node.componentTypesIndex))
                                {
                                    node.componentTypesIndex = componentTypeWrapperIndices.Count;

                                    componentTypeWrapperIndices[componentTypeWrapper] = node.componentTypesIndex;
                                }
                            }

                            if (materialDestination == null)
                                Debug.LogError(renderer.name + " Missing Material Index: " + i);
                            else
                            {
#if UNITY_EDITOR
                                if (!materialDestination.enableInstancing)
                                {
                                    materialDestination.enableInstancing = true;

                                    UnityEditor.EditorUtility.SetDirty(materialDestination);
                                }
#endif

                                if (materialIndices == null)
                                    materialIndices = new Dictionary<Material, int>();

                                if (!materialIndices.TryGetValue(materialDestination, out renderer.materialIndex))
                                {
                                    renderer.materialIndex = materials.Count;

                                    materialIndices[materialDestination] = renderer.materialIndex;

                                    materials.Add(materialDestination);
                                }

                                if (instances == null)
                                    instances = new List<Instance>();
                                else
                                    instances.Clear();

                                if (IsMaterialTransparent(materialDestination))
                                    renderer.flag |= MeshInstanceRendererFlag.DepthSorted;
                                else
                                    renderer.flag &= ~MeshInstanceRendererFlag.DepthSorted;

                                meshDestination = meshSource;
                                renderer.submeshIndex = submeshIndex;
                                if (meshStreamingOverride == null || !meshStreamingOverride(
                                        instance.matrix,
                                        materials[renderer.materialIndex],
                                        ref meshDestination,
                                        ref renderer.submeshIndex,
                                        instances.Add))
                                {
                                    try
                                    {
                                        instance.bounds = bakedMesh.GetSubMesh(renderer.submeshIndex).bounds;// __ComputeSphere(bakedMesh, node.subMesh, out node.center);
                                    }
                                    catch (Exception e)
                                    {
                                        Debug.LogException(e.InnerException ?? e, meshSource);

                                        instance.bounds = new Bounds();
                                    }

                                    instances.Add(instance);
                                }

                                if (meshIndices == null)
                                    meshIndices = new Dictionary<Mesh, int>();

                                if (!meshIndices.TryGetValue(meshDestination, out renderer.meshIndex))
                                {
                                    renderer.meshIndex = meshes.Count;

                                    meshIndices[meshDestination] = renderer.meshIndex;

                                    meshes.Add(meshDestination);
                                }

                                numInstances = instances.Count;
                                if (numInstances > 0)
                                {
                                    if (rendererIndices == null)
                                        rendererIndices = new Dictionary<Renderer, int>();

                                    if (!rendererIndices.TryGetValue(renderer, out node.rendererIndex))
                                    {
                                        node.rendererIndex = rendererIndices.Count;

                                        rendererIndices[renderer] = node.rendererIndex;
                                    }

                                    for (j = 0; j < numInstances; ++j)
                                    {
                                        node.instance = instances[j];

                                        if (nodeMap == null)
                                            nodeMap = new NodeMap();

                                        key.rendererPath = hybridRenderer.transform.GetPath(root);
                                        key.material = materialSource;
                                        if (!nodeMap.TryGetValue(key, out value))
                                            value = new NodeMap.Value();

                                        value.Push(nodes.Count);

                                        nodeMap[key] = value;

                                        nodes.Add(node);
                                    }
                                }
                            }

                            ++submeshIndex;
                        }
                    }

                    if (bakedMesh != meshSource)
                        DestroyImmediate(bakedMesh);
                }

#if UNITY_EDITOR
                if (isShowProgressBar)
                    UnityEditor.EditorUtility.ClearProgressBar();
#endif

                if (rendererIndices == null)
                    return;

                if (materialPropertyIndices == null)
                    materialProperties = null;
                else
                {
                    materialProperties = new MaterialProperty[materialPropertyIndices.Count];
                    foreach (var pair in materialPropertyIndices)
                        materialProperties[pair.Value] = pair.Key;
                }

                renderers = new Renderer[rendererIndices.Count];
                foreach (var pair in rendererIndices)
                    renderers[pair.Value] = pair.Key;

                this.nodes = nodes == null ? null : nodes.ToArray();
                objects = Create(root, lodGroups, this.nodes, nodeMap);

                if (materialPropertyTypeIndics != null)
                {
                    materialPropertyTypes = new Type[materialPropertyTypeIndics.Count];
                    materialPropertyTypeIndics.Keys.CopyTo(materialPropertyTypes, 0);
                }

                if (componentTypeWrapperIndices != null)
                {
                    componentTypeWrappers = new ComponentTypeWrapper[componentTypeWrapperIndices.Count];

                    componentTypeWrapperIndices.Keys.CopyTo(componentTypeWrappers, 0);
                }
            }

            public GameObject InstantiateLegacy(string name, IReadOnlyList<Mesh> meshes, IReadOnlyList<Material> materials)
            {
                GameObject gameObject = new GameObject(name);
                Transform root = gameObject.transform, transform;

                int numObjects = objects == null ? 0 : objects.Length, i;
                LODGroup lodGroup;
                LODGroup[] lodGroups = null;
                if (numObjects > 0)
                {
                    lodGroups = new LODGroup[numObjects];
                    for (i = 0; i < numObjects; ++i)
                    {
                        lodGroup = __CreateLODGroup(i, lodGroups);
                        transform = lodGroup.transform;
                        if (transform.parent == null)
                            transform.SetParent(root, true);
                    }
                }

                int numLODs;
                UnityEngine.LOD[] lods;
                GameObject temp;
                MeshRenderer meshRenderer;
                HybridRenderer[] renderers;
                Material[] sharedMaterials;
                foreach (var node in nodes)
                {
                    temp = new GameObject(node.name);

                    ref readonly var renderer = ref this.renderers[node.rendererIndex];
                    temp.layer = renderer.layer;

                    transform = temp.transform;
                    transform.SetParent(root, true);
                    transform.position = node.instance.matrix.MultiplyPoint(Vector3.zero);
                    transform.localRotation = node.instance.matrix.rotation;
                    transform.localScale = node.instance.matrix.lossyScale;

                    temp.AddComponent<MeshFilter>().sharedMesh = meshes[renderer.meshIndex];
                    meshRenderer = temp.AddComponent<MeshRenderer>();
                    sharedMaterials = new Material[renderer.submeshIndex + 1];
                    sharedMaterials[renderer.submeshIndex] = materials[renderer.materialIndex];
                    meshRenderer.sharedMaterials = sharedMaterials;
                    meshRenderer.motionVectorGenerationMode = renderer.motionVectorGenerationMode;
                    meshRenderer.lightProbeUsage = renderer.lightProbeUsage;
                    meshRenderer.shadowCastingMode = renderer.shadowCastingMode;
                    meshRenderer.receiveShadows = (renderer.flag & MeshInstanceRendererFlag.ReceiveShadows) == MeshInstanceRendererFlag.ReceiveShadows;
                    meshRenderer.staticShadowCaster = (renderer.flag & MeshInstanceRendererFlag.StaticShadowCaster) == MeshInstanceRendererFlag.StaticShadowCaster;
                    meshRenderer.allowOcclusionWhenDynamic = (renderer.flag & MeshInstanceRendererFlag.AllowOcclusionWhenDynamic) == MeshInstanceRendererFlag.AllowOcclusionWhenDynamic;

                    if (node.lods != null && node.lods.Length > 0)
                    {
                        UnityEngine.Assertions.Assert.AreEqual(1, node.lods.Length);
                        foreach (var lod in node.lods)
                        {
                            lodGroup = lodGroups[lod.objectIndex];

                            numLODs = 32 - math.lzcnt(lod.mask);
                            lods = lodGroup.GetLODs();
                            for (i = 0; i < numLODs; ++i)
                            {
                                if ((lod.mask & (1 << i)) == 0)
                                    continue;

                                renderers = lods[i].renderers;

                                Array.Resize(ref renderers, renderers == null ? 1 : renderers.Length + 1);

                                renderers[renderers.Length - 1] = meshRenderer;

                                lods[i].renderers = renderers;
                            }

                            lodGroup.SetLODs(lods);
                        }
                    }
                }

                return gameObject;
            }

            private LODGroup __CreateLODGroup(int index, LODGroup[] lodGroups)
            {
                LODGroup lodGroup = lodGroups[index];
                if (lodGroup != null)
                    return lodGroup;

                var target = objects[index];

                int siblingIndex;
                Transform parentTransform, transform;
                if (target.parent.objectIndex == -1)
                {
                    siblingIndex = -1;
                    parentTransform = null;
                    transform = new GameObject(target.name).transform;
                }
                else
                {
                    var parent = __CreateLODGroup(target.parent.objectIndex, lodGroups);
                    parentTransform = parent.transform;

                    siblingIndex = 32 - math.lzcnt(target.parent.mask);

                    int childCount = parentTransform.childCount;
                    if (childCount <= siblingIndex)
                    {
                        for (int i = childCount; i < siblingIndex; ++i)
                            new GameObject("LOD " + i).transform.SetParent(parentTransform);

                        transform = new GameObject(target.name).transform;
                    }
                    else
                    {
                        transform = parentTransform.GetChild(siblingIndex);
                        UnityEngine.Assertions.Assert.AreEqual(null, transform.GetComponent<LODGroup>());
                        transform.SetParent(null);
                        transform.name = target.name;
                    }
                }

                transform.localPosition = target.matrix.MultiplyPoint(Vector3.zero);
                transform.localRotation = target.matrix.rotation;
                transform.localScale = target.matrix.lossyScale;
                if (parentTransform != null)
                {
                    transform.SetParent(parentTransform, true);
                    transform.SetSiblingIndex(siblingIndex);
                }

                lodGroup = transform.gameObject.AddComponent<LODGroup>();
                lodGroup.localReferencePoint = target.localReferencePoint;

                float size = LODGroupExtensions.GetWorldSpaceSize(lodGroup);

                int numLODs = target.distances.Length;
                var lods = new UnityEngine.LOD[numLODs];
                for (int i = 0; i < numLODs; ++i)
                    lods[i] = new UnityEngine.LOD(size / target.distances[i], null);

                lodGroups[index] = lodGroup;

                return lodGroup;
            }
        }


        public static int EntityStartIndexOf(int nodeIndex, Node[] nodes)
        {
            int entityStartIndex = 0;
            LOD[] lods;
            for (int i = 0; i < nodeIndex; ++i)
            {
                lods = nodes[i].lods;
                entityStartIndex += Mathf.Max(1, lods == null ? 0 : lods.Length);
            }

            return entityStartIndex;
        }


#if UNITY_EDITOR
        [HideInInspector]
        public Transform root;

        public MeshInstanceMaterialPropertySettings materialPropertySettings;
        public MeshInstanceStreamingDatabase streamingDatabase;

        public Data data;

        public void Create()
        {
            var materials = new List<Material>();
            var meshes = new List<Mesh>();
            data.Create(
                root, 
                root.GetComponentsInChildren<HybridRenderer>(),
                root.GetComponentsInChildren<LODGroup>(),
                materialPropertySettings == null ? (MaterialPropertyOverride)null : materialPropertySettings.Override,
                streamingDatabase == null || streamingDatabase._settings == null ? (MeshStreamingOverride)null : streamingDatabase._settings.Override,
                materials,
                meshes,
                out var materialPropertyTypes,
                out _componentTypeWrappers);

            _materials = materials.ToArray();

            _meshes = meshes.ToArray();

            int numMaterialPropertyTypes = materialPropertyTypes == null ? 0 : materialPropertyTypes.Length;
            _types = new string[numMaterialPropertyTypes];

            for (int i = 0; i < numMaterialPropertyTypes; ++i)
                _types[i] = materialPropertyTypes[i].AssemblyQualifiedName;

            if (streamingDatabase != null)
                streamingDatabase.Save();
        }

        public void Rebuild()
        {
            if (__definition.IsCreated)
                __definition.Dispose();

            __definition = data.ToAsset(GetInstanceID());

            __bytes = null;

            ((ISerializationCallbackReceiver)this).OnBeforeSerialize();
        }

        public void EditorMaskDirty()
        {
            Rebuild();

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}