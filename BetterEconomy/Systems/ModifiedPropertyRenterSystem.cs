using System.Runtime.CompilerServices;
using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;

public partial class ModifiedPropertyRenterSystem : GameSystemBase
{
	[BurstCompile]
	private struct PayRentJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		public BufferTypeHandle<Renter> m_RenterType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> m_PrefabType;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildingData;

		[ReadOnly]
		public ComponentLookup<ZoneData> m_ZoneData;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> m_PropertyRenters;

		[NativeDisableParallelForRestriction]
		public BufferLookup<Game.Economy.Resources> m_Resources;

		[ReadOnly]
		public ComponentLookup<BuildingPropertyData> m_BuildingProperties;

		[ReadOnly]
		public ComponentLookup<PropertyOnMarket> m_PropertiesOnMarket;

		[ReadOnly]
		public ComponentLookup<Abandoned> m_Abandoned;

		[ReadOnly]
		public ComponentLookup<Destroyed> m_Destroyed;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> m_Storages;

		public RandomSeed m_RandomSeed;

		public NativeQueue<ServiceFeeSystem.FeeEvent>.ParallelWriter m_FeeQueue;

		public NativeQueue<StatisticsEvent>.ParallelWriter m_StatisticsEventQueue;

		public uint m_UpdateFrameIndex;

		[ReadOnly]
		public EntityArchetype m_RentEventArchetype;

		public bool m_ProvidedGarbageService;

