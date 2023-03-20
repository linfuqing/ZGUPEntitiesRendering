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

            MeshStreamingVertexOffset vertexOffset;
            vertexOffset.value = _database.vertexOffset;
            this.SetComponentData(vertexOffset);
        }

        void IEntityComponent.Init(in Unity.Entities.Entity entity, EntityComponentAssigner assigner)
        {
            MeshStreamingVertexOffset vertexOffset;
            vertexOffset.value = uint.MaxValue;
            assigner.SetComponentData(entity, vertexOffset);
        }
    }
}