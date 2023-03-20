using UnityEngine;

namespace ZG
{
    [EntityComponent(typeof(MeshInstanceOccluderData))]
    public class MeshInstanceOccluderComponent : MonoBehaviour, IEntityComponent
    {
        [SerializeField]
        internal MeshInstanceOccluderDatabase _database;

        public MeshInstanceOccluderDatabase database
        {
            get => _database;

            set => _database = value;
        }

        void IEntityComponent.Init(in Unity.Entities.Entity entity, EntityComponentAssigner assigner)
        {
            MeshInstanceOccluderData instance;
            instance.definition = _database.definition;
            assigner.SetComponentData(entity, instance);
        }
    }
}