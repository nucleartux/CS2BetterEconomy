using System.Runtime.CompilerServices;
using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Events;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;

public partial class ModifiedLeisureSystem : GameSystemBase
{
	[BurstCompile]
	private struct SpendLeisurejob : IJob
	{
		public NativeQueue<LeisureEvent> m_LeisureQueue;

		public ComponentLookup<ServiceAvailable> m_ServiceAvailables;

		public BufferLookup<Game.Economy.Resources> m_Resources;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_Prefabs;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> m_IndustrialProcesses;

		[ReadOnly]
		public ComponentLookup<HouseholdMember> m_HouseholdMembers;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<ServiceCompanyData> m_ServiceCompanyDatas;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		public void Execute()
		{
			LeisureEvent item;
			while (m_LeisureQueue.TryDequeue(out item))
			{
				if (!m_HouseholdMembers.HasComponent(item.m_Citizen) || !m_Prefabs.HasComponent(item.m_Provider))
				{
					continue;
				}
				Entity household = m_HouseholdMembers[item.m_Citizen].m_Household;
				Entity prefab = m_Prefabs[item.m_Provider].m_Prefab;
				if (!m_IndustrialProcesses.HasComponent(prefab))
				{
					continue;
				}
				Resource resource = m_IndustrialProcesses[prefab].m_Output.m_Resource;
				if (resource == Resource.NoResource || !m_Resources.HasBuffer(item.m_Provider) || !m_Resources.HasBuffer(household))
				{
					continue;
				}
				bool flag = false;
				float num = EconomyUtils.GetMarketPrice(resource, m_ResourcePrefabs, ref m_ResourceDatas);
				int amount = kLeisureConsumeAmount;
				// some bug here
				// some bug wuth export to outside connection
				if (m_ServiceAvailables.HasComponent(item.m_Provider) && m_ServiceCompanyDatas.HasComponent(prefab))
				{
					ServiceAvailable value = m_ServiceAvailables[item.m_Provider];
					ServiceCompanyData serviceCompanyData = m_ServiceCompanyDatas[prefab];
				    // num *= (float)serviceCompanyData.m_ServiceConsuming;
					amount *= serviceCompanyData.m_ServiceConsuming;
					//	BetterEconomy.log.Info($"[positive]leisure service cons {serviceCompanyData.m_ServiceConsuming}, available {value.m_ServiceAvailable}");
					
					if (value.m_ServiceAvailable > 0)
					{
						value.m_ServiceAvailable -= serviceCompanyData.m_ServiceConsuming;
						value.m_MeanPriority = math.lerp(value.m_MeanPriority, (float)value.m_ServiceAvailable / (float)serviceCompanyData.m_MaxService, 0.1f);
						m_ServiceAvailables[item.m_Provider] = value;
						num *= EconomyUtils.GetServicePriceMultiplier(value.m_ServiceAvailable, serviceCompanyData.m_MaxService);
						//	BetterEconomy.log.Info($"[positive]leisure service mult {EconomyUtils.GetServicePriceMultiplier(value.m_ServiceAvailable, serviceCompanyData.m_MaxService)}");
					}
					else
					{
						flag = true;
					}
				}
				if (!flag)
				{
					DynamicBuffer<Game.Economy.Resources> resources = m_Resources[item.m_Provider];
					if (EconomyUtils.GetResources(resource, resources) > amount)
					{
						DynamicBuffer<Game.Economy.Resources> resources2 = m_Resources[household];
						// bug 5
						EconomyUtils.AddResources(resource, -amount, resources);
						num *= (float)amount;
						// BetterEconomy.log.Info($"[nagative]leisure total {-num}, amount {amount}, market {EconomyUtils.GetMarketPrice(resource, m_ResourcePrefabs, ref m_ResourceDatas)} for {household.Index}");
					
						EconomyUtils.AddResources(Resource.Money, Mathf.RoundToInt(num), resources);
						EconomyUtils.AddResources(Resource.Money, -Mathf.RoundToInt(num), resources2);
					}
				}
			}
		}
	}

	[BurstCompile]
	private struct LeisureJob : IJobChunk
	{
		public ComponentTypeHandle<Leisure> m_LeisureType;

		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public ComponentTypeHandle<HouseholdMember> m_HouseholdMemberType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		public BufferTypeHandle<TripNeeded> m_TripType;

		[ReadOnly]
		public ComponentTypeHandle<CreatureData> m_CreatureDataType;

		[ReadOnly]
		public ComponentTypeHandle<ResidentData> m_ResidentDataType;

		[ReadOnly]
		public ComponentLookup<PathInformation> m_PathInfos;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> m_PropertyRenters;

		[ReadOnly]
		public ComponentLookup<Target> m_Targets;

		[ReadOnly]
		public ComponentLookup<CarKeeper> m_CarKeepers;

		[ReadOnly]
		public ComponentLookup<ParkedCar> m_ParkedCarData;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.PersonalCar> m_PersonalCarData;

		[ReadOnly]
		public ComponentLookup<CurrentBuilding> m_CurrentBuildings;

		[ReadOnly]
		public ComponentLookup<Building> m_BuildingData;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_PrefabRefs;

