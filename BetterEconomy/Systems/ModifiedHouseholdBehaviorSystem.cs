using System.Runtime.CompilerServices;
using Colossal.Annotations;
using Colossal.Mathematics;
using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems {
public partial class ModifiedHouseholdBehaviorSystem : GameSystemBase
{
	[BurstCompile]
	private struct HouseholdTickJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		public ComponentTypeHandle<Household> m_HouseholdType;

		public ComponentTypeHandle<HouseholdNeed> m_HouseholdNeedType;

		[ReadOnly]
		public BufferTypeHandle<HouseholdCitizen> m_HouseholdCitizenType;

		public BufferTypeHandle<Game.Economy.Resources> m_ResourceType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		public ComponentTypeHandle<TouristHousehold> m_TouristHouseholdType;

		[ReadOnly]
		public ComponentTypeHandle<CommuterHousehold> m_CommuterHouseholdType;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> m_PropertyRenterType;

		[ReadOnly]
		public ComponentTypeHandle<LodgingSeeker> m_LodgingSeekerType;

		[ReadOnly]
		public ComponentTypeHandle<HomelessHousehold> m_HomelessHouseholdType;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> m_OwnedVehicles;

		[ReadOnly]
		public BufferLookup<Renter> m_RenterBufs;

		[ReadOnly]
		public ComponentLookup<PropertySeeker> m_PropertySeekers;

		[ReadOnly]
		public ComponentLookup<Worker> m_Workers;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<LodgingProvider> m_LodgingProviders;

		[ReadOnly]
		public ComponentLookup<Population> m_Populations;

		[ReadOnly]
		public ComponentLookup<Citizen> m_CitizenDatas;

		[ReadOnly]
		public ComponentLookup<ConsumptionData> m_ConsumptionDatas;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_PrefabRefs;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		public RandomSeed m_RandomSeed;

		public EconomyParameterData m_EconomyParameters;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public uint m_UpdateFrameIndex;

		public Entity m_City;

		private bool NeedsCar(int spendableMoney, int familySize, int cars, ref Unity.Mathematics.Random random)
		{
			if (spendableMoney > kCarBuyingMinimumMoney)
			{
				return (double)random.NextFloat() < (double)((0f - math.log((float)cars + 0.1f)) / 10f) + 0.1;
			}
			return false;
		}

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<Household> nativeArray2 = chunk.GetNativeArray(ref m_HouseholdType);
			NativeArray<HouseholdNeed> nativeArray3 = chunk.GetNativeArray(ref m_HouseholdNeedType);
			BufferAccessor<HouseholdCitizen> bufferAccessor = chunk.GetBufferAccessor(ref m_HouseholdCitizenType);
			BufferAccessor<Game.Economy.Resources> bufferAccessor2 = chunk.GetBufferAccessor(ref m_ResourceType);
			NativeArray<TouristHousehold> nativeArray4 = chunk.GetNativeArray(ref m_TouristHouseholdType);
			NativeArray<PropertyRenter> nativeArray5 = chunk.GetNativeArray(ref m_PropertyRenterType);
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(unfilteredChunkIndex);
			int population = m_Populations[m_City].m_Population;
			for (int i = 0; i < chunk.Count; i++)
			{
				Entity entity = nativeArray[i];
				Household household = nativeArray2[i];
				DynamicBuffer<HouseholdCitizen> citizens = bufferAccessor[i];
				if (citizens.Length == 0)
				{
					m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, default(Deleted));
					continue;
				}
				bool flag = true;
				int num = 0;
				for (int j = 0; j < citizens.Length; j++)
				{
					num += m_CitizenDatas[citizens[j].m_Citizen].Happiness;
					if (m_CitizenDatas[citizens[j].m_Citizen].GetAge() >= CitizenAge.Adult)
					{
						flag = false;
					}
				}
				num /= citizens.Length;
				bool flag2 = (float)random.NextInt(10000) < -3f * math.log(-(100 - num) + 70) + 9f;
				if (flag || flag2)
				{
					m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, default(MovingAway));
					continue;
				}
				DynamicBuffer<Game.Economy.Resources> resources = bufferAccessor2[i];
				HouseholdNeed value = nativeArray3[i];
				if (household.m_Resources > 0)
				{
					int householdTotalWealth = EconomyUtils.GetHouseholdTotalWealth(household, resources);
					float num2 = GetConsumptionMultiplier(m_EconomyParameters.m_ResourceConsumptionMultiplier, householdTotalWealth) * m_EconomyParameters.m_ResourceConsumptionPerCitizen * (float)citizens.Length;
					if (chunk.Has(ref m_TouristHouseholdType))
					{
						num2 *= m_EconomyParameters.m_TouristConsumptionMultiplier;
						if (!chunk.Has(ref m_LodgingSeekerType))
						{
							TouristHousehold value2 = nativeArray4[i];
							if (value2.m_Hotel.Equals(Entity.Null))
							{
								m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, default(LodgingSeeker));
							}
							else if (!m_LodgingProviders.HasComponent(value2.m_Hotel))
							{
								value2.m_Hotel = Entity.Null;
								nativeArray4[i] = value2;
								m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, default(LodgingSeeker));
							}
						}
					}
					int num3 = MathUtils.RoundToIntRandom(ref random, num2);
					household.m_ConsumptionPerDay = (short)math.min(32767, kUpdatesPerDay * num3);
					household.m_Resources = math.max(household.m_Resources - num3, 0);
				}
				else
				{
					household.m_Resources = 0;
					household.m_ConsumptionPerDay = 0;
				}
					// if (value.m_Resource == Resource.NoResource || value.m_Amount < KMinimumShoppingAmount)
				if (false)
				{
					PropertyRenter propertyRenter = ((nativeArray5.Length > 0) ? nativeArray5[i] : default(PropertyRenter));
					int householdSpendableMoney = GetHouseholdSpendableMoney(household, resources, ref m_RenterBufs, ref m_ConsumptionDatas, ref m_PrefabRefs, propertyRenter);
					int num4 = 0;
					if (m_OwnedVehicles.HasBuffer(entity))
					{
						num4 = m_OwnedVehicles[entity].Length;
					}
					int num5 = math.min(kMaxShoppingPossibility, Mathf.RoundToInt(200f / math.max(1f, math.sqrt(m_EconomyParameters.m_TrafficReduction * (float)population))));
					if (random.NextInt(100) < num5)
					{
						ResourceIterator iterator = ResourceIterator.GetIterator();
						int num6 = 0;
						while (iterator.Next())
						{
							num6 += GetResourceShopWeightWithAge(householdSpendableMoney, iterator.resource, m_ResourcePrefabs, ref m_ResourceDatas, num4, leisureIncluded: false, citizens, ref m_CitizenDatas);
						}
						int num7 = random.NextInt(num6);
						iterator = ResourceIterator.GetIterator();
						while (iterator.Next())
						{
							int resourceShopWeightWithAge = GetResourceShopWeightWithAge(householdSpendableMoney, iterator.resource, m_ResourcePrefabs, ref m_ResourceDatas, num4, leisureIncluded: false, citizens, ref m_CitizenDatas);
							num6 -= resourceShopWeightWithAge;
							if (resourceShopWeightWithAge <= 0 || num6 > num7)
							{
								continue;
							}
							if (iterator.resource == Resource.Vehicles && NeedsCar(householdSpendableMoney, citizens.Length, num4, ref random))
							{
								value.m_Resource = Resource.Vehicles;
								value.m_Amount = kCarAmount;
								nativeArray3[i] = value;
								break;
							}
							value.m_Resource = iterator.resource;
							float marketPrice = EconomyUtils.GetMarketPrice(m_ResourceDatas[m_ResourcePrefabs[iterator.resource]]);
							value.m_Amount = math.clamp((int)((float)householdSpendableMoney / marketPrice), 0, kMaxHouseholdNeedAmount);
							if (value.m_Amount > KMinimumShoppingAmount)
							{
								nativeArray3[i] = value;
							}
							break;
						}
					}
				}
				int max = math.clamp(Mathf.RoundToInt(0.06f * (float)population), 64, 1024);
				if (!chunk.Has(ref m_TouristHouseholdType) && !chunk.Has(ref m_CommuterHouseholdType) && !m_PropertySeekers.HasComponent(nativeArray[i]) && (!chunk.Has(ref m_PropertyRenterType) || chunk.Has(ref m_HomelessHouseholdType) || random.NextInt(max) == 0))
				{
					m_CommandBuffer.AddComponent(unfilteredChunkIndex, nativeArray[i], default(PropertySeeker));
				}
				nativeArray2[i] = household;
			}
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

		public static Resource GetResource(ResourceInEditor resource)
	{
		return GetResource((int)(resource - 1));
	}

	public static Resource GetResource(int index)
	{
		if (index < 0)
		{
			return Resource.NoResource;
		}
		return (Resource)(1L << index);
	}

		public static Resource GetResources([CanBeNull] ResourceInEditor[] resources)
	{
		Resource resource = Resource.NoResource;
		if (resources != null)
		{
			foreach (ResourceInEditor resource2 in resources)
			{
				resource |= GetResource(resource2);
			}
		}
		return resource;
	}

	public static int GetResources(Resource resource, DynamicBuffer<Game.Economy.Resources> resources)
	{
		for (int i = 0; i < resources.Length; i++)
		{
			Game.Economy.Resources resources2 = resources[i];
			if (resources2.m_Resource == resource)
			{
				return resources2.m_Amount;
			}
		}
		return 0;
	}

	
	public static int GetHouseholdSpendableMoney(Household householdData, DynamicBuffer<Game.Economy.Resources> resources, ref BufferLookup<Renter> m_RenterBufs, ref ComponentLookup<ConsumptionData> consumptionDatas, ref ComponentLookup<PrefabRef> prefabRefs, PropertyRenter propertyRenter)
	{
		int num = GetResources(Resource.Money, resources);
		int toSpent = 0;
		if (propertyRenter.m_Property != Entity.Null && m_RenterBufs.HasBuffer(propertyRenter.m_Property))
		{
			int length = m_RenterBufs[propertyRenter.m_Property].Length;
			toSpent += propertyRenter.m_Rent;
			Entity prefab = prefabRefs[propertyRenter.m_Property].m_Prefab;
			if (length == 0)
			{
				UnityEngine.Debug.LogWarning($"Property:{propertyRenter.m_Property.Index} has 0 renter");
			}
			int num2 = consumptionDatas[prefab].m_Upkeep / (length + 1);
			toSpent += num2;
		}
		return num - toSpent * 3;
	}


	private struct TypeHandle
	{
		[ReadOnly]
		public EntityTypeHandle __Unity_Entities_Entity_TypeHandle;

		public ComponentTypeHandle<Household> __Game_Citizens_Household_RW_ComponentTypeHandle;

		public ComponentTypeHandle<HouseholdNeed> __Game_Citizens_HouseholdNeed_RW_ComponentTypeHandle;

		public BufferTypeHandle<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferTypeHandle;

		[ReadOnly]
		public BufferTypeHandle<HouseholdCitizen> __Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle;

		public ComponentTypeHandle<TouristHousehold> __Game_Citizens_TouristHousehold_RW_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<CommuterHousehold> __Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle;

		public SharedComponentTypeHandle<UpdateFrame> __Game_Simulation_UpdateFrame_SharedComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<LodgingSeeker> __Game_Citizens_LodgingSeeker_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> __Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<HomelessHousehold> __Game_Citizens_HomelessHousehold_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<Worker> __Game_Citizens_Worker_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> __Game_Vehicles_OwnedVehicle_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<Renter> __Game_Buildings_Renter_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<PropertySeeker> __Game_Agents_PropertySeeker_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<LodgingProvider> __Game_Companies_LodgingProvider_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Citizen> __Game_Citizens_Citizen_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Population> __Game_City_Population_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ConsumptionData> __Game_Prefabs_ConsumptionData_RO_ComponentLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Citizens_Household_RW_ComponentTypeHandle = state.GetComponentTypeHandle<Household>();
			__Game_Citizens_HouseholdNeed_RW_ComponentTypeHandle = state.GetComponentTypeHandle<HouseholdNeed>();
			__Game_Economy_Resources_RW_BufferTypeHandle = state.GetBufferTypeHandle<Game.Economy.Resources>();
			__Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle = state.GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);
			__Game_Citizens_TouristHousehold_RW_ComponentTypeHandle = state.GetComponentTypeHandle<TouristHousehold>();
			__Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle = state.GetComponentTypeHandle<CommuterHousehold>(isReadOnly: true);
			__Game_Simulation_UpdateFrame_SharedComponentTypeHandle = state.GetSharedComponentTypeHandle<UpdateFrame>();
			__Game_Citizens_LodgingSeeker_RO_ComponentTypeHandle = state.GetComponentTypeHandle<LodgingSeeker>(isReadOnly: true);
			__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PropertyRenter>(isReadOnly: true);
			__Game_Citizens_HomelessHousehold_RO_ComponentTypeHandle = state.GetComponentTypeHandle<HomelessHousehold>(isReadOnly: true);
			__Game_Citizens_Worker_RO_ComponentLookup = state.GetComponentLookup<Worker>(isReadOnly: true);
			__Game_Vehicles_OwnedVehicle_RO_BufferLookup = state.GetBufferLookup<OwnedVehicle>(isReadOnly: true);
			__Game_Buildings_Renter_RO_BufferLookup = state.GetBufferLookup<Renter>(isReadOnly: true);
			__Game_Agents_PropertySeeker_RO_ComponentLookup = state.GetComponentLookup<PropertySeeker>(isReadOnly: true);
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Game_Companies_LodgingProvider_RO_ComponentLookup = state.GetComponentLookup<LodgingProvider>(isReadOnly: true);
			__Game_Citizens_Citizen_RO_ComponentLookup = state.GetComponentLookup<Citizen>(isReadOnly: true);
			__Game_City_Population_RO_ComponentLookup = state.GetComponentLookup<Population>(isReadOnly: true);
			__Game_Prefabs_PrefabRef_RO_ComponentLookup = state.GetComponentLookup<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_ConsumptionData_RO_ComponentLookup = state.GetComponentLookup<ConsumptionData>(isReadOnly: true);
		}
	}

	public static readonly int kCarAmount = 50;

	public static readonly int kUpdatesPerDay = 256;

	public static readonly int kMaxShoppingPossibility = 80;

	public static readonly int kMaxHouseholdNeedAmount = 2000;

	public static readonly int kCarBuyingMinimumMoney = 10000;

	public static readonly int KMinimumShoppingAmount = 50;

	private EntityQuery m_HouseholdGroup;

	private EntityQuery m_EconomyParameterGroup;

	private SimulationSystem m_SimulationSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private ResourceSystem m_ResourceSystem;

	private TaxSystem m_TaxSystem;

	private CitySystem m_CitySystem;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 262144 / (kUpdatesPerDay * 16);
	}

	public static float GetLastCommutePerCitizen(DynamicBuffer<HouseholdCitizen> householdCitizens, ComponentLookup<Worker> workers)
	{
		float num = 0f;
		float num2 = 0f;
		for (int i = 0; i < householdCitizens.Length; i++)
		{
			Entity citizen = householdCitizens[i].m_Citizen;
			if (workers.HasComponent(citizen))
			{
				num2 += workers[citizen].m_LastCommuteTime;
			}
			num += 1f;
		}
		return num2 / num;
	}

	public static float GetConsumptionMultiplier(float2 parameter, int householdWealth)
	{
		return parameter.x + parameter.y * math.smoothstep(0f, 1f, (float)(math.max(0, householdWealth) + 1000) / 6000f);
	}

	public static bool GetFreeCar(Entity household, BufferLookup<OwnedVehicle> ownedVehicles, ComponentLookup<Game.Vehicles.PersonalCar> personalCars, ref Entity car)
	{
		if (ownedVehicles.HasBuffer(household))
		{
			DynamicBuffer<OwnedVehicle> dynamicBuffer = ownedVehicles[household];
			for (int i = 0; i < dynamicBuffer.Length; i++)
			{
				car = dynamicBuffer[i].m_Vehicle;
				if (personalCars.HasComponent(car) && personalCars[car].m_Keeper.Equals(Entity.Null))
				{
					return true;
				}
			}
		}
		car = Entity.Null;
		return false;
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_TaxSystem = base.World.GetOrCreateSystemManaged<TaxSystem>();
		m_CitySystem = base.World.GetOrCreateSystemManaged<CitySystem>();
		m_EconomyParameterGroup = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
		m_HouseholdGroup = GetEntityQuery(ComponentType.ReadWrite<Household>(), ComponentType.ReadWrite<HouseholdNeed>(), ComponentType.ReadOnly<HouseholdCitizen>(), ComponentType.ReadOnly<Game.Economy.Resources>(), ComponentType.ReadOnly<UpdateFrame>(), ComponentType.Exclude<TouristHousehold>(), ComponentType.Exclude<MovingAway>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		RequireForUpdate(m_HouseholdGroup);
		RequireForUpdate(m_EconomyParameterGroup);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrameWithInterval = SimulationUtils.GetUpdateFrameWithInterval(m_SimulationSystem.frameIndex, (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation), 16);
		__TypeHandle.__Game_Prefabs_ConsumptionData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_City_Population_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Citizen_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_LodgingProvider_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Agents_PropertySeeker_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Renter_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HomelessHousehold_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_LodgingSeeker_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_TouristHousehold_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdNeed_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Household_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		HouseholdTickJob householdTickJob = default(HouseholdTickJob);
		householdTickJob.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		householdTickJob.m_HouseholdType = __TypeHandle.__Game_Citizens_Household_RW_ComponentTypeHandle;
		householdTickJob.m_HouseholdNeedType = __TypeHandle.__Game_Citizens_HouseholdNeed_RW_ComponentTypeHandle;
		householdTickJob.m_ResourceType = __TypeHandle.__Game_Economy_Resources_RW_BufferTypeHandle;
		householdTickJob.m_HouseholdCitizenType = __TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle;
		householdTickJob.m_TouristHouseholdType = __TypeHandle.__Game_Citizens_TouristHousehold_RW_ComponentTypeHandle;
		householdTickJob.m_CommuterHouseholdType = __TypeHandle.__Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle;
		householdTickJob.m_UpdateFrameType = __TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle;
		householdTickJob.m_LodgingSeekerType = __TypeHandle.__Game_Citizens_LodgingSeeker_RO_ComponentTypeHandle;
		householdTickJob.m_PropertyRenterType = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;
		householdTickJob.m_HomelessHouseholdType = __TypeHandle.__Game_Citizens_HomelessHousehold_RO_ComponentTypeHandle;
		householdTickJob.m_Workers = __TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup;
		householdTickJob.m_OwnedVehicles = __TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup;
		householdTickJob.m_RenterBufs = __TypeHandle.__Game_Buildings_Renter_RO_BufferLookup;
		householdTickJob.m_EconomyParameters = m_EconomyParameterGroup.GetSingleton<EconomyParameterData>();
		householdTickJob.m_PropertySeekers = __TypeHandle.__Game_Agents_PropertySeeker_RO_ComponentLookup;
		householdTickJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		householdTickJob.m_LodgingProviders = __TypeHandle.__Game_Companies_LodgingProvider_RO_ComponentLookup;
		householdTickJob.m_CitizenDatas = __TypeHandle.__Game_Citizens_Citizen_RO_ComponentLookup;
		householdTickJob.m_Populations = __TypeHandle.__Game_City_Population_RO_ComponentLookup;
		householdTickJob.m_PrefabRefs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
		householdTickJob.m_ConsumptionDatas = __TypeHandle.__Game_Prefabs_ConsumptionData_RO_ComponentLookup;
		householdTickJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
		householdTickJob.m_RandomSeed = RandomSeed.Next();
		householdTickJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		householdTickJob.m_UpdateFrameIndex = updateFrameWithInterval;
		householdTickJob.m_City = m_CitySystem.City;
		HouseholdTickJob jobData = householdTickJob;
		base.Dependency = JobChunkExtensions.ScheduleParallel(jobData, m_HouseholdGroup, base.Dependency);
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		m_ResourceSystem.AddPrefabsReader(base.Dependency);
		m_TaxSystem.AddReader(base.Dependency);
	}

	public static int GetAgeWeight(ResourceData resourceData, DynamicBuffer<HouseholdCitizen> citizens, ref ComponentLookup<Citizen> citizenDatas)
	{
		int num = 0;
		for (int i = 0; i < citizens.Length; i++)
		{
			Entity citizen = citizens[i].m_Citizen;
			num = citizenDatas[citizen].GetAge() switch
			{
				CitizenAge.Child => num + resourceData.m_ChildWeight, 
				CitizenAge.Teen => num + resourceData.m_TeenWeight, 
				CitizenAge.Elderly => num + resourceData.m_ElderlyWeight, 
				_ => num + resourceData.m_AdultWeight, 
			};
		}
		return num;
	}

	public static int GetResourceShopWeightWithAge(int wealth, Resource resource, ResourcePrefabs resourcePrefabs, ref ComponentLookup<ResourceData> resourceDatas, int carCount, bool leisureIncluded, DynamicBuffer<HouseholdCitizen> citizens, ref ComponentLookup<Citizen> citizenDatas)
	{
		ResourceData resourceData = resourceDatas[resourcePrefabs[resource]];
		return GetResourceShopWeightWithAge(wealth, resourceData, carCount, leisureIncluded, citizens, ref citizenDatas);
	}

	public static int GetResourceShopWeightWithAge(int wealth, ResourceData resourceData, int carCount, bool leisureIncluded, DynamicBuffer<HouseholdCitizen> citizens, ref ComponentLookup<Citizen> citizenDatas)
	{
		float num = ((leisureIncluded || !resourceData.m_IsLeisure) ? resourceData.m_BaseConsumption : 0f);
		num += (float)(carCount * resourceData.m_CarConsumption);
		float a = ((leisureIncluded || !resourceData.m_IsLeisure) ? resourceData.m_WealthModifier : 0f);
		float num2 = GetAgeWeight(resourceData, citizens, ref citizenDatas);
		return Mathf.RoundToInt(100f * num2 * num * math.smoothstep(a, 1f, math.max(0.01f, ((float)wealth + 5000f) / 10000f)));
	}

	public static int GetWeight(int wealth, Resource resource, ResourcePrefabs resourcePrefabs, ref ComponentLookup<ResourceData> resourceDatas, int carCount, bool leisureIncluded)
	{
		ResourceData resourceData = resourceDatas[resourcePrefabs[resource]];
		return GetWeight(wealth, resourceData, carCount, leisureIncluded);
	}

	public static int GetWeight(int wealth, ResourceData resourceData, int carCount, bool leisureIncluded)
	{
		float num = ((leisureIncluded || !resourceData.m_IsLeisure) ? resourceData.m_BaseConsumption : 0f) + (float)(carCount * resourceData.m_CarConsumption);
		float a = ((leisureIncluded || !resourceData.m_IsLeisure) ? resourceData.m_WealthModifier : 0f);
		return Mathf.RoundToInt(num * math.smoothstep(a, 1f, math.clamp(((float)wealth + 5000f) / 10000f, 0.1f, 0.9f)));
	}

	public static int GetHighestEducation(DynamicBuffer<HouseholdCitizen> citizenBuffer, ref ComponentLookup<Citizen> citizens)
	{
		int num = 0;
		for (int i = 0; i < citizenBuffer.Length; i++)
		{
			Entity citizen = citizenBuffer[i].m_Citizen;
			if (citizens.HasComponent(citizen))
			{
				Citizen citizen2 = citizens[citizen];
				CitizenAge age = citizen2.GetAge();
				if (age == CitizenAge.Teen || age == CitizenAge.Adult)
				{
					num = math.max(num, citizen2.GetEducationLevel());
				}
			}
		}
		return num;
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
	public ModifiedHouseholdBehaviorSystem()
	{
	}
}
}