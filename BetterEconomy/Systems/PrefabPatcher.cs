using Game.Prefabs;
using Unity.Entities;

namespace BetterEconomy.Systems;

internal class PrefabPatcher {
    private EntityManager m_EntityManager;
    private PrefabSystem m_PrefabSystem;

    internal PrefabPatcher() {
        m_PrefabSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<PrefabSystem>();
        m_EntityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
    }

    internal bool TryGetPrefab(string prefabType, string prefabName, out PrefabBase prefabBase, out Entity entity) {
        prefabBase = null;
        entity = default;
        PrefabID prefabID = new PrefabID(prefabType, prefabName);
        return m_PrefabSystem.TryGetPrefab(prefabID, out prefabBase) && m_PrefabSystem.TryGetEntity(prefabBase, out entity);
    }

    internal void PatchEconomyParameters() {
        if (TryGetPrefab(nameof(EconomyPrefab), "EconomyParameters", out PrefabBase prefabBase, out Entity entity) && m_PrefabSystem.TryGetComponentData<EconomyParameterData>(prefabBase, out EconomyParameterData comp)) {
           // BetterEconomy.log.Info($"comm {comp.m_CommercialEfficiency}, ext {comp.m_ExtractorProductionEfficiency}, ind {comp.m_IndustrialEfficiency}");
           // comm 1, ext 8, ind 2
           comp.m_CommercialEfficiency = 0.8f;
           comp.m_IndustrialEfficiency = 8;
           comp.m_ExtractorProductionEfficiency = 4;
           m_PrefabSystem.AddComponentData(prefabBase, comp);
        }
    }
}