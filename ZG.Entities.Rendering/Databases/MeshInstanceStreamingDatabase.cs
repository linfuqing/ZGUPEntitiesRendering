using System.Collections;
using UnityEngine;

namespace ZG
{
    [CreateAssetMenu(fileName = "Mesh Instance Streaming Database", menuName = "ZG/Mesh Instance/Streaming Database")]
    public class MeshInstanceStreamingDatabase : ScriptableObject
    {
        [SerializeField, GUIReadOnly]
        internal string _path;

        [SerializeField, GUIReadOnly]
        internal long _pathOffset;

        [SerializeField, GUIReadOnly]
        internal int _vertexCount;

        [SerializeField, GUIReadOnly]
        internal byte[] _md5Hash;

        [SerializeField]
        internal MeshInstanceStreamingSettingsBase _settings;

        private IMeshInstanceStreamingManager __manager;

        public MeshInstanceStreamingSettingsBase settings
        {
            set
            {
                _settings = value;
            }
        }

        public bool isVail => _vertexCount > 0;

        public int vertexCount => _vertexCount;

        public uint vertexOffset => __manager == null ? uint.MaxValue : __manager.vertexOffset;

        public int refCount
        {
            get;

            private set;
        }

        public IEnumerator Load()
        {
            if (__manager != null)
            {
                ++refCount;
                
                return null;
            }

            refCount = 1;

            __manager = _settings.CreateManager(name);

            return __manager.Load(_path, _pathOffset, _vertexCount, _md5Hash);
        }

        public void Unload()
        {
            if (__manager != null && --refCount < 1)
            {
                __manager.Unload();

                __manager = null;
            }
        }

        public Mesh CreateMesh()
        {
            if (__manager == null)
                return null;

            return _settings.CreateMesh(this);
        }

        public GameObject CreateGameObject()
        {
            var gameObject = new GameObject(name);
            gameObject.AddComponent<MeshFilter>().sharedMesh = CreateMesh();
            gameObject.AddComponent<MeshRenderer>();

            return gameObject;
        }

        protected void OnDestroy()
        {
            if (__manager != null)
            {
                __manager.Unload();

                __manager = null;
            }
        }

#if UNITY_EDITOR
        public void Save()
        {
            _path = _settings.name;

            _settings.Save(
                System.IO.Path.Combine(Application.streamingAssetsPath, _path),
                out _pathOffset,
                out _vertexCount, 
                out _md5Hash);

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

    }
}