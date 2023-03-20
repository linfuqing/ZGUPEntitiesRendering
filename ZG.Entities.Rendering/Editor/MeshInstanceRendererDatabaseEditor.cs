using UnityEngine;
using UnityEditor;

namespace ZG
{
    [CustomEditor(typeof(MeshInstanceRendererDatabase))]
    public class MeshInstanceRendererDatabaseEditor : Editor
    {
        [MenuItem("Assets/ZG/MeshInstance/Rebuild All Renderers")]
        public static void RebuildAllRenderers()
        {
            MeshInstanceRendererDatabase target;
            var guids = AssetDatabase.FindAssets("t:MeshInstanceRendererDatabase");
            string path;
            int numGUIDs = guids.Length;
            for(int i = 0; i < numGUIDs; ++i)
            {
                path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (EditorUtility.DisplayCancelableProgressBar("Rebuild All Renderers", path, i * 1.0f / numGUIDs))
                    break;

                target = AssetDatabase.LoadAssetAtPath<MeshInstanceRendererDatabase>(path);
                if (target == null)
                    continue;

                if(target.root == null)
                {
                    Debug.LogError($"{target.name} missing root", target.root);

                    continue;
                }

                switch (PrefabUtility.GetPrefabAssetType(target.root))
                {
                    case PrefabAssetType.Regular:
                    case PrefabAssetType.Variant:
                        var root = (Transform)PrefabUtility.InstantiatePrefab(target.root);
                        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
                        if (renderers != null)
                        {
                            foreach (var renderer in renderers)
                            {
                                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
                            }
                        }

                        PrefabUtility.RecordPrefabInstancePropertyModifications(root);

                        var prefab = PrefabUtility.GetNearestPrefabInstanceRoot(root);

                        PrefabUtility.ApplyPrefabInstance(prefab, InteractionMode.AutomatedAction);

                        DestroyImmediate(prefab);
                        break;
                }

                MeshInstanceRendererDatabase.Data.isShowProgressBar = false;
                target.Create();

                target.EditorMaskDirty();
            }

            EditorUtility.ClearProgressBar();
        }

        public override void OnInspectorGUI()
        {
            var target = base.target as MeshInstanceRendererDatabase;

            EditorGUI.BeginChangeCheck();
            target.root = EditorGUILayout.ObjectField(target.root, typeof(Transform), true) as Transform;
            if (EditorGUI.EndChangeCheck() || GUILayout.Button("Rebuild"))
            {
                if (target.root != null)
                {
                    MeshInstanceRendererDatabase.Data.isShowProgressBar = true;
                    target.Create();

                    if (PrefabUtility.GetPrefabInstanceStatus(target.root) == PrefabInstanceStatus.Connected)
                        target.root = PrefabUtility.GetCorrespondingObjectFromSource(target.root);

                    /*switch (__type)
                    {
                        case Type.Default:
                            target.nodes = MeshInstanceDatabase.Create(
                                __transform.GetComponentsInChildren<Renderer>(),
                                __transform.GetComponentsInChildren<LODGroup>(),
                                out target.objects);
                            break;
                        case Type.Static:
                            target.nodes = MeshInstanceDatabase.CreateStatic(true, __transform, out target.objects);
                            break;
                        case Type.Dynamic:
                            target.nodes = MeshInstanceDatabase.CreateDynamic(true, __transform, out target.objects);
                            break;
                    }*/
                }

                target.EditorMaskDirty();
            }

            if (GUILayout.Button("Instantiatelegacy"))
                target.data.InstantiateLegacy(target.name, target.meshes, target.materials);

            base.OnInspectorGUI();
        }
    }
}