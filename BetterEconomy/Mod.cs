using Colossal.Logging;
using Game;
using Game.Modding;
using Unity.Entities;
using Game.Simulation;
using BetterEconomy.Systems;
using System.Reflection;
using System;
using System.Linq;
using Game.SceneFlow;

namespace BetterEconomy
{
    public class BetterEconomy : IMod
    {

        public static readonly ILog log = LogManager.GetLogger($"{nameof(BetterEconomy)}").SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            bool plopGrowablesFound = GameManager.instance.modManager.Any(mod => {
                return mod.asset.name.Equals("PlopTheGrowables");
            });

            bool realisticTripsFound = GameManager.instance.modManager.Any(mod => {
                return mod.asset.name.Equals("Time2Work");
            });

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<CompanyMoveAwaySystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedCompanyMoveAwaySystem>(SystemUpdatePhase.GameSimulation);
            
            // if (plopGrowablesFound) {
            //     log.Warn("PlopTheGrowables found");
            // } else {
            //     World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<BuildingUpkeepSystem>().Enabled = false;
            //     updateSystem.UpdateAt<ModifiedBuildingUpkeepSystem>(SystemUpdatePhase.GameSimulation);
            // }

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<PropertyRenterSystem>().Enabled = false;
            updateSystem.UpdateAt<ModifiedPropertyRenterSystem>(SystemUpdatePhase.GameSimulation);

            if (realisticTripsFound) {
                log.Warn("Time2Work found");
            } else {
                World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<LeisureSystem>().Enabled = false;
                updateSystem.UpdateAt<ModifiedLeisureSystem>(SystemUpdatePhase.GameSimulation);
            }

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
