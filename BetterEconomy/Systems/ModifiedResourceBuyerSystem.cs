using Game;
using System.Runtime.CompilerServices;
using Game.Agents;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Scripting;
using Game.Simulation;
using System;
using Game.Citizens;
using Game.Areas;
using Game.City;
using Game.Debug;
using Unity.Mathematics;
using UnityEngine;
using Game.Pathfind;
using Game.Objects;
using Game.Routes;
using Game.Net;

namespace BetterEconomy.Systems;

public partial class ModifiedResourceBuyerSystem : GameSystemBase
{
	[Flags]
	private enum SaleFlags : byte
	{
		None = 0,
		CommercialSeller = 1,
		ImportFromOC = 2,
		Virtual = 4
	}

	private struct SalesEvent
	{
		public SaleFlags m_Flags;

		public Entity m_Buyer;

		public Entity m_Seller;

		public Resource m_Resource;

		public int m_Amount;

		public float m_Distance;
	}

	[BurstCompile]
	private struct BuyJob : IJob
	{
		public NativeQueue<SalesEvent> m_SalesQueue;

		public EconomyParameterData m_EconomyParameters;

		public BufferLookup<Game.Economy.Resources> m_Resources;

		public ComponentLookup<ServiceAvailable> m_Services;

		[NativeDisableParallelForRestriction]
		public ComponentLookup<Household> m_Households;

		[NativeDisableParallelForRestriction]
		public ComponentLookup<BuyingCompany> m_BuyingCompanies;

		[ReadOnly]
		public ComponentLookup<Game.Objects.Transform> m_TransformDatas;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> m_PropertyRenters;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_Prefabs;

		[ReadOnly]
		public ComponentLookup<ServiceCompanyData> m_ServiceCompanies;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> m_OwnedVehicles;

		[ReadOnly]
		public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;

		[ReadOnly]
		public BufferLookup<HouseholdAnimal> m_HouseholdAnimals;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> m_Storages;

		public BufferLookup<TradeCost> m_TradeCosts;

		[ReadOnly]
		public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		[ReadOnly]
		public PersonalCarSelectData m_PersonalCarSelectData;

		[ReadOnly]
		public NativeArray<int> m_TaxRates;

		[ReadOnly]
		public ComponentLookup<CurrentDistrict> m_Districts;

		[ReadOnly]
		public BufferLookup<DistrictModifier> m_DistrictModifiers;

		[ReadOnly]
		public ComponentLookup<Population> m_PopulationData;

		public Entity m_PopulationEntity;

		public RandomSeed m_RandomSeed;

		public EntityCommandBuffer m_CommandBuffer;

