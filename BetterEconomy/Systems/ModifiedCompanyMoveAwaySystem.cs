using System.Runtime.CompilerServices;
using Game;
using Game.Agents;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Notifications;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;


public partial class ModifiedCompanyMoveAwaySystem : GameSystemBase
{
	[BurstCompile]
	private struct CheckMoveAwayJob : IJobChunk
	{
		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> m_PrefabType;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> m_PropertyRenterType;

		[ReadOnly]
		public BufferTypeHandle<Resources> m_ResourceType;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> m_OwnedVehicles;

		[ReadOnly]
		public BufferLookup<LayoutElement> m_LayoutElements;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.DeliveryTruck> m_DeliveryTrucks;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<WorkplaceData> m_WorkplaceDatas;

		[ReadOnly]
		public ComponentLookup<ServiceAvailable> m_ServiceAvailables;

		[ReadOnly]
		public ComponentLookup<OfficeProperty> m_OfficeProperties;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> m_IndustrialProcessDatas;

		[ReadOnly]
		public ComponentLookup<WorkProvider> m_WorkProviders;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		[ReadOnly]
		public EconomyParameterData m_EconomyParameters;

		[ReadOnly]
		public NativeArray<int> m_TaxRates;

		public RandomSeed m_RandomSeed;

		public uint m_UpdateFrameIndex;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			Random random = m_RandomSeed.GetRandom(unfilteredChunkIndex);
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<PrefabRef> nativeArray2 = chunk.GetNativeArray(ref m_PrefabType);
			NativeArray<PropertyRenter> nativeArray3 = chunk.GetNativeArray(ref m_PropertyRenterType);
			BufferAccessor<Resources> bufferAccessor = chunk.GetBufferAccessor(ref m_ResourceType);
			for (int i = 0; i < chunk.Count; i++)
			{
				DynamicBuffer<Resources> resources = bufferAccessor[i];
				Entity entity = nativeArray[i];
				Entity prefab = nativeArray2[i].m_Prefab;
				Entity property = nativeArray3[i].m_Property;
				if (m_WorkplaceDatas.HasComponent(prefab))
				{
					int companyTotalWorth;
					if (m_OwnedVehicles.HasBuffer(entity))
					{
						DynamicBuffer<OwnedVehicle> vehicles = m_OwnedVehicles[entity];
						companyTotalWorth = EconomyUtils.GetCompanyTotalWorth(resources, vehicles, m_LayoutElements, m_DeliveryTrucks, m_ResourcePrefabs, m_ResourceDatas);
					}
					else
					{
						companyTotalWorth = EconomyUtils.GetCompanyTotalWorth(resources, m_ResourcePrefabs, m_ResourceDatas);
					}
					int companyMoveAwayChance = CompanyUtils.GetCompanyMoveAwayChance(entity, prefab, property, ref m_ServiceAvailables, ref m_OfficeProperties, ref m_IndustrialProcessDatas, ref m_WorkProviders, m_TaxRates);
					// bug 0
					if (companyTotalWorth < m_EconomyParameters.m_CompanyBankruptcyLimit && random.NextInt(100) < companyMoveAwayChance)
					{
						m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, default(MovingAway));
					}
				}
			}
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	[BurstCompile]
	private struct MovingAwayJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> m_RenterType;

		[ReadOnly]
		public ComponentLookup<PropertyOnMarket> m_OnMarkets;

		[ReadOnly]
		public ComponentLookup<WorkProvider> m_WorkProviders;

		[ReadOnly]
		public ComponentLookup<Abandoned> m_Abandoneds;

		[ReadOnly]
		public EntityArchetype m_RentEventArchetype;

		[ReadOnly]
		public WorkProviderParameterData m_WorkProviderParameterData;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public IconCommandBuffer m_IconCommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<PropertyRenter> nativeArray2 = chunk.GetNativeArray(ref m_RenterType);
			for (int i = 0; i < nativeArray.Length; i++)
			{
				Entity entity = nativeArray[i];
				Entity property = nativeArray2[i].m_Property;
				if (property != Entity.Null)
				{
					if (!m_OnMarkets.HasComponent(property) && !m_Abandoneds.HasComponent(property))
					{
						m_CommandBuffer.AddComponent(unfilteredChunkIndex, property, default(PropertyToBeOnMarket));
					}
					Entity e = m_CommandBuffer.CreateEntity(unfilteredChunkIndex, m_RentEventArchetype);
					m_CommandBuffer.SetComponent(unfilteredChunkIndex, e, new RentersUpdated(property));
				}
				if (m_WorkProviders.HasComponent(entity))
				{
					WorkProvider workProvider = m_WorkProviders[entity];
					if (workProvider.m_EducatedNotificationEntity != Entity.Null)
					{
						m_IconCommandBuffer.Remove(workProvider.m_EducatedNotificationEntity, m_WorkProviderParameterData.m_EducatedNotificationPrefab);
					}
					if (workProvider.m_UneducatedNotificationEntity != Entity.Null)
					{
						m_IconCommandBuffer.Remove(workProvider.m_UneducatedNotificationEntity, m_WorkProviderParameterData.m_UneducatedNotificationPrefab);
					}
				}
				m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, default(Deleted));
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

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;