		[ReadOnly]
		public ComponentLookup<LeisureProviderData> m_LeisureProviderDatas;

		[ReadOnly]
		public ComponentLookup<Worker> m_Workers;

		[ReadOnly]
		public ComponentLookup<Game.Citizens.Student> m_Students;

		[ReadOnly]
		public BufferLookup<Game.Economy.Resources> m_Resources;

		[ReadOnly]
		public ComponentLookup<Household> m_Households;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> m_Renters;

		[NativeDisableParallelForRestriction]
		public ComponentLookup<Citizen> m_CitizenDatas;

		[ReadOnly]
		public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;

		[ReadOnly]
		public ComponentLookup<CarData> m_PrefabCarData;

		[ReadOnly]
		public ComponentLookup<ObjectGeometryData> m_ObjectGeometryData;

		[ReadOnly]
		public ComponentLookup<HumanData> m_PrefabHumanData;

		[ReadOnly]
		public ComponentLookup<TravelPurpose> m_Purposes;

		[ReadOnly]
		public ComponentLookup<OutsideConnectionData> m_OutsideConnectionDatas;

		[ReadOnly]
		public ComponentLookup<TouristHousehold> m_TouristHouseholds;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> m_IndustrialProcesses;

		[ReadOnly]
		public ComponentLookup<ServiceAvailable> m_ServiceAvailables;

		[ReadOnly]
		public ComponentLookup<Population> m_PopulationData;

		[ReadOnly]
		public BufferLookup<Renter> m_RenterBufs;

		[ReadOnly]
		public ComponentLookup<ConsumptionData> m_ConsumptionDatas;

		[ReadOnly]
		public RandomSeed m_RandomSeed;

		[ReadOnly]
		public ComponentTypeSet m_PathfindTypes;

		[ReadOnly]
		public NativeList<ArchetypeChunk> m_HumanChunks;

		public EconomyParameterData m_EconomyParameters;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public NativeQueue<SetupQueueItem>.ParallelWriter m_PathfindQueue;

		public NativeQueue<LeisureEvent>.ParallelWriter m_LeisureQueue;

		public NativeQueue<AddMeetingSystem.AddMeeting>.ParallelWriter m_MeetingQueue;

		public uint m_SimulationFrame;

		public uint m_UpdateFrameIndex;

		public float m_TimeOfDay;

		public float m_Weather;

		public float m_Temperature;

		public Entity m_PopulationEntity;

		public TimeData m_TimeData;

