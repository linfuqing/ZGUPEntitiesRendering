using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ZG
{
    public abstract class MeshInstanceDatabase : ScriptableObject
    {
        /*private int __refCount;

        public void Release()
        {
            if (--__refCount == 0)
                Destroy();
        }

        public void Retain()
        {
            if (++__refCount == 1)
                Init();
        }*/

        public abstract void Init();

        public abstract void Destroy();
    }

    public abstract class MeshInstanceDatabase<T> : MeshInstanceDatabase where T : MeshInstanceDatabase<T>
    {
        private static Dictionary<int, MeshInstanceDatabase<T>> __instanceObjects;

        public abstract int instanceID
        {
            get;
        }

        ~MeshInstanceDatabase()
        {
            Dispose();
        }

        public void Dispose()
        {
            Destroy();

            _Dispose();
        }

        public override void Init()
        {
            var instanceID = this.instanceID;
            if (instanceID == 0)
                return;

            if (__instanceObjects == null)
                __instanceObjects = new Dictionary<int, MeshInstanceDatabase<T>>();

            if (__instanceObjects.TryGetValue(instanceID, out var instanceObject))
            {
                if (ReferenceEquals(instanceObject, this))
                    return;

                if(instanceObject is T)
                    instanceObject._Destroy();
            }

            __instanceObjects[instanceID] = this;

            _Init();
        }

        public override void Destroy()
        {
            if (__instanceObjects == null || 
                !__instanceObjects.TryGetValue(instanceID, out var instanceObject) || 
                !ReferenceEquals(instanceObject, this))
                return;

            _Destroy();

            __instanceObjects.Remove(instanceID);
        }

        protected void OnEnable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                return;
#endif
            Init();
        }

        protected void OnDisable()
        {
            Destroy();
        }

        protected void OnDestroy()
        {
            Dispose();
        }

        protected abstract void _Dispose();

        protected abstract void _Destroy();

        protected abstract void _Init();
    }
}