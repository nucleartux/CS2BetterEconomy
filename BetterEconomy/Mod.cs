using Colossal.Logging;
using Game;
using Game.Modding;
using Unity.Entities;
using Game.Simulation;
using BetterEconomy.Systems;

namespace BetterEconomy
{
    public class BetterEconomy : IMod
    {

        public static readonly ILog log = LogManager.GetLogger($"{nameof(BetterEconomy)}").SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<CompanyMoveAwaySystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedCompanyMoveAwaySystem>(SystemUpdatePhase.GameSimulation);
            
            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<ProcessingCompanySystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedProcessingCompanySystem>(SystemUpdatePhase.GameSimulation);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<BuildingUpkeepSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedBuildingUpkeepSystem>(SystemUpdatePhase.GameSimulation);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<PropertyRenterSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedPropertyRenterSystem>(SystemUpdatePhase.GameSimulation);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<LeisureSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedLeisureSystem>(SystemUpdatePhase.GameSimulation);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<ResourceExporterSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedResourceExporterSystem>(SystemUpdatePhase.GameSimulation);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<UtilityFeeSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedUtilityFeeSystem>(SystemUpdatePhase.GameSimulation);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<ResourceBuyerSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
         }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
