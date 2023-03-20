using UnityEngine;
using UnityEditor;

namespace ZG
{
    [CustomEditor(typeof(MeshInstanceOccluderDatabase))]
    public class MeshInstanceOccluderDatabaseEditor : Editor
    {
        [MenuItem("Assets/ZG/MeshInstance/Rebuild All Occluders")]
        public static void RebuildAllOccluders()
        {
            MeshInstanceOccluderDatabase target;
            var guids = AssetDatabase.FindAssets("t:MeshInstanceOccluderDatabase");
            string path;
            int numGUIDs = guids.Length;
            for(int i = 0; i < numGUIDs; ++i)
            {
                path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (EditorUtility.DisplayCancelableProgressBar("Rebuild All Occluders", path, i * 1.0f / numGUIDs))
                    break;

                target = AssetDatabase.LoadAssetAtPath<MeshInstanceOccluderDatabase>(path);
                if (target == null)
                    continue;

                if(target.root == null)
                {
                    Debug.LogError($"{target.name} missing root", target.root);

                    continue;
                }

                target.Create();

                target.EditorMaskDirty();
            }

            EditorUtility.ClearProgressBar();
        }

        public override void OnInspectorGUI()
        {
            var target = base.target as MeshInstanceOccluderDatabase;

            EditorGUI.BeginChangeCheck();
            target.root = EditorGUILayout.ObjectField(target.root, typeof(Transform), true) as Transform;
            if (EditorGUI.EndChangeCheck() || GUILayout.Button("Rebuild"))
            {
                if (target.root != null)
                {
                    target.Create();

                    if (PrefabUtility.GetPrefabInstanceStatus(target.root) == PrefabInstanceStatus.Connected)
                        target.root = PrefabUtility.GetCorrespondingObjectFromSource(target.root);
                }

                target.EditorMaskDirty();
            }

            base.OnInspectorGUI();
        }
    }
}