		public void Execute()
		{
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(0);
			_ = m_PopulationData[m_PopulationEntity];
			SalesEvent item;
			while (m_SalesQueue.TryDequeue(out item))
			{
				if (!m_Resources.HasBuffer(item.m_Buyer))
				{
					continue;
				}
				bool flag = (item.m_Flags & SaleFlags.CommercialSeller) != 0;
				float num = (flag ? EconomyUtils.GetMarketPrice(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) : EconomyUtils.GetIndustrialPrice(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)) * (float)item.m_Amount;
				if (m_TradeCosts.HasBuffer(item.m_Seller))
				{
					DynamicBuffer<TradeCost> costs = m_TradeCosts[item.m_Seller];
					TradeCost tradeCost = EconomyUtils.GetTradeCost(item.m_Resource, costs);
					num += (float)item.m_Amount * tradeCost.m_BuyCost;
					float weight = EconomyUtils.GetWeight(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas);
					Assert.IsTrue(item.m_Amount != -1);
					float num2 = (float)EconomyUtils.GetTransportCost(item.m_Distance, item.m_Resource, item.m_Amount, weight) / (1f + (float)item.m_Amount);
					TradeCost tradeCost2 = default(TradeCost);
					if (m_TradeCosts.HasBuffer(item.m_Buyer))
					{
						tradeCost2 = EconomyUtils.GetTradeCost(item.m_Resource, m_TradeCosts[item.m_Buyer]);
					}
					if (!m_OutsideConnections.HasComponent(item.m_Seller) && (item.m_Flags & SaleFlags.CommercialSeller) != 0)
					{
						tradeCost.m_SellCost = math.lerp(tradeCost.m_SellCost, num2 + tradeCost2.m_SellCost, 0.5f);
						EconomyUtils.SetTradeCost(item.m_Resource, tradeCost, costs, keepLastTime: true);
					}
					if (m_TradeCosts.HasBuffer(item.m_Buyer) && !m_OutsideConnections.HasComponent(item.m_Buyer))
					{
						tradeCost2.m_BuyCost = math.lerp(tradeCost2.m_BuyCost, num2 + tradeCost.m_BuyCost, 0.5f);
						EconomyUtils.SetTradeCost(item.m_Resource, tradeCost, m_TradeCosts[item.m_Buyer], keepLastTime: true);
					}
				}
				if (m_Resources.HasBuffer(item.m_Seller) && EconomyUtils.GetResources(item.m_Resource, m_Resources[item.m_Seller]) <= 0)
				{
					continue;
				}
				TaxSystem.GetIndustrialTaxRate(item.m_Resource, m_TaxRates);
				if (flag && m_Services.HasComponent(item.m_Seller) && m_PropertyRenters.HasComponent(item.m_Seller))
				{
					Entity prefab = m_Prefabs[item.m_Seller].m_Prefab;
					ServiceAvailable value = m_Services[item.m_Seller];
					ServiceCompanyData serviceCompanyData = m_ServiceCompanies[prefab];
					num *= EconomyUtils.GetServicePriceMultiplier(value.m_ServiceAvailable, serviceCompanyData.m_MaxService);
					value.m_ServiceAvailable = math.max(0, Mathf.RoundToInt(value.m_ServiceAvailable - item.m_Amount));
					if (value.m_MeanPriority > 0f)
					{
						value.m_MeanPriority = math.min(1f, math.lerp(value.m_MeanPriority, (float)value.m_ServiceAvailable / (float)serviceCompanyData.m_MaxService, 0.1f));
					}
					else
					{
						value.m_MeanPriority = math.min(1f, (float)value.m_ServiceAvailable / (float)serviceCompanyData.m_MaxService);
					}
					m_Services[item.m_Seller] = value;
					Entity property = m_PropertyRenters[item.m_Seller].m_Property;
					if (m_Districts.HasComponent(property))
					{
						Entity district = m_Districts[property].m_District;
						TaxSystem.GetModifiedCommercialTaxRate(item.m_Resource, m_TaxRates, district, m_DistrictModifiers);
					}
					else
					{
						TaxSystem.GetCommercialTaxRate(item.m_Resource, m_TaxRates);
					}
				}
				if (m_Resources.HasBuffer(item.m_Seller))
				{
					DynamicBuffer<Game.Economy.Resources> resources = m_Resources[item.m_Seller];
					int resources2 = EconomyUtils.GetResources(item.m_Resource, resources);
					EconomyUtils.AddResources(item.m_Resource, -math.min(resources2, Mathf.RoundToInt(item.m_Amount)), resources);
				}
				var amout = -Mathf.RoundToInt(num);
				EconomyUtils.AddResources(Resource.Money, amout, m_Resources[item.m_Buyer]);
				// BetterEconomy.log.Info($"[negative] buyer {item.m_Buyer.Index} for {amout} price, amount {item.m_Amount}");
				if (m_Households.HasComponent(item.m_Buyer))
				{
					Household value2 = m_Households[item.m_Buyer];
					value2.m_Resources = (int)math.clamp((long)((float)value2.m_Resources + num), -2147483648L, 2147483647L);
					m_Households[item.m_Buyer] = value2;
				}
				else if (m_BuyingCompanies.HasComponent(item.m_Buyer))
				{
					BuyingCompany value3 = m_BuyingCompanies[item.m_Buyer];
					value3.m_LastTradePartner = item.m_Seller;
					m_BuyingCompanies[item.m_Buyer] = value3;
					if ((item.m_Flags & SaleFlags.Virtual) != 0)
					{
						EconomyUtils.AddResources(item.m_Resource, item.m_Amount, m_Resources[item.m_Buyer]);
					}
				}
				if (!m_Storages.HasComponent(item.m_Seller) && m_PropertyRenters.HasComponent(item.m_Seller))
				{
					DynamicBuffer<Game.Economy.Resources> resources3 = m_Resources[item.m_Seller];
                 //   BetterEconomy.log.Info($"[positive] sller {item.m_Seller.Index} for {num} price, amount {item.m_Amount}");
					EconomyUtils.AddResources(Resource.Money, Mathf.RoundToInt(num), resources3);
				}
				if (item.m_Resource != Resource.Vehicles || item.m_Amount != HouseholdBehaviorSystem.kCarAmount || !m_PropertyRenters.HasComponent(item.m_Seller))
				{
					continue;
				}
				Entity property2 = m_PropertyRenters[item.m_Seller].m_Property;
				if (!m_TransformDatas.HasComponent(property2) || !m_HouseholdCitizens.HasBuffer(item.m_Buyer))
				{
					continue;
				}
				Entity buyer = item.m_Buyer;
				Game.Objects.Transform transform = m_TransformDatas[property2];
				int length = m_HouseholdCitizens[buyer].Length;
				int num3 = (m_HouseholdAnimals.HasBuffer(buyer) ? m_HouseholdAnimals[buyer].Length : 0);
				int passengerAmount;
				int num4;
				if (m_OwnedVehicles.HasBuffer(buyer) && m_OwnedVehicles[buyer].Length >= 1)
				{
					passengerAmount = random.NextInt(1, 1 + length);
					num4 = random.NextInt(1, 2 + num3);
				}
				else
				{
					passengerAmount = length;
					num4 = 1 + num3;
				}
				if (random.NextInt(20) == 0)
				{
					num4 += 5;
				}
				Entity entity = m_PersonalCarSelectData.CreateVehicle(m_CommandBuffer, ref random, passengerAmount, num4, avoidTrailers: true, noSlowVehicles: false, transform, property2, Entity.Null, (PersonalCarFlags)0u, stopped: true);
				if (entity != Entity.Null)
				{
					m_CommandBuffer.AddComponent(entity, new Owner(buyer));
					if (!m_OwnedVehicles.HasBuffer(buyer))
					{
						m_CommandBuffer.AddBuffer<OwnedVehicle>(buyer);
					}
				}
			}
		}
	}

	[BurstCompile]
	private struct HandleBuyersJob : IJobChunk
	{
		[ReadOnly]
		public ComponentTypeHandle<ResourceBuyer> m_BuyerType;

		[ReadOnly]
		public ComponentTypeHandle<ResourceBought> m_BoughtType;

		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		public BufferTypeHandle<TripNeeded> m_TripType;

		[ReadOnly]
		public ComponentTypeHandle<Citizen> m_CitizenType;

		[ReadOnly]
		public ComponentTypeHandle<CreatureData> m_CreatureDataType;

		[ReadOnly]
		public ComponentTypeHandle<ResidentData> m_ResidentDataType;

		[ReadOnly]
		public ComponentTypeHandle<AttendingMeeting> m_AttendingMeetingType;

		[ReadOnly]
		public ComponentLookup<PathInformation> m_PathInformation;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> m_Properties;

		[ReadOnly]
		public ComponentLookup<ServiceAvailable> m_ServiceAvailables;

		[ReadOnly]
		public ComponentLookup<CarKeeper> m_CarKeepers;

		[ReadOnly]
		public ComponentLookup<ParkedCar> m_ParkedCarData;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.PersonalCar> m_PersonalCarData;

		[ReadOnly]
		public ComponentLookup<Target> m_Targets;

		[ReadOnly]
		public ComponentLookup<CurrentBuilding> m_CurrentBuildings;

		[ReadOnly]
		public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;

		[ReadOnly]
		public ComponentLookup<HouseholdMember> m_HouseholdMembers;

