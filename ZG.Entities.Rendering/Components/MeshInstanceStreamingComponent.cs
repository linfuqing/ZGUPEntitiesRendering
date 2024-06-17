using System.Collections;
using UnityEngine;

namespace ZG
{
    [EntityComponent(typeof(MeshStreamingVertexOffset))]
    public class MeshInstanceStreamingComponent : EntityProxyComponent, IEntityComponent
    {
        private Coroutine __coroutine;
        
        [SerializeField]
        internal MeshInstanceStreamingDatabase _database;

        public MeshInstanceStreamingDatabase database
        {
            set
            {
                _database = value;
            }
        }

        protected void OnEnable()
        {
            __coroutine = StartCoroutine(__Load());
        }

        protected void OnDisable()
        {
            if (__coroutine != null)
            {
                StopCoroutine(__coroutine);

                __coroutine = null;
            }

            _database.Unload();
        }

        private IEnumerator __Load()
        {
            yield return _database.Load();

            this.SetComponentData(new MeshStreamingVertexOffset(_database.vertexOffset));

            __coroutine = null;
        }

        void IEntityComponent.Init(in Unity.Entities.Entity entity, EntityComponentAssigner assigner)
        {
            assigner.SetComponentData(entity, new MeshStreamingVertexOffset(uint.MaxValue));
        }
    }
}