using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Collections;

namespace ZG
{
    public struct MaterialPropertyOverrideInit<T> : IComponentData where T : struct, IComponentData
    {
    }

    public struct MaterialPropertyOverride<T> : IComponentData where T : struct, IComponentData
    {
        public T value;
    }

    public struct MaterialPropertyInitSystem<T> where T : unmanaged, IComponentData
    {
        private EntityQuery __groupToCreate;
        private EntityQuery __groupToDestroy;
        private SharedHashMap<Entity, MeshInstanceRendererBuilder> __rendererBuilders;

        public void OnCreate(ref SystemState systemState)
        {
            __groupToDestroy = systemState.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MaterialPropertyOverrideInit<T>>()
                    },

                    None = new ComponentType[]
                    {
                        typeof(MeshInstanceNode)
                    }
                },
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MaterialPropertyOverrideInit<T>>()
                    },

                    Any = new ComponentType[]
                    {
                        typeof(MeshInstanceRendererDisabled),
                        typeof(MeshInstanceRendererDirty)
                    },

                    Options = EntityQueryOptions.IncludeDisabledEntities
                });

            __groupToCreate = systemState.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<MeshInstanceNode>(),
                        ComponentType.ReadOnly<MaterialPropertyOverride<T>>()
                    },
                    None = new ComponentType[]
                    {
                        typeof(MaterialPropertyOverrideInit<T>)
                    }, 
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });

            __rendererBuilders = systemState.World.GetOrCreateSystemUnmanaged<MeshInstanceRendererSystem>().builders;
        }

        public void OnDestroy(ref SystemState systemState)
        {

        }

        public void OnUpdate(ref SystemState systemState)
        {
            var entityManager = systemState.EntityManager;

            entityManager.RemoveComponent<MaterialPropertyOverrideInit<T>>(__groupToDestroy);

            using (var entitiesToCreate = new NativeList<Entity>(Allocator.TempJob))
            using (var entitiesToInit = new NativeList<Entity>(Allocator.TempJob))
            {
                __rendererBuilders.lookupJobManager.CompleteReadOnlyDependency();
                systemState.CompleteDependency();

                MaterialPropertyCollect collect;
                collect.builders = __rendererBuilders.reader;
                collect.entityType = systemState.GetEntityTypeHandle();
                collect.rendererType = systemState.GetBufferTypeHandle<MeshInstanceNode>(true);
                collect.entitiesToCreate = entitiesToCreate;
                collect.entitiesToInit = entitiesToInit;
                collect.Run(__groupToCreate);

                entityManager.AddComponentBurstCompatible<T>(entitiesToCreate.AsArray());

                entityManager.AddComponentBurstCompatible<MaterialPropertyOverrideInit<T>>(entitiesToInit.AsArray());
            }
        }
    }

    public struct MaterialPropertyChangeSystem<T> where T : unmanaged, IComponentData
    {
        private EntityQuery __group;

        public void OnCreate(ref SystemState systemState)
        {
            __group = systemState.GetEntityQuery(
                ComponentType.ReadOnly<MeshInstanceNode>(), 
                ComponentType.ReadOnly<MaterialPropertyOverride<T>>(),
                ComponentType.ReadOnly<MaterialPropertyOverrideInit<T>>());
            __group.SetChangedVersionFilter(new ComponentType[] { typeof(MeshInstanceNode), typeof(MaterialPropertyOverride<T>) });
        }

        public void OnDestroy(ref SystemState systemState)
        {

        }

        public void OnUpdate(ref SystemState systemState)
        {
            MaterialPropertyChange<T> change;
            change.overrideType = systemState.GetComponentTypeHandle<MaterialPropertyOverride<T>>(true);
            change.nodeType = systemState.GetBufferTypeHandle<MeshInstanceNode>(true);
            change.values = systemState.GetComponentLookup<T>();

            systemState.Dependency = change.ScheduleParallel(__group, systemState.Dependency);
        }
    }

    [UpdateInGroup(typeof(MeshInstanceSystemGroup), OrderLast = true)/*, UpdateAfter(typeof(MeshInstanceMaterialPropertyTypeInitSystem))*/]
    public partial class MaterialPropertyInitSystemGroup : ComponentSystemGroup
    {

    }
}