		[ReadOnly]
		public ComponentLookup<Household> m_Households;

		[ReadOnly]
		public ComponentLookup<TouristHousehold> m_TouristHouseholds;

		[ReadOnly]
		public ComponentLookup<CommuterHousehold> m_CommuterHouseholds;

		[ReadOnly]
		public ComponentLookup<Worker> m_Workers;

		[ReadOnly]
		public ComponentLookup<ServiceCompanyData> m_ServiceCompanyDatas;

		[ReadOnly]
		public BufferLookup<Game.Economy.Resources> m_Resources;

		[ReadOnly]
		public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;

		[NativeDisableParallelForRestriction]
		public ComponentLookup<CoordinatedMeeting> m_CoordinatedMeetings;

		[ReadOnly]
		public BufferLookup<HaveCoordinatedMeetingData> m_HaveCoordinatedMeetingDatas;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_PrefabRefData;

		[ReadOnly]
		public ComponentLookup<CarData> m_PrefabCarData;

		[ReadOnly]
		public ComponentLookup<ObjectGeometryData> m_ObjectGeometryData;

		[ReadOnly]
		public ComponentLookup<HumanData> m_PrefabHumanData;

		[ReadOnly]
		public ComponentLookup<Population> m_Populations;

		[ReadOnly]
		public float m_TimeOfDay;

		[ReadOnly]
		public RandomSeed m_RandomSeed;

		[ReadOnly]
		public ComponentTypeSet m_PathfindTypes;

		[ReadOnly]
		public NativeList<ArchetypeChunk> m_HumanChunks;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public NativeQueue<SetupQueueItem>.ParallelWriter m_PathfindQueue;

		public EconomyParameterData m_EconomyParameterData;

		public Entity m_City;

