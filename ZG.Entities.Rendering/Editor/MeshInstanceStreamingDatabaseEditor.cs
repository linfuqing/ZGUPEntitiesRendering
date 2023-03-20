using UnityEngine;
using UnityEditor;

namespace ZG
{
    [CustomEditor(typeof(MeshInstanceStreamingDatabase), true)]
    public class MeshInstanceStreamingDatabaseEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            if(GUILayout.Button("Create GameObject"))
                ((MeshInstanceStreamingDatabase)target).CreateGameObject();

            base.OnInspectorGUI();
        }
    }
}