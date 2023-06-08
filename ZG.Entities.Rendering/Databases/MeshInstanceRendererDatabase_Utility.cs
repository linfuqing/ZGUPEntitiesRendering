using System;
using System.Collections.Generic;
using UnityEngine;
using HybridRenderer = UnityEngine.Renderer;

namespace ZG
{
    public partial class MeshInstanceRendererDatabase
    {
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
                        if (renderer == null)
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
                    rendererLODCount = Mathf.Max(lodGroups == null ? 0 : lodGroups.Length, 1);
                else
                    rendererLODCount = 1;

                rendererCount = renderer.sharedMaterials.Length * rendererLODCount;
                rendererStartIndex += rendererCount;
            }
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
                if (skinBone == null)
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
            catch (Exception e)
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
}