		public NativeQueue<SalesEvent>.ParallelWriter m_SalesQueue;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<ResourceBuyer> nativeArray2 = chunk.GetNativeArray(ref m_BuyerType);
			NativeArray<ResourceBought> nativeArray3 = chunk.GetNativeArray(ref m_BoughtType);
			BufferAccessor<TripNeeded> bufferAccessor = chunk.GetBufferAccessor(ref m_TripType);
			NativeArray<Citizen> nativeArray4 = chunk.GetNativeArray(ref m_CitizenType);
			NativeArray<AttendingMeeting> nativeArray5 = chunk.GetNativeArray(ref m_AttendingMeetingType);
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(unfilteredChunkIndex);
			for (int i = 0; i < nativeArray3.Length; i++)
			{
				Entity e = nativeArray[i];
				ResourceBought resourceBought = nativeArray3[i];
				if (m_PrefabRefData.HasComponent(resourceBought.m_Payer) && m_PrefabRefData.HasComponent(resourceBought.m_Seller))
				{
					SalesEvent salesEvent = default(SalesEvent);
					salesEvent.m_Amount = resourceBought.m_Amount;
					salesEvent.m_Buyer = resourceBought.m_Payer;
					salesEvent.m_Seller = resourceBought.m_Seller;
					salesEvent.m_Resource = resourceBought.m_Resource;
					salesEvent.m_Flags = SaleFlags.None;
					salesEvent.m_Distance = resourceBought.m_Distance;
					SalesEvent value = salesEvent;
					m_SalesQueue.Enqueue(value);
				}
				m_CommandBuffer.RemoveComponent<ResourceBought>(unfilteredChunkIndex, e);
			}
			for (int j = 0; j < nativeArray2.Length; j++)
			{
				ResourceBuyer resourceBuyer = nativeArray2[j];
				Entity entity = nativeArray[j];
				DynamicBuffer<TripNeeded> dynamicBuffer = bufferAccessor[j];
				bool flag = false;
				Entity entity2 = m_ResourcePrefabs[resourceBuyer.m_ResourceNeeded];
				if (m_ResourceDatas.HasComponent(entity2))
				{
					flag = EconomyUtils.GetWeight(resourceBuyer.m_ResourceNeeded, m_ResourcePrefabs, ref m_ResourceDatas) == 0f;
				}
				if (m_PathInformation.HasComponent(entity))
				{
					PathInformation pathInformation = m_PathInformation[entity];
					if ((pathInformation.m_State & PathFlags.Pending) != 0)
					{
						continue;
					}
					Entity destination = pathInformation.m_Destination;
					if (m_Properties.HasComponent(destination) || m_OutsideConnections.HasComponent(destination))
					{
						DynamicBuffer<Game.Economy.Resources> resources = m_Resources[destination];
						int resources2 = EconomyUtils.GetResources(resourceBuyer.m_ResourceNeeded, resources);
						if (flag || resourceBuyer.m_AmountNeeded < 2 * resources2)
						{
							resourceBuyer.m_AmountNeeded = math.min(resourceBuyer.m_AmountNeeded, resources2);
							SaleFlags saleFlags = (m_ServiceAvailables.HasComponent(destination) ? SaleFlags.CommercialSeller : SaleFlags.None);
							if (flag)
							{
								saleFlags |= SaleFlags.Virtual;
							}
							if (m_OutsideConnections.HasComponent(destination))
							{
								saleFlags |= SaleFlags.ImportFromOC;
							}
							SalesEvent salesEvent = default(SalesEvent);
							salesEvent.m_Amount = resourceBuyer.m_AmountNeeded;
							salesEvent.m_Buyer = resourceBuyer.m_Payer;
							salesEvent.m_Seller = destination;
							salesEvent.m_Resource = resourceBuyer.m_ResourceNeeded;
							salesEvent.m_Flags = saleFlags;
							salesEvent.m_Distance = pathInformation.m_Distance;
							SalesEvent value2 = salesEvent;
							m_SalesQueue.Enqueue(value2);
							m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
							m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
							int population = m_Populations[m_City].m_Population;
							bool flag2 = random.NextInt(100) < 100 - Mathf.RoundToInt(100f / math.max(1f, math.sqrt(m_EconomyParameterData.m_TrafficReduction * (float)population * 0.1f)));
							if (!flag && !flag2)
							{
								TripNeeded elem = default(TripNeeded);
								elem.m_TargetAgent = destination;
								elem.m_Purpose = Purpose.Shopping;
								elem.m_Data = resourceBuyer.m_AmountNeeded;
								elem.m_Resource = resourceBuyer.m_ResourceNeeded;
								dynamicBuffer.Add(elem);
								if (!m_Targets.HasComponent(nativeArray[j]))
								{
									m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, new Target
									{
										m_Target = destination
									});
								}
							}
						}
						else
						{
							m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
							m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
						}
						continue;
					}
					m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
					m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
					if (nativeArray5.IsCreated)
					{
						AttendingMeeting attendingMeeting = nativeArray5[j];
						Entity prefab = m_PrefabRefData[attendingMeeting.m_Meeting].m_Prefab;
						CoordinatedMeeting value3 = m_CoordinatedMeetings[attendingMeeting.m_Meeting];
						if (m_HaveCoordinatedMeetingDatas[prefab][value3.m_Phase].m_TravelPurpose.m_Purpose == Purpose.Shopping)
						{
							value3.m_Status = MeetingStatus.Done;
							m_CoordinatedMeetings[attendingMeeting.m_Meeting] = value3;
						}
					}
				}
				else if ((!m_HouseholdMembers.HasComponent(entity) || (!m_TouristHouseholds.HasComponent(m_HouseholdMembers[entity].m_Household) && !m_CommuterHouseholds.HasComponent(m_HouseholdMembers[entity].m_Household))) && m_CurrentBuildings.HasComponent(entity) && m_OutsideConnections.HasComponent(m_CurrentBuildings[entity].m_CurrentBuilding) && !nativeArray5.IsCreated)
				{
					SaleFlags flags = SaleFlags.ImportFromOC;
					SalesEvent salesEvent = default(SalesEvent);
					salesEvent.m_Amount = resourceBuyer.m_AmountNeeded;
					salesEvent.m_Buyer = resourceBuyer.m_Payer;
					salesEvent.m_Seller = m_CurrentBuildings[entity].m_CurrentBuilding;
					salesEvent.m_Resource = resourceBuyer.m_ResourceNeeded;
					salesEvent.m_Flags = flags;
					salesEvent.m_Distance = 0f;
					SalesEvent value4 = salesEvent;
					m_SalesQueue.Enqueue(value4);
					m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
				}
				else
				{
					Citizen citizen = default(Citizen);
					if (nativeArray4.Length > 0)
					{
						citizen = nativeArray4[j];
						Entity household = m_HouseholdMembers[entity].m_Household;
						Household householdData = m_Households[household];
						DynamicBuffer<HouseholdCitizen> dynamicBuffer2 = m_HouseholdCitizens[household];
						FindShopForCitizen(chunk, unfilteredChunkIndex, entity, resourceBuyer.m_ResourceNeeded, resourceBuyer.m_AmountNeeded, resourceBuyer.m_Flags, citizen, householdData, dynamicBuffer2.Length, flag);
					}
					else
					{
						FindShopForCompany(chunk, unfilteredChunkIndex, entity, resourceBuyer.m_ResourceNeeded, resourceBuyer.m_AmountNeeded, resourceBuyer.m_Flags, flag);
					}
				}
			}
		}

		private void FindShopForCitizen(ArchetypeChunk chunk, int index, Entity buyer, Resource resource, int amount, SetupTargetFlags flags, Citizen citizenData, Household householdData, int householdCitizenCount, bool virtualGood)
		{
			m_CommandBuffer.AddComponent(index, buyer, in m_PathfindTypes);
			m_CommandBuffer.SetComponent(index, buyer, new PathInformation
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
			PathfindParameters pathfindParameters = default(PathfindParameters);
			pathfindParameters.m_MaxSpeed = 277.77777f;
			pathfindParameters.m_WalkSpeed = humanData.m_WalkSpeed;
			pathfindParameters.m_Weights = CitizenUtils.GetPathfindWeights(citizenData, householdData, householdCitizenCount);
			pathfindParameters.m_Methods = PathMethod.Pedestrian | PathMethod.Taxi | RouteUtils.GetPublicTransportMethods(m_TimeOfDay);
			pathfindParameters.m_SecondaryIgnoredRules = VehicleUtils.GetIgnoredPathfindRulesTaxiDefaults();
			pathfindParameters.m_MaxCost = CitizenBehaviorSystem.kMaxPathfindCost;
			PathfindParameters parameters = pathfindParameters;
			SetupQueueTarget setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.CurrentLocation;
			setupQueueTarget.m_Methods = PathMethod.Pedestrian;
			setupQueueTarget.m_RandomCost = 30f;
			SetupQueueTarget origin = setupQueueTarget;
			setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.ResourceSeller;
			setupQueueTarget.m_Methods = PathMethod.Pedestrian;
			setupQueueTarget.m_Resource = resource;
			setupQueueTarget.m_Value = amount;
			setupQueueTarget.m_Flags = flags;
			setupQueueTarget.m_RandomCost = 30f;
			setupQueueTarget.m_ActivityMask = creatureData.m_SupportedActivities;
			SetupQueueTarget destination = setupQueueTarget;
			if (virtualGood)
			{
				parameters.m_PathfindFlags |= PathfindFlags.SkipPathfind;
			}
			if (m_HouseholdMembers.HasComponent(buyer))
			{
				Entity household = m_HouseholdMembers[buyer].m_Household;
				if (m_Properties.HasComponent(household))
				{
					parameters.m_Authorization1 = m_Properties[household].m_Property;
				}
			}
			if (m_Workers.HasComponent(buyer))
			{
				Worker worker = m_Workers[buyer];
				if (m_Properties.HasComponent(worker.m_Workplace))
				{
					parameters.m_Authorization2 = m_Properties[worker.m_Workplace].m_Property;
				}
				else
				{
					parameters.m_Authorization2 = worker.m_Workplace;
				}
			}
			if (m_CarKeepers.IsComponentEnabled(buyer))
			{
				Entity car = m_CarKeepers[buyer].m_Car;
				if (m_ParkedCarData.HasComponent(car))
				{
					PrefabRef prefabRef = m_PrefabRefData[car];
					ParkedCar parkedCar = m_ParkedCarData[car];
					CarData carData = m_PrefabCarData[prefabRef.m_Prefab];
					parameters.m_MaxSpeed.x = carData.m_MaxSpeed;
					parameters.m_ParkingTarget = parkedCar.m_Lane;
					parameters.m_ParkingDelta = parkedCar.m_CurvePosition;
					parameters.m_ParkingSize = VehicleUtils.GetParkingSize(car, ref m_PrefabRefData, ref m_ObjectGeometryData);
					parameters.m_Methods |= PathMethod.Road | PathMethod.Parking;
					parameters.m_IgnoredRules = VehicleUtils.GetIgnoredPathfindRules(carData);
					if (m_PersonalCarData.TryGetComponent(car, out var componentData) && (componentData.m_State & PersonalCarFlags.HomeTarget) == 0)
					{
						parameters.m_PathfindFlags |= PathfindFlags.ParkingReset;
					}
				}
			}
			SetupQueueItem value = new SetupQueueItem(buyer, parameters, origin, destination);
			m_PathfindQueue.Enqueue(value);
		}

		private void FindShopForCompany(ArchetypeChunk chunk, int index, Entity buyer, Resource resource, int amount, SetupTargetFlags flags, bool virtualGood)
		{
			m_CommandBuffer.AddComponent(index, buyer, in m_PathfindTypes);
			m_CommandBuffer.SetComponent(index, buyer, new PathInformation
			{
				m_State = PathFlags.Pending
			});
			float transportCost = EconomyUtils.GetTransportCost(100f, amount, m_ResourceDatas[m_ResourcePrefabs[resource]].m_Weight, StorageTransferFlags.Car);
			PathfindParameters pathfindParameters = default(PathfindParameters);
			pathfindParameters.m_MaxSpeed = 111.111115f;
			pathfindParameters.m_WalkSpeed = 5.555556f;
			pathfindParameters.m_Weights = new PathfindWeights(1f, 1f, transportCost, 1f);
			pathfindParameters.m_Methods = PathMethod.Road | PathMethod.CargoLoading;
			pathfindParameters.m_IgnoredRules = RuleFlags.ForbidSlowTraffic;
			PathfindParameters parameters = pathfindParameters;
			SetupQueueTarget setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.CurrentLocation;
			setupQueueTarget.m_Methods = PathMethod.Road | PathMethod.CargoLoading;
			setupQueueTarget.m_RoadTypes = RoadTypes.Car;
			SetupQueueTarget origin = setupQueueTarget;
			setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.ResourceSeller;
			setupQueueTarget.m_Methods = PathMethod.Road | PathMethod.CargoLoading;
			setupQueueTarget.m_RoadTypes = RoadTypes.Car;
			setupQueueTarget.m_Resource = resource;
			setupQueueTarget.m_Value = amount;
			setupQueueTarget.m_Flags = flags;
			SetupQueueTarget destination = setupQueueTarget;
			if (virtualGood)
			{
				parameters.m_PathfindFlags |= PathfindFlags.SkipPathfind;
			}
			SetupQueueItem value = new SetupQueueItem(buyer, parameters, origin, destination);
			m_PathfindQueue.Enqueue(value);
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

		[ReadOnly]
		public ComponentTypeHandle<ResourceBuyer> __Game_Companies_ResourceBuyer_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ResourceBought> __Game_Citizens_ResourceBought_RO_ComponentTypeHandle;

		public BufferTypeHandle<TripNeeded> __Game_Citizens_TripNeeded_RW_BufferTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<Citizen> __Game_Citizens_Citizen_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<CreatureData> __Game_Prefabs_CreatureData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ResidentData> __Game_Prefabs_ResidentData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<AttendingMeeting> __Game_Citizens_AttendingMeeting_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<ServiceAvailable> __Game_Companies_ServiceAvailable_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PathInformation> __Game_Pathfind_PathInformation_RO_ComponentLookup;

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
		public ComponentLookup<CurrentBuilding> __Game_Citizens_CurrentBuilding_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Objects.OutsideConnection> __Game_Objects_OutsideConnection_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<HouseholdMember> __Game_Citizens_HouseholdMember_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Household> __Game_Citizens_Household_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<TouristHousehold> __Game_Citizens_TouristHousehold_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CommuterHousehold> __Game_Citizens_CommuterHousehold_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ServiceCompanyData> __Game_Companies_ServiceCompanyData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Worker> __Game_Citizens_Worker_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<HouseholdCitizen> __Game_Citizens_HouseholdCitizen_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CarData> __Game_Prefabs_CarData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ObjectGeometryData> __Game_Prefabs_ObjectGeometryData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<HumanData> __Game_Prefabs_HumanData_RO_ComponentLookup;

		public ComponentLookup<CoordinatedMeeting> __Game_Citizens_CoordinatedMeeting_RW_ComponentLookup;

		[ReadOnly]
		public BufferLookup<HaveCoordinatedMeetingData> __Game_Prefabs_HaveCoordinatedMeetingData_RO_BufferLookup;

		public ComponentLookup<Population> __Game_City_Population_RW_ComponentLookup;

		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferLookup;

		public ComponentLookup<ServiceAvailable> __Game_Companies_ServiceAvailable_RW_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Objects.Transform> __Game_Objects_Transform_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> __Game_Vehicles_OwnedVehicle_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<HouseholdAnimal> __Game_Citizens_HouseholdAnimal_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> __Game_Companies_StorageCompany_RO_ComponentLookup;

		public ComponentLookup<Household> __Game_Citizens_Household_RW_ComponentLookup;

		public ComponentLookup<BuyingCompany> __Game_Companies_BuyingCompany_RW_ComponentLookup;

		public BufferLookup<TradeCost> __Game_Companies_TradeCost_RW_BufferLookup;

		[ReadOnly]
		public ComponentLookup<CurrentDistrict> __Game_Areas_CurrentDistrict_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<DistrictModifier> __Game_Areas_DistrictModifier_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<Population> __Game_City_Population_RO_ComponentLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Companies_ResourceBuyer_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ResourceBuyer>(isReadOnly: true);
			__Game_Citizens_ResourceBought_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ResourceBought>(isReadOnly: true);
			__Game_Citizens_TripNeeded_RW_BufferTypeHandle = state.GetBufferTypeHandle<TripNeeded>();
			__Game_Citizens_Citizen_RO_ComponentTypeHandle = state.GetComponentTypeHandle<Citizen>(isReadOnly: true);
			__Game_Prefabs_CreatureData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<CreatureData>(isReadOnly: true);
			__Game_Prefabs_ResidentData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ResidentData>(isReadOnly: true);
			__Game_Citizens_AttendingMeeting_RO_ComponentTypeHandle = state.GetComponentTypeHandle<AttendingMeeting>(isReadOnly: true);
			__Game_Companies_ServiceAvailable_RO_ComponentLookup = state.GetComponentLookup<ServiceAvailable>(isReadOnly: true);
			__Game_Pathfind_PathInformation_RO_ComponentLookup = state.GetComponentLookup<PathInformation>(isReadOnly: true);
			__Game_Buildings_PropertyRenter_RO_ComponentLookup = state.GetComponentLookup<PropertyRenter>(isReadOnly: true);
			__Game_Citizens_CarKeeper_RO_ComponentLookup = state.GetComponentLookup<CarKeeper>(isReadOnly: true);
			__Game_Vehicles_ParkedCar_RO_ComponentLookup = state.GetComponentLookup<ParkedCar>(isReadOnly: true);
			__Game_Vehicles_PersonalCar_RO_ComponentLookup = state.GetComponentLookup<Game.Vehicles.PersonalCar>(isReadOnly: true);
			__Game_Common_Target_RO_ComponentLookup = state.GetComponentLookup<Target>(isReadOnly: true);
			__Game_Citizens_CurrentBuilding_RO_ComponentLookup = state.GetComponentLookup<CurrentBuilding>(isReadOnly: true);
			__Game_Objects_OutsideConnection_RO_ComponentLookup = state.GetComponentLookup<Game.Objects.OutsideConnection>(isReadOnly: true);
			__Game_Citizens_HouseholdMember_RO_ComponentLookup = state.GetComponentLookup<HouseholdMember>(isReadOnly: true);
			__Game_Citizens_Household_RO_ComponentLookup = state.GetComponentLookup<Household>(isReadOnly: true);
			__Game_Citizens_TouristHousehold_RO_ComponentLookup = state.GetComponentLookup<TouristHousehold>(isReadOnly: true);
			__Game_Citizens_CommuterHousehold_RO_ComponentLookup = state.GetComponentLookup<CommuterHousehold>(isReadOnly: true);
			__Game_Companies_ServiceCompanyData_RO_ComponentLookup = state.GetComponentLookup<ServiceCompanyData>(isReadOnly: true);
			__Game_Citizens_Worker_RO_ComponentLookup = state.GetComponentLookup<Worker>(isReadOnly: true);
			__Game_Economy_Resources_RO_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>(isReadOnly: true);
			__Game_Citizens_HouseholdCitizen_RO_BufferLookup = state.GetBufferLookup<HouseholdCitizen>(isReadOnly: true);
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Game_Prefabs_PrefabRef_RO_ComponentLookup = state.GetComponentLookup<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_CarData_RO_ComponentLookup = state.GetComponentLookup<CarData>(isReadOnly: true);
			__Game_Prefabs_ObjectGeometryData_RO_ComponentLookup = state.GetComponentLookup<ObjectGeometryData>(isReadOnly: true);
			__Game_Prefabs_HumanData_RO_ComponentLookup = state.GetComponentLookup<HumanData>(isReadOnly: true);
			__Game_Citizens_CoordinatedMeeting_RW_ComponentLookup = state.GetComponentLookup<CoordinatedMeeting>();
			__Game_Prefabs_HaveCoordinatedMeetingData_RO_BufferLookup = state.GetBufferLookup<HaveCoordinatedMeetingData>(isReadOnly: true);
			__Game_City_Population_RW_ComponentLookup = state.GetComponentLookup<Population>();
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>();
			__Game_Companies_ServiceAvailable_RW_ComponentLookup = state.GetComponentLookup<ServiceAvailable>();
			__Game_Objects_Transform_RO_ComponentLookup = state.GetComponentLookup<Game.Objects.Transform>(isReadOnly: true);
			__Game_Vehicles_OwnedVehicle_RO_BufferLookup = state.GetBufferLookup<OwnedVehicle>(isReadOnly: true);
			__Game_Citizens_HouseholdAnimal_RO_BufferLookup = state.GetBufferLookup<HouseholdAnimal>(isReadOnly: true);
			__Game_Companies_StorageCompany_RO_ComponentLookup = state.GetComponentLookup<Game.Companies.StorageCompany>(isReadOnly: true);
			__Game_Citizens_Household_RW_ComponentLookup = state.GetComponentLookup<Household>();
			__Game_Companies_BuyingCompany_RW_ComponentLookup = state.GetComponentLookup<BuyingCompany>();
			__Game_Companies_TradeCost_RW_BufferLookup = state.GetBufferLookup<TradeCost>();
			__Game_Areas_CurrentDistrict_RO_ComponentLookup = state.GetComponentLookup<CurrentDistrict>(isReadOnly: true);
			__Game_Areas_DistrictModifier_RO_BufferLookup = state.GetBufferLookup<DistrictModifier>(isReadOnly: true);
			__Game_City_Population_RO_ComponentLookup = state.GetComponentLookup<Population>(isReadOnly: true);
		}
	}

	private const int UPDATE_INTERVAL = 16;

	private EntityQuery m_BuyerQuery;

	private EntityQuery m_CarPrefabQuery;

	private EntityQuery m_EconomyParameterQuery;

	private EntityQuery m_ResidentPrefabQuery;

	private EntityQuery m_PopulationQuery;

	private ComponentTypeSet m_PathfindTypes;

	private EndFrameBarrier m_EndFrameBarrier;

	private PathfindSetupSystem m_PathfindSetupSystem;

	private ResourceSystem m_ResourceSystem;

	private TaxSystem m_TaxSystem;

	private TimeSystem m_TimeSystem;

	private CityConfigurationSystem m_CityConfigurationSystem;

	private PersonalCarSelectData m_PersonalCarSelectData;

	private CitySystem m_CitySystem;

	private NativeQueue<SalesEvent> m_SalesQueue;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 16;
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_PathfindSetupSystem = base.World.GetOrCreateSystemManaged<PathfindSetupSystem>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_TaxSystem = base.World.GetOrCreateSystemManaged<TaxSystem>();
		m_TimeSystem = base.World.GetOrCreateSystemManaged<TimeSystem>();
		m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
		m_PersonalCarSelectData = new PersonalCarSelectData(this);
		m_CitySystem = base.World.GetOrCreateSystemManaged<CitySystem>();
		m_SalesQueue = new NativeQueue<SalesEvent>(Allocator.Persistent);
		m_BuyerQuery = GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[2]
			{
				ComponentType.ReadWrite<ResourceBuyer>(),
				ComponentType.ReadWrite<TripNeeded>()
			},
			None = new ComponentType[3]
			{
				ComponentType.ReadOnly<TravelPurpose>(),
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Temp>()
			}
		}, new EntityQueryDesc
		{
			All = new ComponentType[1] { ComponentType.ReadOnly<ResourceBought>() },
			None = new ComponentType[2]
			{
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Temp>()
			}
		});
		m_CarPrefabQuery = GetEntityQuery(PersonalCarSelectData.GetEntityQueryDesc());
		m_EconomyParameterQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
		m_PopulationQuery = GetEntityQuery(ComponentType.ReadOnly<Population>());
		m_ResidentPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<ObjectData>(), ComponentType.ReadOnly<HumanData>(), ComponentType.ReadOnly<ResidentData>(), ComponentType.ReadOnly<PrefabData>());
		m_PathfindTypes = new ComponentTypeSet(ComponentType.ReadWrite<PathInformation>(), ComponentType.ReadWrite<PathElement>());
		RequireForUpdate(m_BuyerQuery);
		RequireForUpdate(m_EconomyParameterQuery);
		RequireForUpdate(m_PopulationQuery);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		m_SalesQueue.Dispose();
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnStopRunning()
	{
		base.OnStopRunning();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		if (m_BuyerQuery.CalculateEntityCount() > 0)
		{
			m_PersonalCarSelectData.PreUpdate(this, m_CityConfigurationSystem, m_CarPrefabQuery, Allocator.TempJob, out var jobHandle);
			__TypeHandle.__Game_City_Population_RW_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_HaveCoordinatedMeetingData_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_CoordinatedMeeting_RW_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_HumanData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_ObjectGeometryData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_CarData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Economy_Resources_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_ServiceCompanyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_CommuterHousehold_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_TouristHousehold_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_Household_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Objects_OutsideConnection_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_CurrentBuilding_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Common_Target_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Vehicles_PersonalCar_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Vehicles_ParkedCar_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_CarKeeper_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Pathfind_PathInformation_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_AttendingMeeting_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_ResidentData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_CreatureData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_Citizen_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_TripNeeded_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_ResourceBought_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_ResourceBuyer_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
			HandleBuyersJob handleBuyersJob = default(HandleBuyersJob);
			handleBuyersJob.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
			handleBuyersJob.m_BuyerType = __TypeHandle.__Game_Companies_ResourceBuyer_RO_ComponentTypeHandle;
			handleBuyersJob.m_BoughtType = __TypeHandle.__Game_Citizens_ResourceBought_RO_ComponentTypeHandle;
			handleBuyersJob.m_TripType = __TypeHandle.__Game_Citizens_TripNeeded_RW_BufferTypeHandle;
			handleBuyersJob.m_CitizenType = __TypeHandle.__Game_Citizens_Citizen_RO_ComponentTypeHandle;
			handleBuyersJob.m_CreatureDataType = __TypeHandle.__Game_Prefabs_CreatureData_RO_ComponentTypeHandle;
			handleBuyersJob.m_ResidentDataType = __TypeHandle.__Game_Prefabs_ResidentData_RO_ComponentTypeHandle;
			handleBuyersJob.m_AttendingMeetingType = __TypeHandle.__Game_Citizens_AttendingMeeting_RO_ComponentTypeHandle;
			handleBuyersJob.m_ServiceAvailables = __TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentLookup;
			handleBuyersJob.m_PathInformation = __TypeHandle.__Game_Pathfind_PathInformation_RO_ComponentLookup;
			handleBuyersJob.m_Properties = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup;
			handleBuyersJob.m_CarKeepers = __TypeHandle.__Game_Citizens_CarKeeper_RO_ComponentLookup;
			handleBuyersJob.m_ParkedCarData = __TypeHandle.__Game_Vehicles_ParkedCar_RO_ComponentLookup;
			handleBuyersJob.m_PersonalCarData = __TypeHandle.__Game_Vehicles_PersonalCar_RO_ComponentLookup;
			handleBuyersJob.m_Targets = __TypeHandle.__Game_Common_Target_RO_ComponentLookup;
			handleBuyersJob.m_CurrentBuildings = __TypeHandle.__Game_Citizens_CurrentBuilding_RO_ComponentLookup;
			handleBuyersJob.m_OutsideConnections = __TypeHandle.__Game_Objects_OutsideConnection_RO_ComponentLookup;
			handleBuyersJob.m_HouseholdMembers = __TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentLookup;
			handleBuyersJob.m_Households = __TypeHandle.__Game_Citizens_Household_RO_ComponentLookup;
			handleBuyersJob.m_TouristHouseholds = __TypeHandle.__Game_Citizens_TouristHousehold_RO_ComponentLookup;
			handleBuyersJob.m_CommuterHouseholds = __TypeHandle.__Game_Citizens_CommuterHousehold_RO_ComponentLookup;
			handleBuyersJob.m_ServiceCompanyDatas = __TypeHandle.__Game_Companies_ServiceCompanyData_RO_ComponentLookup;
			handleBuyersJob.m_Workers = __TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup;
			handleBuyersJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RO_BufferLookup;
			handleBuyersJob.m_HouseholdCitizens = __TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferLookup;
			handleBuyersJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
			handleBuyersJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
			handleBuyersJob.m_PrefabRefData = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
			handleBuyersJob.m_PrefabCarData = __TypeHandle.__Game_Prefabs_CarData_RO_ComponentLookup;
			handleBuyersJob.m_ObjectGeometryData = __TypeHandle.__Game_Prefabs_ObjectGeometryData_RO_ComponentLookup;
			handleBuyersJob.m_PrefabHumanData = __TypeHandle.__Game_Prefabs_HumanData_RO_ComponentLookup;
			handleBuyersJob.m_CoordinatedMeetings = __TypeHandle.__Game_Citizens_CoordinatedMeeting_RW_ComponentLookup;
			handleBuyersJob.m_HaveCoordinatedMeetingDatas = __TypeHandle.__Game_Prefabs_HaveCoordinatedMeetingData_RO_BufferLookup;
			handleBuyersJob.m_Populations = __TypeHandle.__Game_City_Population_RW_ComponentLookup;
			handleBuyersJob.m_TimeOfDay = m_TimeSystem.normalizedTime;
			handleBuyersJob.m_RandomSeed = RandomSeed.Next();
			handleBuyersJob.m_PathfindTypes = m_PathfindTypes;
			handleBuyersJob.m_HumanChunks = m_ResidentPrefabQuery.ToArchetypeChunkListAsync(base.World.UpdateAllocator.ToAllocator, out var outJobHandle);
			handleBuyersJob.m_PathfindQueue = m_PathfindSetupSystem.GetQueue(this, 80, 16).AsParallelWriter();
			handleBuyersJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
			handleBuyersJob.m_EconomyParameterData = m_EconomyParameterQuery.GetSingleton<EconomyParameterData>();
			handleBuyersJob.m_City = m_CitySystem.City;
			handleBuyersJob.m_SalesQueue = m_SalesQueue.AsParallelWriter();
			HandleBuyersJob jobData = handleBuyersJob;
			base.Dependency = JobChunkExtensions.ScheduleParallel(jobData, m_BuyerQuery, JobHandle.CombineDependencies(base.Dependency, outJobHandle));
			m_ResourceSystem.AddPrefabsReader(base.Dependency);
			m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
			m_PathfindSetupSystem.AddQueueWriter(base.Dependency);
			__TypeHandle.__Game_City_Population_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Areas_DistrictModifier_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Areas_CurrentDistrict_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Objects_OutsideConnection_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_TradeCost_RW_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_BuyingCompany_RW_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_Household_RW_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_ServiceCompanyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_HouseholdAnimal_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Objects_Transform_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_ServiceAvailable_RW_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
			BuyJob buyJob = default(BuyJob);
			buyJob.m_EconomyParameters = m_EconomyParameterQuery.GetSingleton<EconomyParameterData>();
			buyJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
			buyJob.m_SalesQueue = m_SalesQueue;
			buyJob.m_Services = __TypeHandle.__Game_Companies_ServiceAvailable_RW_ComponentLookup;
			buyJob.m_TransformDatas = __TypeHandle.__Game_Objects_Transform_RO_ComponentLookup;
			buyJob.m_PropertyRenters = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup;
			buyJob.m_OwnedVehicles = __TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup;
			buyJob.m_HouseholdCitizens = __TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferLookup;
			buyJob.m_HouseholdAnimals = __TypeHandle.__Game_Citizens_HouseholdAnimal_RO_BufferLookup;
			buyJob.m_Prefabs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
			buyJob.m_ServiceCompanies = __TypeHandle.__Game_Companies_ServiceCompanyData_RO_ComponentLookup;
			buyJob.m_Storages = __TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup;
			buyJob.m_Households = __TypeHandle.__Game_Citizens_Household_RW_ComponentLookup;
			buyJob.m_BuyingCompanies = __TypeHandle.__Game_Companies_BuyingCompany_RW_ComponentLookup;
			buyJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
			buyJob.m_TradeCosts = __TypeHandle.__Game_Companies_TradeCost_RW_BufferLookup;
			buyJob.m_OutsideConnections = __TypeHandle.__Game_Objects_OutsideConnection_RO_ComponentLookup;
			buyJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
			buyJob.m_RandomSeed = RandomSeed.Next();
			buyJob.m_PersonalCarSelectData = m_PersonalCarSelectData;
			buyJob.m_TaxRates = m_TaxSystem.GetTaxRates();
			buyJob.m_Districts = __TypeHandle.__Game_Areas_CurrentDistrict_RO_ComponentLookup;
			buyJob.m_DistrictModifiers = __TypeHandle.__Game_Areas_DistrictModifier_RO_BufferLookup;
			buyJob.m_PopulationData = __TypeHandle.__Game_City_Population_RO_ComponentLookup;
			buyJob.m_PopulationEntity = m_PopulationQuery.GetSingletonEntity();
			buyJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
			BuyJob jobData2 = buyJob;
			base.Dependency = IJobExtensions.Schedule(jobData2, JobHandle.CombineDependencies(base.Dependency, jobHandle));
			m_PersonalCarSelectData.PostUpdate(base.Dependency);
			m_ResourceSystem.AddPrefabsReader(base.Dependency);
			m_TaxSystem.AddReader(base.Dependency);
			m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		}
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
	public ModifiedResourceBuyerSystem()
	{
	}
}