		public ServiceFeeParameterData m_FeeParameters;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(1 + unfilteredChunkIndex);
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<PrefabRef> nativeArray2 = chunk.GetNativeArray(ref m_PrefabType);
			BufferAccessor<Renter> bufferAccessor = chunk.GetBufferAccessor(ref m_RenterType);
			for (int i = 0; i < chunk.Count; i++)
			{
				DynamicBuffer<Renter> dynamicBuffer = bufferAccessor[i];
				Entity prefab = nativeArray2[i].m_Prefab;
				if (!m_SpawnableBuildingData.HasComponent(prefab))
				{
					continue;
				}
				SpawnableBuildingData spawnableBuildingData = m_SpawnableBuildingData[prefab];
				AreaType areaType = m_ZoneData[spawnableBuildingData.m_ZonePrefab].m_AreaType;
				bool flag = (m_ZoneData[spawnableBuildingData.m_ZonePrefab].m_ZoneFlags & ZoneFlags.Office) != 0;
				BuildingPropertyData buildingPropertyData = m_BuildingProperties[prefab];
				int num = 0;
				switch (areaType)
				{
				case AreaType.Residential:
					num = m_FeeParameters.m_GarbageFeeRCIO.x / kUpdatesPerDay;
					break;
				case AreaType.Commercial:
					num = m_FeeParameters.m_GarbageFeeRCIO.y / kUpdatesPerDay;
					break;
				case AreaType.Industrial:
					num = ((!flag) ? m_FeeParameters.m_GarbageFeeRCIO.w : m_FeeParameters.m_GarbageFeeRCIO.z) / kUpdatesPerDay;
					break;
				}
				if (m_ProvidedGarbageService)
				{
					m_StatisticsEventQueue.Enqueue(new StatisticsEvent
					{
						m_Statistic = StatisticType.Income,
						m_Change = (float)num,
						m_Parameter = 12
					});
					m_FeeQueue.Enqueue(new ServiceFeeSystem.FeeEvent
					{
						m_Amount = 1f,
						m_Cost = (float)num,
						m_Outside = false,
						m_Resource = PlayerResource.Garbage
					});
				}
				int num2 = MathUtils.RoundToIntRandom(ref random, 1f * (float)num / (float)dynamicBuffer.Length);
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					Entity renter = dynamicBuffer[j].m_Renter;
					if (m_PropertyRenters.HasComponent(renter))
					{
						PropertyRenter propertyRenter = m_PropertyRenters[renter];
						int num3 = ((!m_Storages.HasComponent(renter)) ? MathUtils.RoundToIntRandom(ref random, (float)propertyRenter.m_Rent * 1f / (float)kUpdatesPerDay) : EconomyUtils.GetResources(Resource.Money, m_Resources[renter]));
						EconomyUtils.AddResources(Resource.Money, -num3, m_Resources[renter]);
							// BetterEconomy.log.Info($"[nagative]rent total {-num3} for {renter.Index}");
					
						if (!m_Storages.HasComponent(renter))
						{
							// BetterEconomy.log.Info($"[nagative]rent total 2 {-num2} for {renter.Index}");
						 // bug 4
							EconomyUtils.AddResources(Resource.Money, -num2, m_Resources[renter]);
						}
					}
				}
				bool flag2 = !m_Abandoned.HasComponent(nativeArray[i]) && !m_Destroyed.HasComponent(nativeArray[i]);
				bool flag3 = false;
				for (int num4 = dynamicBuffer.Length - 1; num4 >= 0; num4--)
				{
					Entity renter2 = dynamicBuffer[num4].m_Renter;
					if (!m_PropertyRenters.HasComponent(renter2))
					{
						dynamicBuffer.RemoveAt(num4);
						flag3 = true;
					}
				}
				if (dynamicBuffer.Length < buildingPropertyData.CountProperties() && !m_PropertiesOnMarket.HasComponent(nativeArray[i]) && flag2 && !chunk.Has<Signature>())
				{
					m_CommandBuffer.AddComponent(unfilteredChunkIndex, nativeArray[i], default(PropertyToBeOnMarket));
				}
				int num5 = buildingPropertyData.CountProperties();
				while ((dynamicBuffer.Length > 0 && !flag2) || dynamicBuffer.Length > num5)
				{
					Entity renter3 = dynamicBuffer[dynamicBuffer.Length - 1].m_Renter;
					if (m_PropertyRenters.HasComponent(renter3))
					{
						m_CommandBuffer.RemoveComponent<PropertyRenter>(unfilteredChunkIndex, renter3);
					}
					dynamicBuffer.RemoveAt(dynamicBuffer.Length - 1);
					flag3 = true;
				}
				if (flag3)
				{
					Entity e = m_CommandBuffer.CreateEntity(unfilteredChunkIndex, m_RentEventArchetype);
					m_CommandBuffer.SetComponent(unfilteredChunkIndex, e, new RentersUpdated(nativeArray[i]));
				}
			}
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	[BurstCompile]
	private struct RenterMovingAwayJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			for (int i = 0; i < nativeArray.Length; i++)
			{
				Entity e = nativeArray[i];
				m_CommandBuffer.RemoveComponent<PropertyRenter>(unfilteredChunkIndex, e);
			}
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

		public BufferTypeHandle<Renter> __Game_Buildings_Renter_RW_BufferTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> __Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ZoneData> __Game_Prefabs_ZoneData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PropertyRenter> __Game_Buildings_PropertyRenter_RO_ComponentLookup;

		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferLookup;

		[ReadOnly]
		public ComponentLookup<BuildingPropertyData> __Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PropertyOnMarket> __Game_Buildings_PropertyOnMarket_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Abandoned> __Game_Buildings_Abandoned_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Destroyed> __Game_Common_Destroyed_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> __Game_Companies_StorageCompany_RO_ComponentLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Buildings_Renter_RW_BufferTypeHandle = state.GetBufferTypeHandle<Renter>();
			__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup = state.GetComponentLookup<SpawnableBuildingData>(isReadOnly: true);
			__Game_Prefabs_ZoneData_RO_ComponentLookup = state.GetComponentLookup<ZoneData>(isReadOnly: true);
			__Game_Buildings_PropertyRenter_RO_ComponentLookup = state.GetComponentLookup<PropertyRenter>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>();
			__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup = state.GetComponentLookup<BuildingPropertyData>(isReadOnly: true);
			__Game_Buildings_PropertyOnMarket_RO_ComponentLookup = state.GetComponentLookup<PropertyOnMarket>(isReadOnly: true);
			__Game_Buildings_Abandoned_RO_ComponentLookup = state.GetComponentLookup<Abandoned>(isReadOnly: true);
			__Game_Common_Destroyed_RO_ComponentLookup = state.GetComponentLookup<Destroyed>(isReadOnly: true);
			__Game_Companies_StorageCompany_RO_ComponentLookup = state.GetComponentLookup<Game.Companies.StorageCompany>(isReadOnly: true);
		}
	}

	public static readonly int kUpdatesPerDay = 16;

	private SimulationSystem m_SimulationSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private CityStatisticsSystem m_CityStatisticsSystem;

	private ServiceFeeSystem m_ServiceFeeSystem;

	private EntityQuery m_BuildingGroup;

	private EntityQuery m_GarbageFacilityGroup;

	private EntityQuery m_MovingAwayHouseholdGroup;

	private EntityArchetype m_RentEventArchetype;

	private TypeHandle __TypeHandle;

	private EntityQuery __query_595560377_0;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 262144 / (kUpdatesPerDay * 16);
	}

	public static int GetUpkeep(int level, float baseUpkeep, int lotSize, AreaType areaType, ref EconomyParameterData economyParameterData, bool isStorage = false)
	{
		float num;
		switch (areaType)
		{
		case AreaType.Residential:
			return Mathf.RoundToInt(math.pow(level, economyParameterData.m_ResidentialUpkeepLevelExponent) * baseUpkeep * (float)lotSize);
		default:
			num = 1f;
			break;
		case AreaType.Industrial:
			num = economyParameterData.m_IndustrialUpkeepLevelExponent;
			break;
		case AreaType.Commercial:
			num = economyParameterData.m_CommercialUpkeepLevelExponent;
			break;
		}
		float y = num;
		return Mathf.RoundToInt(math.pow(level, y) * baseUpkeep * (float)lotSize * (isStorage ? 0.5f : 1f));
	}

	public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
	{
	}

	public void Deserialize<TReader>(TReader reader) where TReader : IReader
	{
		if (reader.context.version >= Version.taxRateArrayLength && reader.context.version < Version.economyFix)
		{
			reader.Read(out Entity _);
		}
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_CityStatisticsSystem = base.World.GetOrCreateSystemManaged<CityStatisticsSystem>();
		m_ServiceFeeSystem = base.World.GetOrCreateSystemManaged<ServiceFeeSystem>();
		m_BuildingGroup = GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[3]
			{
				ComponentType.ReadOnly<Building>(),
				ComponentType.ReadOnly<Renter>(),
				ComponentType.ReadOnly<UpdateFrame>()
			},
			Any = new ComponentType[1] { ComponentType.ReadWrite<BuildingCondition>() },
			None = new ComponentType[2]
			{
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Temp>()
			}
		});
		m_GarbageFacilityGroup = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.GarbageFacility>(), ComponentType.Exclude<Game.Objects.OutsideConnection>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		m_MovingAwayHouseholdGroup = GetEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<MovingAway>(), ComponentType.ReadOnly<PropertyRenter>());
		m_RentEventArchetype = base.EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<RentersUpdated>());
		RequireForUpdate(m_BuildingGroup);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16);
		bool providedGarbageService = false;
		NativeArray<Entity> nativeArray = m_GarbageFacilityGroup.ToEntityArray(Allocator.TempJob);
		for (int i = 0; i < nativeArray.Length; i++)
		{
			if (base.EntityManager.TryGetComponent<Building>(nativeArray[i], out var component) && !BuildingUtils.CheckOption(component, BuildingOption.Inactive))
			{
				providedGarbageService = true;
				break;
			}
		}
		nativeArray.Dispose();
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		RenterMovingAwayJob jobData = default(RenterMovingAwayJob);
		jobData.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		jobData.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		JobHandle jobHandle = JobChunkExtensions.Schedule(jobData, m_MovingAwayHouseholdGroup, base.Dependency);
		m_EndFrameBarrier.AddJobHandleForProducer(jobHandle);
		__TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Common_Destroyed_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Abandoned_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_PropertyOnMarket_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ZoneData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Renter_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		PayRentJob jobData2 = default(PayRentJob);
		jobData2.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		jobData2.m_RenterType = __TypeHandle.__Game_Buildings_Renter_RW_BufferTypeHandle;
		jobData2.m_PrefabType = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;
		jobData2.m_UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>();
		jobData2.m_SpawnableBuildingData = __TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;
		jobData2.m_ZoneData = __TypeHandle.__Game_Prefabs_ZoneData_RO_ComponentLookup;
		jobData2.m_PropertyRenters = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentLookup;
		jobData2.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		jobData2.m_BuildingProperties = __TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;
		jobData2.m_PropertiesOnMarket = __TypeHandle.__Game_Buildings_PropertyOnMarket_RO_ComponentLookup;
		jobData2.m_Abandoned = __TypeHandle.__Game_Buildings_Abandoned_RO_ComponentLookup;
		jobData2.m_Destroyed = __TypeHandle.__Game_Common_Destroyed_RO_ComponentLookup;
		jobData2.m_Storages = __TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup;
		jobData2.m_RentEventArchetype = m_RentEventArchetype;
		jobData2.m_RandomSeed = RandomSeed.Next();
		jobData2.m_FeeParameters = __query_595560377_0.GetSingleton<ServiceFeeParameterData>();
		jobData2.m_UpdateFrameIndex = updateFrame;
		jobData2.m_ProvidedGarbageService = providedGarbageService;
		jobData2.m_FeeQueue = m_ServiceFeeSystem.GetFeeQueue(out var deps).AsParallelWriter();
		jobData2.m_StatisticsEventQueue = m_CityStatisticsSystem.GetStatisticsEventQueue(out var deps2).AsParallelWriter();
		jobData2.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		jobHandle = JobChunkExtensions.ScheduleParallel(jobData2, m_BuildingGroup, JobHandle.CombineDependencies(jobHandle, deps, deps2));
		m_EndFrameBarrier.AddJobHandleForProducer(jobHandle);
		m_ServiceFeeSystem.AddQueueWriter(jobHandle);
		m_CityStatisticsSystem.AddWriter(jobHandle);
		base.Dependency = jobHandle;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void __AssignQueries(ref SystemState state)
	{
		__query_595560377_0 = state.GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[1] { ComponentType.ReadOnly<ServiceFeeParameterData>() },
			Any = new ComponentType[0],
			None = new ComponentType[0],
			Disabled = new ComponentType[0],
			Absent = new ComponentType[0],
			Options = EntityQueryOptions.IncludeSystems
		});
	}

	protected override void OnCreateForCompiler()
	{
		base.OnCreateForCompiler();
		__AssignQueries(ref base.CheckedStateRef);
		__TypeHandle.__AssignHandles(ref base.CheckedStateRef);
	}

	[Preserve]
	public ModifiedPropertyRenterSystem()
	{
	}
}
