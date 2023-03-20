using System.IO;
using UnityEngine;
using UnityEditor;

namespace ZG
{
    [CustomEditor(typeof(MeshInstanceStreamingSettingsBase), true)]
    public class MeshInstanceStreamingSettingsEditor : Editor
    {
        public int triangleCountPerInstance = 32;

        public Mesh CreateInstancedMesh(ushort vertexCountPerInstance)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var meshData = meshDataArray[0];
            MeshStreamingUtility.BuildInstancedMesh(vertexCountPerInstance, ref meshData);

            var mesh = new Mesh();

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);

            return mesh;
        }

        public override void OnInspectorGUI()
        {
            var target = base.target as MeshInstanceStreamingSettingsBase;// MeshStreamingComponentBase;

            triangleCountPerInstance = EditorGUILayout.IntField("Triangle Count Per Instance", triangleCountPerInstance);
            if(GUILayout.Button("Rebuild Instanced Mesh"))
            {
                target.instancedMesh = CreateInstancedMesh((ushort)(triangleCountPerInstance * 3));

                string path = AssetDatabase.GetAssetPath(target);
                if (!AssetDatabase.IsValidFolder(path))
                {
                    path = AssetDatabase.CreateFolder(Path.GetDirectoryName(path), Path.GetFileName(path));
                    path = AssetDatabase.GUIDToAssetPath(path);
                }

                AssetDatabase.CreateAsset(target.instancedMesh, Path.Combine(path, "Instanced Mesh"));
            }

            string streammingPath = target.streamingPath;
            if (File.Exists(streammingPath) && GUILayout.Button($"Clear Mesh Stream: {streammingPath}"))
            {
                File.Delete(streammingPath);

                string persistentPath = target.persistentPath;
                if(File.Exists(persistentPath))
                    File.Delete(persistentPath);
            }

            base.OnInspectorGUI();
        }
    }
}