		[ReadOnly]
		public BufferTypeHandle<Resources> __Game_Economy_Resources_RO_BufferTypeHandle;

		public SharedComponentTypeHandle<UpdateFrame> __Game_Simulation_UpdateFrame_SharedComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> __Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.DeliveryTruck> __Game_Vehicles_DeliveryTruck_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> __Game_Vehicles_OwnedVehicle_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<LayoutElement> __Game_Vehicles_LayoutElement_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> __Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<WorkplaceData> __Game_Prefabs_WorkplaceData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ServiceAvailable> __Game_Companies_ServiceAvailable_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<OfficeProperty> __Game_Buildings_OfficeProperty_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<WorkProvider> __Game_Companies_WorkProvider_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PropertyOnMarket> __Game_Buildings_PropertyOnMarket_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Abandoned> __Game_Buildings_Abandoned_RO_ComponentLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PrefabRef>(isReadOnly: true);
			__Game_Economy_Resources_RO_BufferTypeHandle = state.GetBufferTypeHandle<Resources>(isReadOnly: true);
			__Game_Simulation_UpdateFrame_SharedComponentTypeHandle = state.GetSharedComponentTypeHandle<UpdateFrame>();
			__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PropertyRenter>(isReadOnly: true);
			__Game_Vehicles_DeliveryTruck_RO_ComponentLookup = state.GetComponentLookup<Game.Vehicles.DeliveryTruck>(isReadOnly: true);
			__Game_Vehicles_OwnedVehicle_RO_BufferLookup = state.GetBufferLookup<OwnedVehicle>(isReadOnly: true);
			__Game_Vehicles_LayoutElement_RO_BufferLookup = state.GetBufferLookup<LayoutElement>(isReadOnly: true);
			__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup = state.GetComponentLookup<IndustrialProcessData>(isReadOnly: true);
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Game_Prefabs_WorkplaceData_RO_ComponentLookup = state.GetComponentLookup<WorkplaceData>(isReadOnly: true);
			__Game_Companies_ServiceAvailable_RO_ComponentLookup = state.GetComponentLookup<ServiceAvailable>(isReadOnly: true);
			__Game_Buildings_OfficeProperty_RO_ComponentLookup = state.GetComponentLookup<OfficeProperty>(isReadOnly: true);
			__Game_Companies_WorkProvider_RO_ComponentLookup = state.GetComponentLookup<WorkProvider>(isReadOnly: true);
			__Game_Buildings_PropertyOnMarket_RO_ComponentLookup = state.GetComponentLookup<PropertyOnMarket>(isReadOnly: true);
			__Game_Buildings_Abandoned_RO_ComponentLookup = state.GetComponentLookup<Abandoned>(isReadOnly: true);
		}
	}

	public static readonly int kUpdatesPerDay = 16;

	private EntityQuery m_CompanyQuery;

	private EntityQuery m_MovingAwayQuery;

	private EntityQuery m_EconomyParameterQuery;

	private EntityArchetype m_RentEventArchetype;

	private SimulationSystem m_SimulationSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private ResourceSystem m_ResourceSystem;

	private TaxSystem m_TaxSystem;

	private IconCommandSystem m_IconCommandSystem;

	private TypeHandle __TypeHandle;

	private EntityQuery __query_731167828_0;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 262144 / (kUpdatesPerDay * 16);
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_TaxSystem = base.World.GetOrCreateSystemManaged<TaxSystem>();
		m_IconCommandSystem = base.World.GetOrCreateSystemManaged<IconCommandSystem>();
		m_CompanyQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Companies.ProcessingCompany>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<WorkProvider>(), ComponentType.ReadOnly<Resources>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Game.Companies.ExtractorCompany>(), ComponentType.Exclude<MovingAway>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		m_MovingAwayQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Companies.ProcessingCompany>(), ComponentType.ReadOnly<MovingAway>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		m_EconomyParameterQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
		m_RentEventArchetype = base.EntityManager.CreateArchetype(ComponentType.ReadWrite<Event>(), ComponentType.ReadWrite<RentersUpdated>());
		RequireAnyForUpdate(m_CompanyQuery, m_MovingAwayQuery);
		RequireForUpdate<WorkProviderParameterData>();
		RequireForUpdate(m_EconomyParameterQuery);
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16);
		JobHandle jobHandle = default(JobHandle);
		if (!m_CompanyQuery.IsEmptyIgnoreFilter)
		{
			__TypeHandle.__Game_Companies_WorkProvider_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Buildings_OfficeProperty_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_WorkplaceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Vehicles_LayoutElement_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Vehicles_DeliveryTruck_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Economy_Resources_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
			CheckMoveAwayJob jobData = default(CheckMoveAwayJob);
			jobData.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
			jobData.m_PrefabType = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;
			jobData.m_ResourceType = __TypeHandle.__Game_Economy_Resources_RO_BufferTypeHandle;
			jobData.m_UpdateFrameType = __TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle;
			jobData.m_PropertyRenterType = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;
			jobData.m_DeliveryTrucks = __TypeHandle.__Game_Vehicles_DeliveryTruck_RO_ComponentLookup;
			jobData.m_OwnedVehicles = __TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup;
			jobData.m_LayoutElements = __TypeHandle.__Game_Vehicles_LayoutElement_RO_BufferLookup;
			jobData.m_IndustrialProcessDatas = __TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;
			jobData.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
			jobData.m_WorkplaceDatas = __TypeHandle.__Game_Prefabs_WorkplaceData_RO_ComponentLookup;
			jobData.m_ServiceAvailables = __TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentLookup;
			jobData.m_OfficeProperties = __TypeHandle.__Game_Buildings_OfficeProperty_RO_ComponentLookup;
			jobData.m_WorkProviders = __TypeHandle.__Game_Companies_WorkProvider_RO_ComponentLookup;
			jobData.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
			jobData.m_EconomyParameters = m_EconomyParameterQuery.GetSingleton<EconomyParameterData>();
			jobData.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
			jobData.m_TaxRates = m_TaxSystem.GetTaxRates();
			jobData.m_RandomSeed = RandomSeed.Next();
			jobData.m_UpdateFrameIndex = updateFrame;
			jobHandle = JobChunkExtensions.ScheduleParallel(jobData, m_CompanyQuery, base.Dependency);
			m_EndFrameBarrier.AddJobHandleForProducer(jobHandle);
			m_ResourceSystem.AddPrefabsReader(jobHandle);
			base.Dependency = jobHandle;
		}
		if (!m_MovingAwayQuery.IsEmptyIgnoreFilter)
		{
			__TypeHandle.__Game_Buildings_Abandoned_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Companies_WorkProvider_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Buildings_PropertyOnMarket_RO_ComponentLookup.Update(ref base.CheckedStateRef);
			__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
			__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
			MovingAwayJob jobData2 = default(MovingAwayJob);
			jobData2.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
			jobData2.m_RenterType = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;
			jobData2.m_OnMarkets = __TypeHandle.__Game_Buildings_PropertyOnMarket_RO_ComponentLookup;
			jobData2.m_WorkProviders = __TypeHandle.__Game_Companies_WorkProvider_RO_ComponentLookup;
			jobData2.m_Abandoneds = __TypeHandle.__Game_Buildings_Abandoned_RO_ComponentLookup;
			jobData2.m_RentEventArchetype = m_RentEventArchetype;
			jobData2.m_WorkProviderParameterData = __query_731167828_0.GetSingleton<WorkProviderParameterData>();
			jobData2.m_IconCommandBuffer = m_IconCommandSystem.CreateCommandBuffer();
			jobData2.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
			JobHandle jobHandle2 = JobChunkExtensions.ScheduleParallel(jobData2, m_MovingAwayQuery, JobHandle.CombineDependencies(jobHandle, base.Dependency));
			m_EndFrameBarrier.AddJobHandleForProducer(jobHandle2);
			base.Dependency = JobHandle.CombineDependencies(jobHandle2, jobHandle);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void __AssignQueries(ref SystemState state)
	{
		__query_731167828_0 = state.GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[1] { ComponentType.ReadOnly<WorkProviderParameterData>() },
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
	public ModifiedCompanyMoveAwaySystem()
	{
	}
}
