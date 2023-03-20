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
    public class MeshInstanceRendererDatabase : MeshInstanceDatabase<MeshInstanceRendererDatabase>, ISerializationCallbackReceiver
    {
        public delegate Material MaterialPropertyOverride(Material material, Action<Type, float[]> propertyValues);

        public delegate bool MeshStreamingOverride(
            in Matrix4x4 matrix,
            Material material,
            ref Mesh mesh,
            ref int submeshIndex,
            Action<Instance> instances);

        [Flags]
        public enum InitType
        {
            Materials = 0x01,
            Meshes = 0x02,
            TypeIndices = 0x04,
            ComponentTypes = 0x08
        }

        [Serializable]
        public struct LOD
        {
            public int objectIndex;
            public int mask;
        }

        [Serializable]
        public struct ComponentTypeWrapper : IEquatable<ComponentTypeWrapper>
        {
            public int[] typeIndices;

            public ComponentTypeWrapper(int[] typeIndices)
            {
                this.typeIndices = typeIndices;
            }

            public ComponentTypeSet ToComponentTypes(int[] typeIndices)
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

                for(int i = 0; i < numValues; ++i)
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
        public struct Data
        {
#if UNITY_EDITOR
            public static bool isShowProgressBar = true;
#endif
            public MaterialProperty[] materialProperties;

            public Renderer[] renderers;

            public Node[] nodes;

            public Object[] objects;

            //public LightMap[] lightMaps;

            public static void Build(LODGroup[] lodGroups, IDictionary<HybridRenderer, int> outRendererLODCounts)
            {
                int rendererLODCount;
                UnityEngine.LOD[] lods;
                var renderers = new HashSet<HybridRenderer>();
                foreach (var lodGroup in lodGroups)
                {
                    renderers.Clear();

                    lods = lodGroup.GetLODs();
                    foreach (var lod in lods)
                    {
                        foreach (var renderer in lod.renderers)
                        {
                            if (renderers.Add(renderer))
                            {
                                if (!outRendererLODCounts.TryGetValue(renderer, out rendererLODCount))
                                    rendererLODCount = 0;

                                outRendererLODCounts[renderer] = rendererLODCount + 1;
                            }
                        }
                    }
                }
            }

            public static void Build(LODGroup[] lodGroups, IDictionary<HybridRenderer, LODGroup[]> outRendererLODGroups)
            {
                int numLODGroups;
                LODGroup[] outLODGroups;
                UnityEngine.LOD[] lods;
                var renderers = new HashSet<HybridRenderer>();
                foreach (var lodGroup in lodGroups)
                {
                    if (lodGroup == null)
                    {
                        Debug.LogError(lodGroup, lodGroup);

                        continue;
                    }

                    renderers.Clear();

                    lods = lodGroup.GetLODs();
                    foreach (var lod in lods)
                    {
                        foreach (var renderer in lod.renderers)
                        {
                            if(renderer == null)
                            {
                                Debug.LogError(lodGroup, lodGroup);

                                continue;
                            }

                            if (renderers.Add(renderer))
                            {
                                if (!outRendererLODGroups.TryGetValue(renderer, out outLODGroups))
                                    outLODGroups = null;

                                numLODGroups = outLODGroups == null ? 0 : outLODGroups.Length;
                                Array.Resize(ref outLODGroups, numLODGroups + 1);

                                outLODGroups[numLODGroups] = lodGroup;
                            }
                        }
                    }
                }
            }

            public static void Build(
                HybridRenderer[] renderers, 
                IDictionary<HybridRenderer, LODGroup[]> rendererLODGroups, 
                IDictionary<HybridRenderer, int> outRendererStartIndices)
            {
                HybridRenderer renderer;
                LODGroup[] lodGroups;
                int numRenderers = renderers.Length, rendererStartIndex = 0, rendererCount, rendererLODCount;
                for (int i = 0; i < numRenderers; ++i)
                {
                    renderer = renderers[i];

                    outRendererStartIndices[renderer] = rendererStartIndex;

                    if (rendererLODGroups.TryGetValue(renderer, out lodGroups))
                        rendererLODCount = math.max(lodGroups == null ? 0 : lodGroups.Length, 1);
                    else
                        rendererLODCount = 1;

                    rendererCount = renderer.sharedMaterials.Length * rendererLODCount;
                    rendererStartIndex += rendererCount;
                }
            }

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
                    for(i = 0; i < numMaterialPropertices; ++i)
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

            public static Object[] Create(LODGroup[] lodGroups, Node[] nodes, Dictionary<(HybridRenderer, Material), int[]> nodeIndices)
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
                List<Object> results = null;
                UnityEngine.LOD[] lods;
                HybridRenderer[] renderers;
                Material[] materials;
                int[] nodeIndexArray;
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

                    if (nodeIndices != null)
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
                                    foreach(var material in materials)
                                    {
                                        if (!nodeIndices.TryGetValue((renderer, material), out nodeIndexArray))
                                            continue;

                                        foreach (int nodeIndex in nodeIndexArray)
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

                Instance instance;
                instance.meshStreamingOffset = -1;

#if UNITY_EDITOR
                int index = 0;
#endif
                int i, j, numMaterials, submeshIndex, nodeIndexArrayIndex, materialPropertyIndex, numMaterialProperties, numInstances;
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
                int[] nodeIndexArray, typeIndices;
                Material[] sharedMaterials;
                List<Instance> instances = null;
                List<Node> nodes = null;
                Dictionary<Renderer, int> rendererIndices = null;
                Dictionary<Mesh, int> meshIndices = null;
                Dictionary<Material, int> materialIndices = null;
                Dictionary<ComponentTypeWrapper, int> componentTypeWrapperIndices = null;
                Dictionary<MaterialProperty, int> materialPropertyIndices = null;
                Dictionary<Type, int> materialPropertyTypeIndics = null;
                Dictionary<Type, float[]> materialPropertyValues = null;
                Dictionary<(HybridRenderer, Material), int[]> nodeIndices = null;
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
                        if (nodes == null)
                            nodes = new List<Node>();

                        for (i = 0; i < numMaterials; ++i)
                        {
                            materialSource = sharedMaterials[i];
                            materialDestination = materialSource;
                            if (materialDestination != null && materialPropertyOverride != null)
                            {
                                if (materialPropertyValues == null)
                                    materialPropertyValues = new Dictionary<Type, float[]>();
                                else
                                    materialPropertyValues.Clear();

                                materialDestination = materialPropertyOverride(materialDestination, materialPropertyValues.Add);

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

                                    componentTypeWrapper = new ComponentTypeWrapper(typeIndices);

                                    if (componentTypeWrapperIndices == null)
                                        componentTypeWrapperIndices = new Dictionary<ComponentTypeWrapper, int>();

                                    if(!componentTypeWrapperIndices.TryGetValue(componentTypeWrapper, out node.componentTypesIndex))
                                    {
                                        node.componentTypesIndex = componentTypeWrapperIndices.Count;

                                        componentTypeWrapperIndices[componentTypeWrapper] = node.componentTypesIndex;
                                    }
                                }
                                else
                                {
                                    node.componentTypesIndex = -1;
                                    node.materialPropertyIndices = null;
                                }
                            }
                            else
                            {
                                node.componentTypesIndex = -1;
                                node.materialPropertyIndices = null;
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

                                        if (nodeIndices == null)
                                            nodeIndices = new Dictionary<(HybridRenderer, Material), int[]>();

                                        if (!nodeIndices.TryGetValue((hybridRenderer, materialSource), out nodeIndexArray))
                                            nodeIndexArray = null;

                                        nodeIndexArrayIndex = nodeIndexArray == null ? 0 : nodeIndexArray.Length;
                                        Array.Resize(ref nodeIndexArray, nodeIndexArrayIndex + 1);

                                        nodeIndexArray[nodeIndexArrayIndex] = nodes.Count;

                                        nodeIndices[(hybridRenderer, materialSource)] = nodeIndexArray;

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
                objects = Create(lodGroups, this.nodes, nodeIndices);

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

            private static float __ComputeSphere(Mesh mesh, int subMeshIndex, out Vector3 center)
            {
                var bounds = mesh.GetSubMesh(subMeshIndex).bounds;
                center = bounds.center;

                var extents = bounds.extents;
                return Mathf.Max(extents.x, extents.y, extents.z);
                /*Vector3[] vertices = mesh == null ? null : mesh.vertices;
                int[] indices = vertices == null ? null : mesh.GetIndices(subMeshIndex);
                int numIndices = indices == null ? 0 : indices.Length;

                center = Vector3.zero;
                if (numIndices < 1)
                    return 0.0f;

                int i;
                for (i = 0; i < numIndices; ++i)
                    center += vertices[indices[i]];

                center /= numIndices;

                float maxDistance = 0.0f, distance;
                for (i = 0; i < numIndices; ++i)
                {
                    distance = (vertices[indices[i]] - center).sqrMagnitude;
                    if (distance > maxDistance)
                        maxDistance = distance;
                }

                return Mathf.Sqrt(maxDistance);*/
            }

            public static Mesh Bake(SkinnedMeshRenderer skinnedMeshRenderer)
            {
                var smrRootBone = skinnedMeshRenderer.rootBone;
                smrRootBone = smrRootBone == null ? skinnedMeshRenderer.transform : smrRootBone;

                var invRoot = smrRootBone.localToWorldMatrix.inverse;

                var skinBones = skinnedMeshRenderer.bones;
                var bindposes = skinnedMeshRenderer.sharedMesh.bindposes;

                int numSkinBones = skinBones.Length;

#if UNITY_EDITOR

                UnityEngine.Assertions.Assert.IsTrue(numSkinBones > 0, $"The SkinnedMeshRenderer {UnityEditor.AssetDatabase.GetAssetPath(skinnedMeshRenderer.transform.root)} is invail!");
#else
                UnityEngine.Assertions.Assert.IsTrue(numSkinBones > 0, $"The SkinnedMeshRenderer {skinnedMeshRenderer} is invail!");
#endif

                Matrix4x4 skinMatrix;
                Matrix4x4[] skinMatrices = new Matrix4x4[numSkinBones];

                for (int i = 0; i < numSkinBones; ++i)
                {
                    ref readonly var skinBone = ref skinBones[i];
                    if(skinBone == null)
                    {
                        Debug.LogError($"The SkinnedMeshRenderer {skinnedMeshRenderer} is invail!", skinnedMeshRenderer.transform.root);

                        skinMatrix = Matrix4x4.identity;
                    }
                    else
                        skinMatrix = invRoot * skinBones[i].localToWorldMatrix;

                    skinMatrix *= bindposes[i];

                    skinMatrices[i] = skinMatrix;
                }

                var skinnedMesh = skinnedMeshRenderer.sharedMesh;
                var boneWeights = skinnedMesh.boneWeights;
                var originVertices = skinnedMesh.vertices;
                var originNormals = skinnedMesh.normals;
                var originTangents = skinnedMesh.tangents;

                int numVertices = originVertices.Length;
                var skinnedVertices = new Vector3[numVertices];
                var skinnedNormals = new Vector3[numVertices];
                var skinnedTangents = new Vector4[numVertices];

                try
                {
                    for (int i = 0; i < numVertices; ++i)
                    {
                        ref readonly var boneWeight = ref boneWeights[i];

                        ref readonly var originVertex = ref originVertices[i];
                        skinnedVertices[i] = __Skin(new Vector4(originVertex.x, originVertex.y, originVertex.z, 1.0f), boneWeight, skinMatrices);

                        ref readonly var originNormal = ref originNormals[i];
                        skinnedNormals[i] = __Skin(new Vector4(originNormal.x, originNormal.y, originNormal.z, 0.0f), boneWeight, skinMatrices);

                        ref readonly var originTangent = ref originTangents[i];
                        skinnedTangents[i] = __Skin(new Vector4(originTangent.x, originTangent.y, originTangent.z, 0.0f), boneWeight, skinMatrices);
                    }
                }
                catch(Exception e)
                {
                    Debug.LogException(e.InnerException ?? e, skinnedMesh);

#if UNITY_EDITOR
                    Debug.LogError($"The SkinnedMeshRenderer {UnityEditor.AssetDatabase.GetAssetPath(skinnedMesh)} is invail!", skinnedMeshRenderer.transform.root);
#endif
                }

                var mesh = Instantiate(skinnedMesh);
                mesh.boneWeights = null;
                mesh.vertices = skinnedVertices;
                mesh.normals = skinnedNormals;
                mesh.tangents = skinnedTangents;

                return mesh;
            }

            private static Vector3 __Skin(Vector4 origin, in BoneWeight boneWeight, Matrix4x4[] skinMatrices)
            {
                var result = Vector3.zero;

                ref readonly var skinMatrix0 = ref skinMatrices[boneWeight.boneIndex0];

                result += (Vector3)(skinMatrix0 * origin) * boneWeight.weight0;

                ref readonly var skinMatrix1 = ref skinMatrices[boneWeight.boneIndex1];

                result += (Vector3)(skinMatrix1 * origin) * boneWeight.weight1;

                ref readonly var skinMatrix2 = ref skinMatrices[boneWeight.boneIndex2];

                result += (Vector3)(skinMatrix2 * origin) * boneWeight.weight2;

                ref readonly var skinMatrix3 = ref skinMatrices[boneWeight.boneIndex3];

                result += (Vector3)(skinMatrix3 * origin) * boneWeight.weight3;

                return result;
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

        private int[] __typeIndices;

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
                    var instance = SingletonAssetContainer<int>.instance;

                    SingletonAssetContainerHandle handle;
                    handle.instanceID = __definition.Value.instanceID;

                    int typeIndex;
                    __typeIndices = new int[numTypes];
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

            for(int i = 0; i < numMaterialPropertyTypes; ++i)
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