		private void SpendLeisure(int index, Entity entity, ref Citizen citizen, ref Leisure leisure, Entity providerEntity, LeisureProviderData provider)
		{
			bool flag = m_BuildingData.HasComponent(providerEntity) && BuildingUtils.CheckOption(m_BuildingData[providerEntity], BuildingOption.Inactive);
			if (m_ServiceAvailables.HasComponent(providerEntity) && m_ServiceAvailables[providerEntity].m_ServiceAvailable <= 0)
			{
				flag = true;
			}
			Entity prefab = m_PrefabRefs[providerEntity].m_Prefab;
			if (!flag && m_IndustrialProcesses.HasComponent(prefab))
			{
				Resource resource = m_IndustrialProcesses[prefab].m_Output.m_Resource;
				if (resource != Resource.NoResource && m_Resources.HasBuffer(providerEntity) && EconomyUtils.GetResources(resource, m_Resources[providerEntity]) <= 0)
				{
					flag = true;
				}
			}
			if (!flag)
			{
				citizen.m_LeisureCounter = (byte)math.min(255, citizen.m_LeisureCounter + provider.m_Efficiency);
				m_LeisureQueue.Enqueue(new LeisureEvent
				{
					m_Citizen = entity,
					m_Provider = providerEntity
				});
			}
			if (citizen.m_LeisureCounter > 250 || m_SimulationFrame >= leisure.m_LastPossibleFrame || flag)
			{
				m_CommandBuffer.RemoveComponent<Leisure>(index, entity);
			}
		}

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<Leisure> nativeArray2 = chunk.GetNativeArray(ref m_LeisureType);
			NativeArray<HouseholdMember> nativeArray3 = chunk.GetNativeArray(ref m_HouseholdMemberType);
			BufferAccessor<TripNeeded> bufferAccessor = chunk.GetBufferAccessor(ref m_TripType);
			int population = m_PopulationData[m_PopulationEntity].m_Population;
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(unfilteredChunkIndex);
			for (int i = 0; i < nativeArray.Length; i++)
			{
				Entity entity = nativeArray[i];
				Leisure leisure = nativeArray2[i];
				DynamicBuffer<TripNeeded> dynamicBuffer = bufferAccessor[i];
				Citizen citizen = m_CitizenDatas[entity];
				bool flag = m_Purposes.HasComponent(entity) && m_Purposes[entity].m_Purpose == Purpose.Traveling;
				Entity providerEntity = leisure.m_TargetAgent;
				Entity entity2 = Entity.Null;
				LeisureProviderData provider = default(LeisureProviderData);
				if (leisure.m_TargetAgent != Entity.Null && m_CurrentBuildings.HasComponent(entity))
				{
					Entity currentBuilding = m_CurrentBuildings[entity].m_CurrentBuilding;
					if (m_PropertyRenters.HasComponent(leisure.m_TargetAgent) && m_PropertyRenters[leisure.m_TargetAgent].m_Property == currentBuilding && m_PrefabRefs.HasComponent(leisure.m_TargetAgent))
					{
						Entity prefab = m_PrefabRefs[leisure.m_TargetAgent].m_Prefab;
						if (m_LeisureProviderDatas.HasComponent(prefab))
						{
							entity2 = prefab;
							provider = m_LeisureProviderDatas[entity2];
						}
					}
					else if (m_PrefabRefs.HasComponent(currentBuilding))
					{
						Entity prefab2 = m_PrefabRefs[currentBuilding].m_Prefab;
						providerEntity = currentBuilding;
						if (m_LeisureProviderDatas.HasComponent(prefab2))
						{
							entity2 = prefab2;
							provider = m_LeisureProviderDatas[entity2];
						}
						else if (flag && m_OutsideConnectionDatas.HasComponent(prefab2))
						{
							entity2 = prefab2;
							LeisureProviderData leisureProviderData = default(LeisureProviderData);
							leisureProviderData.m_Efficiency = 20;
							leisureProviderData.m_LeisureType = LeisureType.Travel;
							leisureProviderData.m_Resources = Resource.NoResource;
							provider = leisureProviderData;
						}
					}
				}
				if (entity2 != Entity.Null)
				{
					SpendLeisure(unfilteredChunkIndex, entity, ref citizen, ref leisure, providerEntity, provider);
					nativeArray2[i] = leisure;
					m_CitizenDatas[entity] = citizen;
				}
				else if (!flag && m_PathInfos.HasComponent(entity))
				{
					PathInformation pathInformation = m_PathInfos[entity];
					if ((pathInformation.m_State & PathFlags.Pending) != 0)
					{
						continue;
					}
					Entity destination = pathInformation.m_Destination;
					if ((m_PropertyRenters.HasComponent(destination) || m_PrefabRefs.HasComponent(destination)) && !m_Targets.HasComponent(entity))
					{
						if ((!m_Workers.HasComponent(entity) || WorkerSystem.IsTodayOffDay(citizen, ref m_EconomyParameters, m_SimulationFrame, m_TimeData, population) || !WorkerSystem.IsTimeToWork(citizen, m_Workers[entity], ref m_EconomyParameters, m_TimeOfDay)) && (!m_Students.HasComponent(entity) || StudentSystem.IsTimeToStudy(citizen, m_Students[entity], ref m_EconomyParameters, m_TimeOfDay, m_SimulationFrame, m_TimeData, population)))
						{
							Entity prefab3 = m_PrefabRefs[destination].m_Prefab;
							if (m_LeisureProviderDatas[prefab3].m_Efficiency == 0)
							{
								UnityEngine.Debug.LogWarning($"Warning: Leisure provider {destination.Index} has zero efficiency");
							}
							leisure.m_TargetAgent = destination;
							nativeArray2[i] = leisure;
							TripNeeded elem = default(TripNeeded);
							elem.m_TargetAgent = destination;
							elem.m_Purpose = Purpose.Leisure;
							dynamicBuffer.Add(elem);
							m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, new Target
							{
								m_Target = destination
							});
						}
						else
						{
							if (m_Purposes.HasComponent(entity) && (m_Purposes[entity].m_Purpose == Purpose.Leisure || m_Purposes[entity].m_Purpose == Purpose.Traveling))
							{
								m_CommandBuffer.RemoveComponent<TravelPurpose>(unfilteredChunkIndex, entity);
							}
							m_CommandBuffer.RemoveComponent<Leisure>(unfilteredChunkIndex, entity);
							m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
						}
					}
					else if (!m_Targets.HasComponent(entity))
					{
						if (m_Purposes.HasComponent(entity) && (m_Purposes[entity].m_Purpose == Purpose.Leisure || m_Purposes[entity].m_Purpose == Purpose.Traveling))
						{
							m_CommandBuffer.RemoveComponent<TravelPurpose>(unfilteredChunkIndex, entity);
						}
						m_CommandBuffer.RemoveComponent<Leisure>(unfilteredChunkIndex, entity);
						m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
					}
				}
				else if (!m_Purposes.HasComponent(entity))
				{
					Entity household = nativeArray3[i].m_Household;
					FindLeisure(unfilteredChunkIndex, entity, household, citizen, ref random, m_TouristHouseholds.HasComponent(household));
					nativeArray2[i] = leisure;
				}
			}
		}

		private float GetWeight(LeisureType type, int wealth, CitizenAge age)
		{
			float num = 1f;
			float num2;
			float a;
			float num3;
			switch (type)
			{
			case LeisureType.Meals:
				num2 = 10f;
				a = 0.2f;
				num3 = age switch
				{
					CitizenAge.Child => 10f, 
					CitizenAge.Teen => 25f, 
					CitizenAge.Elderly => 35f, 
					_ => 35f, 
				};
				break;
			case LeisureType.Entertainment:
				num2 = 10f;
				a = 0.3f;
				num3 = age switch
				{
					CitizenAge.Child => 0f, 
					CitizenAge.Teen => 45f, 
					CitizenAge.Elderly => 10f, 
					_ => 45f, 
				};
				break;
			case LeisureType.Commercial:
				num2 = 10f;
				a = 0.4f;
				num3 = age switch
				{
					CitizenAge.Child => 20f, 
					CitizenAge.Teen => 25f, 
					CitizenAge.Elderly => 25f, 
					_ => 30f, 
				};
				break;
			case LeisureType.CityIndoors:
			case LeisureType.CityPark:
			case LeisureType.CityBeach:
				num2 = 10f;
				a = 0f;
				num3 = age switch
				{
					CitizenAge.Child => 30f, 
					CitizenAge.Teen => 25f, 
					CitizenAge.Elderly => 15f, 
					_ => 30f, 
				};
				num = type switch
				{
					LeisureType.CityIndoors => 1f, 
					LeisureType.CityPark => 2f * (1f - 0.95f * m_Weather), 
					_ => 0.05f + 4f * math.saturate(0.35f - m_Weather) * math.saturate((m_Temperature - 20f) / 30f), 
				};
				break;
			case LeisureType.Travel:
				num2 = 1f;
				a = 0.5f;
				num = 0.5f + math.saturate((30f - m_Temperature) / 50f);
				num3 = age switch
				{
					CitizenAge.Child => 15f, 
					CitizenAge.Teen => 15f, 
					CitizenAge.Elderly => 30f, 
					_ => 40f, 
				};
				break;
			default:
				num2 = 0f;
				a = 0f;
				num3 = 0f;
				num = 0f;
				break;
			}
			return num3 * num * num2 * math.smoothstep(a, 1f, ((float)wealth + 5000f) / 10000f);
		}

		private LeisureType SelectLeisureType(Entity household, bool tourist, Citizen citizenData, ref Unity.Mathematics.Random random)
		{
			PropertyRenter propertyRenter = (m_Renters.HasComponent(household) ? m_Renters[household] : default(PropertyRenter));
			if (tourist && random.NextFloat() < 0.3f)
			{
				return LeisureType.Attractions;
			}
			if (m_Households.HasComponent(household) && m_Resources.HasBuffer(household) && m_HouseholdCitizens.HasBuffer(household))
			{
				int wealth = ((!tourist) ? EconomyUtils.GetHouseholdSpendableMoney(m_Households[household], m_Resources[household], ref m_RenterBufs, ref m_ConsumptionDatas, ref m_PrefabRefs, propertyRenter) : EconomyUtils.GetResources(Resource.Money, m_Resources[household]));
				float num = 0f;
				CitizenAge age = citizenData.GetAge();
				for (int i = 0; i < 10; i++)
				{
					num += GetWeight((LeisureType)i, wealth, age);
				}
				float num2 = num * random.NextFloat();
				for (int j = 0; j < 10; j++)
				{
					num2 -= GetWeight((LeisureType)j, wealth, age);
					if (num2 <= 0.001f)
					{
						return (LeisureType)j;
					}
				}
			}
			UnityEngine.Debug.LogWarning("Leisure type randomization failed");
			return LeisureType.Count;
		}

		private void FindLeisure(int chunkIndex, Entity citizen, Entity household, Citizen citizenData, ref Unity.Mathematics.Random random, bool tourist)
		{
			LeisureType leisureType = SelectLeisureType(household, tourist, citizenData, ref random);
			float value = 255f - (float)(int)citizenData.m_LeisureCounter;
			if (leisureType == LeisureType.Travel || leisureType == LeisureType.Sightseeing || leisureType == LeisureType.Attractions)
			{
				if (m_Purposes.HasComponent(citizen))
				{
					m_CommandBuffer.RemoveComponent<TravelPurpose>(chunkIndex, citizen);
				}
				m_MeetingQueue.Enqueue(new AddMeetingSystem.AddMeeting
				{
					m_Household = household,
					m_Type = leisureType
				});
				return;
			}
			m_CommandBuffer.AddComponent(chunkIndex, citizen, in m_PathfindTypes);
			m_CommandBuffer.SetComponent(chunkIndex, citizen, new PathInformation
			{
				m_State = PathFlags.Pending
			});
			CreatureData creatureData;
			PseudoRandomSeed randomSeed;
			Entity entity = ObjectEmergeSystem.SelectResidentPrefab(citizenData, m_HumanChunks, m_EntityType, ref m_CreatureDataType, ref m_ResidentDataType, out creatureData, out randomSeed);
			HumanData humanData = default(HumanData);
			if (entity != Entity.Null)
			{
				humanData = m_PrefabHumanData[entity];
			}
			Household household2 = m_Households[household];
			DynamicBuffer<HouseholdCitizen> dynamicBuffer = m_HouseholdCitizens[household];
			PathfindParameters pathfindParameters = default(PathfindParameters);
			pathfindParameters.m_MaxSpeed = 277.77777f;
			pathfindParameters.m_WalkSpeed = humanData.m_WalkSpeed;
			pathfindParameters.m_Weights = CitizenUtils.GetPathfindWeights(citizenData, household2, dynamicBuffer.Length);
			pathfindParameters.m_Methods = PathMethod.Pedestrian | PathMethod.Taxi | RouteUtils.GetPublicTransportMethods(m_TimeOfDay);
			pathfindParameters.m_SecondaryIgnoredRules = VehicleUtils.GetIgnoredPathfindRulesTaxiDefaults();
			pathfindParameters.m_MaxCost = CitizenBehaviorSystem.kMaxPathfindCost;
			PathfindParameters parameters = pathfindParameters;
			if (m_PropertyRenters.HasComponent(household))
			{
				parameters.m_Authorization1 = m_PropertyRenters[household].m_Property;
			}
			if (m_Workers.HasComponent(citizen))
			{
				Worker worker = m_Workers[citizen];
				if (m_PropertyRenters.HasComponent(worker.m_Workplace))
				{
					parameters.m_Authorization2 = m_PropertyRenters[worker.m_Workplace].m_Property;
				}
				else
				{
					parameters.m_Authorization2 = worker.m_Workplace;
				}
			}
			if (m_CarKeepers.IsComponentEnabled(citizen))
			{
				Entity car = m_CarKeepers[citizen].m_Car;
				if (m_ParkedCarData.HasComponent(car))
				{
					PrefabRef prefabRef = m_PrefabRefs[car];
					ParkedCar parkedCar = m_ParkedCarData[car];
					CarData carData = m_PrefabCarData[prefabRef.m_Prefab];
					parameters.m_MaxSpeed.x = carData.m_MaxSpeed;
					parameters.m_ParkingTarget = parkedCar.m_Lane;
					parameters.m_ParkingDelta = parkedCar.m_CurvePosition;
					parameters.m_ParkingSize = VehicleUtils.GetParkingSize(car, ref m_PrefabRefs, ref m_ObjectGeometryData);
					parameters.m_Methods |= PathMethod.Road | PathMethod.Parking;
					parameters.m_IgnoredRules = VehicleUtils.GetIgnoredPathfindRules(carData);
					if (m_PersonalCarData.TryGetComponent(car, out var componentData) && (componentData.m_State & PersonalCarFlags.HomeTarget) == 0)
					{
						parameters.m_PathfindFlags |= PathfindFlags.ParkingReset;
					}
				}
			}
			SetupQueueTarget setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.CurrentLocation;
			setupQueueTarget.m_Methods = PathMethod.Pedestrian;
			setupQueueTarget.m_RandomCost = 30f;
			SetupQueueTarget origin = setupQueueTarget;
			setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.Leisure;
			setupQueueTarget.m_Methods = PathMethod.Pedestrian;
			setupQueueTarget.m_Value = (int)leisureType;
			setupQueueTarget.m_Value2 = value;
			setupQueueTarget.m_RandomCost = 30f;
			setupQueueTarget.m_ActivityMask = creatureData.m_SupportedActivities;
			SetupQueueTarget destination = setupQueueTarget;
			SetupQueueItem value2 = new SetupQueueItem(citizen, parameters, origin, destination);
			m_PathfindQueue.Enqueue(value2);
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	private struct TypeHandle
	{
		[ReadOnly]
		public EntityTypeHandle __Unity_Entities_Entity_TypeHandle;

		public ComponentTypeHandle<Leisure> __Game_Citizens_Leisure_RW_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<HouseholdMember> __Game_Citizens_HouseholdMember_RO_ComponentTypeHandle;

		public SharedComponentTypeHandle<UpdateFrame> __Game_Simulation_UpdateFrame_SharedComponentTypeHandle;

		public BufferTypeHandle<TripNeeded> __Game_Citizens_TripNeeded_RW_BufferTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<CreatureData> __Game_Prefabs_CreatureData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ResidentData> __Game_Prefabs_ResidentData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<PathInformation> __Game_Pathfind_PathInformation_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CurrentBuilding> __Game_Citizens_CurrentBuilding_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Building> __Game_Buildings_Building_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> __Game_Buildings_PropertyRenter_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CarKeeper> __Game_Citizens_CarKeeper_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ParkedCar> __Game_Vehicles_ParkedCar_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.PersonalCar> __Game_Vehicles_PersonalCar_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Target> __Game_Common_Target_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<LeisureProviderData> __Game_Prefabs_LeisureProviderData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Citizens.Student> __Game_Citizens_Student_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Worker> __Game_Citizens_Worker_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Household> __Game_Citizens_Household_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RO_BufferLookup;

		public ComponentLookup<Citizen> __Game_Citizens_Citizen_RW_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CarData> __Game_Prefabs_CarData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ObjectGeometryData> __Game_Prefabs_ObjectGeometryData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<HumanData> __Game_Prefabs_HumanData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<TravelPurpose> __Game_Citizens_TravelPurpose_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<OutsideConnectionData> __Game_Prefabs_OutsideConnectionData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<TouristHousehold> __Game_Citizens_TouristHousehold_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> __Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ServiceAvailable> __Game_Companies_ServiceAvailable_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Population> __Game_City_Population_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<HouseholdCitizen> __Game_Citizens_HouseholdCitizen_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<Renter> __Game_Buildings_Renter_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<ConsumptionData> __Game_Prefabs_ConsumptionData_RO_ComponentLookup;

		public ComponentLookup<ServiceAvailable> __Game_Companies_ServiceAvailable_RW_ComponentLookup;

		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferLookup;

		[ReadOnly]
		public ComponentLookup<HouseholdMember> __Game_Citizens_HouseholdMember_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ServiceCompanyData> __Game_Companies_ServiceCompanyData_RO_ComponentLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Citizens_Leisure_RW_ComponentTypeHandle = state.GetComponentTypeHandle<Leisure>();
			__Game_Citizens_HouseholdMember_RO_ComponentTypeHandle = state.GetComponentTypeHandle<HouseholdMember>(isReadOnly: true);
			__Game_Simulation_UpdateFrame_SharedComponentTypeHandle = state.GetSharedComponentTypeHandle<UpdateFrame>();
			__Game_Citizens_TripNeeded_RW_BufferTypeHandle = state.GetBufferTypeHandle<TripNeeded>();
			__Game_Prefabs_CreatureData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<CreatureData>(isReadOnly: true);
			__Game_Prefabs_ResidentData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ResidentData>(isReadOnly: true);
			__Game_Pathfind_PathInformation_RO_ComponentLookup = state.GetComponentLookup<PathInformation>(isReadOnly: true);
			__Game_Citizens_CurrentBuilding_RO_ComponentLookup = state.GetComponentLookup<CurrentBuilding>(isReadOnly: true);
			__Game_Buildings_Building_RO_ComponentLookup = state.GetComponentLookup<Building>(isReadOnly: true);
			__Game_Buildings_PropertyRenter_RO_ComponentLookup = state.GetComponentLookup<PropertyRenter>(isReadOnly: true);
			__Game_Citizens_CarKeeper_RO_ComponentLookup = state.GetComponentLookup<CarKeeper>(isReadOnly: true);
			__Game_Vehicles_ParkedCar_RO_ComponentLookup = state.GetComponentLookup<ParkedCar>(isReadOnly: true);
			__Game_Vehicles_PersonalCar_RO_ComponentLookup = state.GetComponentLookup<Game.Vehicles.PersonalCar>(isReadOnly: true);
			__Game_Common_Target_RO_ComponentLookup = state.GetComponentLookup<Target>(isReadOnly: true);
			__Game_Prefabs_PrefabRef_RO_ComponentLookup = state.GetComponentLookup<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_LeisureProviderData_RO_ComponentLookup = state.GetComponentLookup<LeisureProviderData>(isReadOnly: true);
			__Game_Citizens_Student_RO_ComponentLookup = state.GetComponentLookup<Game.Citizens.Student>(isReadOnly: true);
			__Game_Citizens_Worker_RO_ComponentLookup = state.GetComponentLookup<Worker>(isReadOnly: true);
			__Game_Citizens_Household_RO_ComponentLookup = state.GetComponentLookup<Household>(isReadOnly: true);
			__Game_Economy_Resources_RO_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>(isReadOnly: true);
			__Game_Citizens_Citizen_RW_ComponentLookup = state.GetComponentLookup<Citizen>();
			__Game_Prefabs_CarData_RO_ComponentLookup = state.GetComponentLookup<CarData>(isReadOnly: true);
			__Game_Prefabs_ObjectGeometryData_RO_ComponentLookup = state.GetComponentLookup<ObjectGeometryData>(isReadOnly: true);
			__Game_Prefabs_HumanData_RO_ComponentLookup = state.GetComponentLookup<HumanData>(isReadOnly: true);
			__Game_Citizens_TravelPurpose_RO_ComponentLookup = state.GetComponentLookup<TravelPurpose>(isReadOnly: true);
			__Game_Prefabs_OutsideConnectionData_RO_ComponentLookup = state.GetComponentLookup<OutsideConnectionData>(isReadOnly: true);
			__Game_Citizens_TouristHousehold_RO_ComponentLookup = state.GetComponentLookup<TouristHousehold>(isReadOnly: true);
			__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup = state.GetComponentLookup<IndustrialProcessData>(isReadOnly: true);
			__Game_Companies_ServiceAvailable_RO_ComponentLookup = state.GetComponentLookup<ServiceAvailable>(isReadOnly: true);
			__Game_City_Population_RO_ComponentLookup = state.GetComponentLookup<Population>(isReadOnly: true);
			__Game_Citizens_HouseholdCitizen_RO_BufferLookup = state.GetBufferLookup<HouseholdCitizen>(isReadOnly: true);
			__Game_Buildings_Renter_RO_BufferLookup = state.GetBufferLookup<Renter>(isReadOnly: true);
			__Game_Prefabs_ConsumptionData_RO_ComponentLookup = state.GetComponentLookup<ConsumptionData>(isReadOnly: true);
			__Game_Companies_ServiceAvailable_RW_ComponentLookup = state.GetComponentLookup<ServiceAvailable>();
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>();
			__Game_Citizens_HouseholdMember_RO_ComponentLookup = state.GetComponentLookup<HouseholdMember>(isReadOnly: true);
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Game_Companies_ServiceCompanyData_RO_ComponentLookup = state.GetComponentLookup<ServiceCompanyData>(isReadOnly: true);
		}
	}

	private static readonly int kLeisureConsumeAmount = 2;

	private SimulationSystem m_SimulationSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private PathfindSetupSystem m_PathFindSetupSystem;

	private TimeSystem m_TimeSystem;

	private ResourceSystem m_ResourceSystem;

	private ClimateSystem m_ClimateSystem;

	private AddMeetingSystem m_AddMeetingSystem;

	private EntityQuery m_LeisureQuery;

	private EntityQuery m_EconomyParameterQuery;

	private EntityQuery m_LeisureParameterQuery;

	private EntityQuery m_ResidentPrefabQuery;

	private EntityQuery m_TimeDataQuery;

	private EntityQuery m_PopulationQuery;

	private ComponentTypeSet m_PathfindTypes;

	private NativeQueue<LeisureEvent> m_LeisureQueue;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 16;
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_PathFindSetupSystem = base.World.GetOrCreateSystemManaged<PathfindSetupSystem>();
		m_TimeSystem = base.World.GetOrCreateSystemManaged<TimeSystem>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_ClimateSystem = base.World.GetOrCreateSystemManaged<ClimateSystem>();
		m_AddMeetingSystem = base.World.GetOrCreateSystemManaged<AddMeetingSystem>();
		m_EconomyParameterQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
		m_LeisureParameterQuery = GetEntityQuery(ComponentType.ReadOnly<LeisureParametersData>());
		m_LeisureQuery = GetEntityQuery(ComponentType.ReadWrite<Citizen>(), ComponentType.ReadWrite<Leisure>(), ComponentType.ReadWrite<TripNeeded>(), ComponentType.ReadWrite<CurrentBuilding>(), ComponentType.Exclude<HealthProblem>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		m_ResidentPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<ObjectData>(), ComponentType.ReadOnly<HumanData>(), ComponentType.ReadOnly<ResidentData>(), ComponentType.ReadOnly<PrefabData>());
		m_TimeDataQuery = GetEntityQuery(ComponentType.ReadOnly<TimeData>());
		m_PopulationQuery = GetEntityQuery(ComponentType.ReadOnly<Population>());
		m_PathfindTypes = new ComponentTypeSet(ComponentType.ReadWrite<PathInformation>(), ComponentType.ReadWrite<PathElement>());
		m_LeisureQueue = new NativeQueue<LeisureEvent>(Allocator.Persistent);
		RequireForUpdate(m_LeisureQuery);
		RequireForUpdate(m_EconomyParameterQuery);
		RequireForUpdate(m_LeisureParameterQuery);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		m_LeisureQueue.Dispose();
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrameWithInterval = SimulationUtils.GetUpdateFrameWithInterval(m_SimulationSystem.frameIndex, (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation), 16);
		float value = m_ClimateSystem.precipitation.value;
		__TypeHandle.__Game_Prefabs_ConsumptionData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Renter_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_City_Population_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_TouristHousehold_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_OutsideConnectionData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_TravelPurpose_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_HumanData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ObjectGeometryData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_CarData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Citizen_RW_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Household_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Student_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_LeisureProviderData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Common_Target_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Vehicles_PersonalCar_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Vehicles_ParkedCar_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_CarKeeper_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Building_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_CurrentBuilding_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Pathfind_PathInformation_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ResidentData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_CreatureData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_TripNeeded_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Leisure_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		LeisureJob jobData = default(LeisureJob);
		jobData.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		jobData.m_LeisureType = __TypeHandle.__Game_Citizens_Leisure_RW_ComponentTypeHandle;
		jobData.m_HouseholdMemberType = __TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentTypeHandle;
		jobData.m_UpdateFrameType = __TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle;
		jobData.m_TripType = __TypeHandle.__Game_Citizens_TripNeeded_RW_BufferTypeHandle;
		jobData.m_CreatureDataType = __TypeHandle.__Game_Prefabs_CreatureData_RO_ComponentTypeHandle;
		jobData.m_ResidentDataType = __TypeHandle.__Game_Prefabs_ResidentData_RO_ComponentTypeHandle;
		jobData.m_PathInfos = __TypeHandle.__Game_Pathfind_PathInformation_RO_ComponentLookup;
		jobData.m_CurrentBuildings = __TypeHandle.__Game_Citizens_CurrentBuilding_RO_ComponentLookup;
		jobData.m_BuildingData = __TypeHandle.__Game_Buildings_Building_RO_ComponentLookup;
		jobData.m_PropertyRenters = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup;
		jobData.m_CarKeepers = __TypeHandle.__Game_Citizens_CarKeeper_RO_ComponentLookup;
		jobData.m_ParkedCarData = __TypeHandle.__Game_Vehicles_ParkedCar_RO_ComponentLookup;
		jobData.m_PersonalCarData = __TypeHandle.__Game_Vehicles_PersonalCar_RO_ComponentLookup;
		jobData.m_Targets = __TypeHandle.__Game_Common_Target_RO_ComponentLookup;
		jobData.m_PrefabRefs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
		jobData.m_LeisureProviderDatas = __TypeHandle.__Game_Prefabs_LeisureProviderData_RO_ComponentLookup;
		jobData.m_Students = __TypeHandle.__Game_Citizens_Student_RO_ComponentLookup;
		jobData.m_Workers = __TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup;
		jobData.m_Households = __TypeHandle.__Game_Citizens_Household_RO_ComponentLookup;
		jobData.m_Resources = __TypeHandle.__Game_Economy_Resources_RO_BufferLookup;
		jobData.m_CitizenDatas = __TypeHandle.__Game_Citizens_Citizen_RW_ComponentLookup;
		jobData.m_Renters = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup;
		jobData.m_PrefabCarData = __TypeHandle.__Game_Prefabs_CarData_RO_ComponentLookup;
		jobData.m_ObjectGeometryData = __TypeHandle.__Game_Prefabs_ObjectGeometryData_RO_ComponentLookup;
		jobData.m_PrefabHumanData = __TypeHandle.__Game_Prefabs_HumanData_RO_ComponentLookup;
		jobData.m_Purposes = __TypeHandle.__Game_Citizens_TravelPurpose_RO_ComponentLookup;
		jobData.m_OutsideConnectionDatas = __TypeHandle.__Game_Prefabs_OutsideConnectionData_RO_ComponentLookup;
		jobData.m_TouristHouseholds = __TypeHandle.__Game_Citizens_TouristHousehold_RO_ComponentLookup;
		jobData.m_IndustrialProcesses = __TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;
		jobData.m_ServiceAvailables = __TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentLookup;
		jobData.m_PopulationData = __TypeHandle.__Game_City_Population_RO_ComponentLookup;
		jobData.m_HouseholdCitizens = __TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferLookup;
		jobData.m_RenterBufs = __TypeHandle.__Game_Buildings_Renter_RO_BufferLookup;
		jobData.m_ConsumptionDatas = __TypeHandle.__Game_Prefabs_ConsumptionData_RO_ComponentLookup;
		jobData.m_EconomyParameters = m_EconomyParameterQuery.GetSingleton<EconomyParameterData>();
		jobData.m_SimulationFrame = m_SimulationSystem.frameIndex;
		jobData.m_TimeOfDay = m_TimeSystem.normalizedTime;
		jobData.m_UpdateFrameIndex = updateFrameWithInterval;
		jobData.m_Weather = value;
		jobData.m_Temperature = m_ClimateSystem.temperature;
		jobData.m_RandomSeed = RandomSeed.Next();
		jobData.m_PathfindTypes = m_PathfindTypes;
		jobData.m_HumanChunks = m_ResidentPrefabQuery.ToArchetypeChunkListAsync(base.World.UpdateAllocator.ToAllocator, out var outJobHandle);
		jobData.m_PathfindQueue = m_PathFindSetupSystem.GetQueue(this, 64).AsParallelWriter();
		jobData.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		jobData.m_MeetingQueue = m_AddMeetingSystem.GetMeetingQueue(out var deps).AsParallelWriter();
		jobData.m_LeisureQueue = m_LeisureQueue.AsParallelWriter();
		jobData.m_TimeData = m_TimeDataQuery.GetSingleton<TimeData>();
		jobData.m_PopulationEntity = m_PopulationQuery.GetSingletonEntity();
		JobHandle jobHandle = JobChunkExtensions.ScheduleParallel(jobData, m_LeisureQuery, JobHandle.CombineDependencies(base.Dependency, JobHandle.CombineDependencies(outJobHandle, deps)));
		m_EndFrameBarrier.AddJobHandleForProducer(jobHandle);
		m_PathFindSetupSystem.AddQueueWriter(jobHandle);
		__TypeHandle.__Game_Companies_ServiceCompanyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_ServiceAvailable_RW_ComponentLookup.Update(ref base.CheckedStateRef);
		SpendLeisurejob jobData2 = default(SpendLeisurejob);
		jobData2.m_ServiceAvailables = __TypeHandle.__Game_Companies_ServiceAvailable_RW_ComponentLookup;
		jobData2.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		jobData2.m_HouseholdMembers = __TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentLookup;
		jobData2.m_IndustrialProcesses = __TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;
		jobData2.m_Prefabs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
		jobData2.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		jobData2.m_ServiceCompanyDatas = __TypeHandle.__Game_Companies_ServiceCompanyData_RO_ComponentLookup;
		jobData2.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
		jobData2.m_LeisureQueue = m_LeisureQueue;
		JobHandle jobHandle2 = IJobExtensions.Schedule(jobData2, jobHandle);
		m_ResourceSystem.AddPrefabsReader(jobHandle2);
		base.Dependency = jobHandle2;
	}

	public static void AddToTempList(NativeList<LeisureProviderData> tempProviderList, LeisureProviderData providerToAdd)
	{
		for (int i = 0; i < tempProviderList.Length; i++)
		{
			LeisureProviderData value = tempProviderList[i];
			if (value.m_LeisureType == providerToAdd.m_LeisureType && value.m_Resources == providerToAdd.m_Resources)
			{
				value.m_Efficiency += providerToAdd.m_Efficiency;
				tempProviderList[i] = value;
				return;
			}
		}
		tempProviderList.Add(in providerToAdd);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void __AssignQueries(ref SystemState state)
	{
	}

	protected override void OnCreateForCompiler()
	{
		base.OnCreateForCompiler();
		__AssignQueries(ref base.CheckedStateRef);
		__TypeHandle.__AssignHandles(ref base.CheckedStateRef);
	}

	[Preserve]
	public ModifiedLeisureSystem()
	{
	}
}
