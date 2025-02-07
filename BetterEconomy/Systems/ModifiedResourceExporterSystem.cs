#define UNITY_ASSERTIONS
using System.Runtime.CompilerServices;
using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;
public partial class ModifiedResourceExporterSystem : GameSystemBase
{
	[BurstCompile]
	private struct ExportJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public ComponentTypeHandle<ResourceExporter> m_ResourceExporterType;

		public BufferTypeHandle<TripNeeded> m_TripType;

		[ReadOnly]
		public ComponentLookup<PathInformation> m_PathInformation;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> m_StorageCompanies;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		public NativeQueue<ExportEvent>.ParallelWriter m_ExportQueue;

		public NativeQueue<SetupQueueItem>.ParallelWriter m_PathfindQueue;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			NativeArray<ResourceExporter> nativeArray = chunk.GetNativeArray(ref m_ResourceExporterType);
			NativeArray<Entity> nativeArray2 = chunk.GetNativeArray(m_EntityType);
			BufferAccessor<TripNeeded> bufferAccessor = chunk.GetBufferAccessor(ref m_TripType);
			for (int i = 0; i < chunk.Count; i++)
			{
				Entity entity = nativeArray2[i];
				ResourceExporter resourceExporter = nativeArray[i];
				DynamicBuffer<TripNeeded> dynamicBuffer = bufferAccessor[i];
				bool flag = false;
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					if (dynamicBuffer[j].m_Purpose == Purpose.Exporting)
					{
						flag = true;
						break;
					}
				}
				Entity entity2 = m_ResourcePrefabs[resourceExporter.m_Resource];
				if (m_ResourceDatas.HasComponent(entity2) && EconomyUtils.GetWeight(resourceExporter.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) == 0f)
				{
					m_CommandBuffer.RemoveComponent<ResourceExporter>(unfilteredChunkIndex, entity);
				}
				else if (flag)
				{
					m_CommandBuffer.RemoveComponent<ResourceExporter>(unfilteredChunkIndex, entity);
				}
				else if (m_PathInformation.HasComponent(entity))
				{
					PathInformation pathInformation = m_PathInformation[entity];
					if ((pathInformation.m_State & PathFlags.Pending) == 0)
					{
						Entity destination = pathInformation.m_Destination;
						if (m_StorageCompanies.HasComponent(destination))
						{
							m_ExportQueue.Enqueue(new ExportEvent
							{
								m_Seller = entity,
								m_Buyer = destination,
								m_Distance = pathInformation.m_Distance,
								m_Amount = resourceExporter.m_Amount,
								m_Resource = resourceExporter.m_Resource
							});
							m_CommandBuffer.RemoveComponent<ResourceExporter>(unfilteredChunkIndex, entity);
							m_CommandBuffer.RemoveComponent<PathInformation>(unfilteredChunkIndex, entity);
							m_CommandBuffer.RemoveComponent<PathElement>(unfilteredChunkIndex, entity);
							TripNeeded elem = default(TripNeeded);
							elem.m_TargetAgent = destination;
							elem.m_Purpose = Purpose.Exporting;
							elem.m_Resource = resourceExporter.m_Resource;
							elem.m_Data = resourceExporter.m_Amount;
							dynamicBuffer.Add(elem);
						}
						else
						{
							m_CommandBuffer.RemoveComponent<ResourceExporter>(unfilteredChunkIndex, entity);
							m_CommandBuffer.RemoveComponent<PathInformation>(unfilteredChunkIndex, entity);
							m_CommandBuffer.RemoveComponent<PathElement>(unfilteredChunkIndex, entity);
						}
					}
				}
				else
				{
					FindTarget(unfilteredChunkIndex, entity, resourceExporter.m_Resource, resourceExporter.m_Amount);
				}
			}
		}

		private void FindTarget(int chunkIndex, Entity exporter, Resource resource, int amount)
		{
			m_CommandBuffer.AddComponent(chunkIndex, exporter, new PathInformation
			{
				m_State = PathFlags.Pending
			});
			m_CommandBuffer.AddBuffer<PathElement>(chunkIndex, exporter);
			float transportCost = EconomyUtils.GetTransportCost(1f, amount, m_ResourceDatas[m_ResourcePrefabs[resource]].m_Weight, StorageTransferFlags.Car);
			PathfindParameters pathfindParameters = default(PathfindParameters);
			pathfindParameters.m_MaxSpeed = 111.111115f;
			pathfindParameters.m_WalkSpeed = 5.555556f;
			pathfindParameters.m_Weights = new PathfindWeights(0.01f, 0.01f, transportCost, 0.01f);
			pathfindParameters.m_Methods = PathMethod.Road | PathMethod.CargoLoading;
			pathfindParameters.m_IgnoredRules = RuleFlags.ForbidSlowTraffic;
			PathfindParameters parameters = pathfindParameters;
			SetupQueueTarget setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.CurrentLocation;
			setupQueueTarget.m_Methods = PathMethod.Road | PathMethod.CargoLoading;
			setupQueueTarget.m_RoadTypes = RoadTypes.Car;
			SetupQueueTarget origin = setupQueueTarget;
			setupQueueTarget = default(SetupQueueTarget);
			setupQueueTarget.m_Type = SetupTargetType.ResourceExport;
			setupQueueTarget.m_Methods = PathMethod.Road | PathMethod.CargoLoading;
			setupQueueTarget.m_RoadTypes = RoadTypes.Car;
			setupQueueTarget.m_Resource = resource;
			setupQueueTarget.m_Value = amount;
			SetupQueueTarget destination = setupQueueTarget;
			SetupQueueItem value = new SetupQueueItem(exporter, parameters, origin, destination);
			m_PathfindQueue.Enqueue(value);
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	private struct ExportEvent
	{
		public Resource m_Resource;

		public Entity m_Seller;

		public int m_Amount;

		public Entity m_Buyer;

		public float m_Distance;
	}

	[BurstCompile]
	private struct HandleExportsJob : IJob
	{
		public BufferLookup<Game.Economy.Resources> m_Resources;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> m_Storages;

		public BufferLookup<TradeCost> m_TradeCosts;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		public NativeQueue<ExportEvent> m_ExportQueue;

		public void Execute()
		{
			ExportEvent item;
			while (m_ExportQueue.TryDequeue(out item))
			{
				int resources = EconomyUtils.GetResources(item.m_Resource, m_Resources[item.m_Seller]);
				if (item.m_Amount > 0 && resources > 0)
				{
					item.m_Amount = math.min(item.m_Amount, resources);
					int num = Mathf.RoundToInt(EconomyUtils.GetMarketPrice(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) * (float)item.m_Amount);
					float weight = EconomyUtils.GetWeight(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas);
					if (weight != 0f && m_Storages.HasComponent(item.m_Buyer))
					{
						DynamicBuffer<TradeCost> costs = m_TradeCosts[item.m_Buyer];
						TradeCost tradeCost = EconomyUtils.GetTradeCost(item.m_Resource, costs);
						Assert.IsTrue(item.m_Amount != 0 && !float.IsNaN(tradeCost.m_BuyCost), $"NaN error of Entity:{item.m_Buyer.Index}");
						float num2 = (float)EconomyUtils.GetTransportCost(item.m_Distance, item.m_Resource, item.m_Amount, weight) / (float)item.m_Amount;
						tradeCost.m_BuyCost = math.lerp(tradeCost.m_BuyCost, num2, 0.5f);
						Assert.IsTrue(!float.IsNaN(tradeCost.m_BuyCost), $"NaN error of Entity:{item.m_Buyer.Index}");
						EconomyUtils.SetTradeCost(item.m_Resource, tradeCost, costs, keepLastTime: true);
						DynamicBuffer<TradeCost> costs2 = m_TradeCosts[item.m_Seller];
						tradeCost.m_SellCost = math.lerp(tradeCost.m_SellCost, num2, 0.5f);
						EconomyUtils.SetTradeCost(item.m_Resource, tradeCost, costs2, keepLastTime: true);
						num -= Mathf.RoundToInt(num2);
					}
					EconomyUtils.AddResources(item.m_Resource, -item.m_Amount, m_Resources[item.m_Seller]);
					// bug 6
					// EconomyUtils.AddResources(Resource.Money, num, m_Resources[item.m_Seller]);
				}
			}
		}
	}

	private struct TypeHandle
	{
		[ReadOnly]
		public EntityTypeHandle __Unity_Entities_Entity_TypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ResourceExporter> __Game_Companies_ResourceExporter_RO_ComponentTypeHandle;

		public BufferTypeHandle<TripNeeded> __Game_Citizens_TripNeeded_RW_BufferTypeHandle;

		[ReadOnly]
		public ComponentLookup<PathInformation> __Game_Pathfind_PathInformation_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Companies.StorageCompany> __Game_Companies_StorageCompany_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferLookup;

		public BufferLookup<TradeCost> __Game_Companies_TradeCost_RW_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Companies_ResourceExporter_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ResourceExporter>(isReadOnly: true);
			__Game_Citizens_TripNeeded_RW_BufferTypeHandle = state.GetBufferTypeHandle<TripNeeded>();
			__Game_Pathfind_PathInformation_RO_ComponentLookup = state.GetComponentLookup<PathInformation>(isReadOnly: true);
			__Game_Companies_StorageCompany_RO_ComponentLookup = state.GetComponentLookup<Game.Companies.StorageCompany>(isReadOnly: true);
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>();
			__Game_Companies_TradeCost_RW_BufferLookup = state.GetBufferLookup<TradeCost>();
		}
	}

	private EntityQuery m_ExporterQuery;

	private EntityQuery m_EconomyParameterQuery;

	private PathfindSetupSystem m_PathfindSetupSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private ResourceSystem m_ResourceSystem;

	private TaxSystem m_TaxSystem;

	private NativeQueue<ExportEvent> m_ExportQueue;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 16;
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_PathfindSetupSystem = base.World.GetOrCreateSystemManaged<PathfindSetupSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_TaxSystem = base.World.GetOrCreateSystemManaged<TaxSystem>();
		m_ExporterQuery = GetEntityQuery(ComponentType.ReadOnly<ResourceExporter>(), ComponentType.ReadOnly<TaxPayer>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<Game.Economy.Resources>(), ComponentType.Exclude<ResourceBuyer>(), ComponentType.ReadWrite<TripNeeded>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		m_EconomyParameterQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
		m_ExportQueue = new NativeQueue<ExportEvent>(Allocator.Persistent);
		RequireForUpdate(m_ExporterQuery);
		RequireForUpdate(m_EconomyParameterQuery);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		m_ExportQueue.Dispose();
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Pathfind_PathInformation_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_TripNeeded_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_ResourceExporter_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		ExportJob exportJob = default(ExportJob);
		exportJob.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		exportJob.m_ResourceExporterType = __TypeHandle.__Game_Companies_ResourceExporter_RO_ComponentTypeHandle;
		exportJob.m_TripType = __TypeHandle.__Game_Citizens_TripNeeded_RW_BufferTypeHandle;
		exportJob.m_PathInformation = __TypeHandle.__Game_Pathfind_PathInformation_RO_ComponentLookup;
		exportJob.m_StorageCompanies = __TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup;
		exportJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
		exportJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		exportJob.m_ExportQueue = m_ExportQueue.AsParallelWriter();
		exportJob.m_PathfindQueue = m_PathfindSetupSystem.GetQueue(this, 64).AsParallelWriter();
		exportJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		ExportJob jobData = exportJob;
		base.Dependency = JobChunkExtensions.ScheduleParallel(jobData, m_ExporterQuery, base.Dependency);
		m_ResourceSystem.AddPrefabsReader(base.Dependency);
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		m_PathfindSetupSystem.AddQueueWriter(base.Dependency);
		__TypeHandle.__Game_Companies_TradeCost_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		HandleExportsJob handleExportsJob = default(HandleExportsJob);
		handleExportsJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		handleExportsJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		handleExportsJob.m_Storages = __TypeHandle.__Game_Companies_StorageCompany_RO_ComponentLookup;
		handleExportsJob.m_TradeCosts = __TypeHandle.__Game_Companies_TradeCost_RW_BufferLookup;
		handleExportsJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
		handleExportsJob.m_ExportQueue = m_ExportQueue;
		HandleExportsJob jobData2 = handleExportsJob;
		base.Dependency = IJobExtensions.Schedule(jobData2, base.Dependency);
		m_ResourceSystem.AddPrefabsReader(base.Dependency);
		m_TaxSystem.AddReader(base.Dependency);
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
	public ModifiedResourceExporterSystem()
	{
	}
}
