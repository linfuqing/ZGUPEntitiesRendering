using System.Collections;
using UnityEngine;

namespace ZG
{
    [EntityComponent(typeof(MeshStreamingVertexOffset))]
    public class MeshInstanceStreamingComponent : EntityProxyComponent, IEntityComponent
    {
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
            StartCoroutine(__Load());
        }

        protected void OnDisable()
        {
            _database.Unload();
        }

        private IEnumerator __Load()
        {
            yield return _database.Load();

            this.SetComponentData(new MeshStreamingVertexOffset(_database.vertexOffset));
        }

        void IEntityComponent.Init(in Unity.Entities.Entity entity, EntityComponentAssigner assigner)
        {
            assigner.SetComponentData(entity, new MeshStreamingVertexOffset(uint.MaxValue));
        }
    }
}