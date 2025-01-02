using System.Runtime.CompilerServices;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;

public partial class ModifiedUtilityFeeSystem : GameSystemBase
{
	[BurstCompile]
	private struct SellUtilitiesJob : IJobChunk
	{
		[ReadOnly]
		public ComponentTypeHandle<ElectricityConsumer> m_ElectricityConsumerType;

		[ReadOnly]
		public ComponentTypeHandle<WaterConsumer> m_WaterConsumerType;

		[ReadOnly]
		public BufferTypeHandle<Renter> m_RenterType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		[NativeDisableParallelForRestriction]
		public BufferLookup<Resources> m_Resources;

		[ReadOnly]
		public BufferLookup<ServiceFee> m_Fees;

		public Entity m_City;

		public uint m_UpdateFrameIndex;

		public NativeQueue<StatisticsEvent>.ParallelWriter m_StatQueue;

		public NativeQueue<ServiceFeeSystem.FeeEvent>.ParallelWriter m_FeeQueue;

		public RandomSeed m_RandomSeed;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			Random random = m_RandomSeed.GetRandom(1 + unfilteredChunkIndex);
			random.NextUInt();
			NativeArray<ElectricityConsumer> nativeArray = chunk.GetNativeArray(ref m_ElectricityConsumerType);
			NativeArray<WaterConsumer> nativeArray2 = chunk.GetNativeArray(ref m_WaterConsumerType);
			BufferAccessor<Renter> bufferAccessor = chunk.GetBufferAccessor(ref m_RenterType);
			bool flag = nativeArray.Length != 0;
			bool flag2 = nativeArray2.Length != 0;
			float fee = ServiceFeeSystem.GetFee(PlayerResource.Water, m_Fees[m_City]);
			float fee2 = ServiceFeeSystem.GetFee(PlayerResource.Water, m_Fees[m_City]);
			float3 @float = new float3(fee, fee2, fee2);
			float3 float2 = 0;
			for (int i = 0; i < chunk.Count; i++)
			{
				DynamicBuffer<Renter> dynamicBuffer = bufferAccessor[i];
				if (dynamicBuffer.Length <= 0)
				{
					continue;
				}
				float3 float3 = new float3(flag ? ((float)nativeArray[i].m_FulfilledConsumption) : 0f, flag2 ? ((float)nativeArray2[i].m_FulfilledFresh) : 0f, flag2 ? ((float)nativeArray2[i].m_FulfilledSewage) : 0f);
				float3 /= 128f;
				float2 += float3;
				float value = math.csum(float3 * @float) / (float)dynamicBuffer.Length;
				foreach (Renter item in dynamicBuffer)
				{
					if (m_Resources.TryGetBuffer(item, out var bufferData))
					{
						int num = MathUtils.RoundToIntRandom(ref random, value);
						EconomyUtils.AddResources(Resource.Money, -num, bufferData);
							// BetterEconomy.log.Info($"[nagative] utility fee {-num} for {item.m_Renter.Index}");
					}
				}
			}
			float3 float4 = float2 * @float;
			EnqueueFeeEvent(PlayerResource.Electricity, float2.x, float4.x);
			EnqueueFeeEvent(PlayerResource.Water, float2.y, float4.y);
			EnqueueFeeEvent(PlayerResource.Sewage, float2.z, float4.z);
			EnqueueStatIncomeEvent(IncomeSource.FeeElectricity, float4.x);
			EnqueueStatIncomeEvent(IncomeSource.FeeWater, float4.y + float4.z);
		}

		private void EnqueueFeeEvent(PlayerResource resource, float totalSold, float totalIncome)
		{
			m_FeeQueue.Enqueue(new ServiceFeeSystem.FeeEvent
			{
				m_Resource = resource,
				m_Amount = totalSold,
				m_Cost = totalIncome,
				m_Outside = false
			});
		}

		private void EnqueueStatIncomeEvent(IncomeSource source, float totalIncome)
		{
			m_StatQueue.Enqueue(new StatisticsEvent
			{
				m_Statistic = StatisticType.Income,
				m_Change = totalIncome,
				m_Parameter = (int)source
			});
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	private struct TypeHandle
	{
		[ReadOnly]
		public BufferTypeHandle<Renter> __Game_Buildings_Renter_RO_BufferTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ElectricityConsumer> __Game_Buildings_ElectricityConsumer_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<WaterConsumer> __Game_Buildings_WaterConsumer_RO_ComponentTypeHandle;

		public BufferLookup<Resources> __Game_Economy_Resources_RW_BufferLookup;

		[ReadOnly]
		public BufferLookup<ServiceFee> __Game_City_ServiceFee_RO_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Game_Buildings_Renter_RO_BufferTypeHandle = state.GetBufferTypeHandle<Renter>(isReadOnly: true);
			__Game_Buildings_ElectricityConsumer_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ElectricityConsumer>(isReadOnly: true);
			__Game_Buildings_WaterConsumer_RO_ComponentTypeHandle = state.GetComponentTypeHandle<WaterConsumer>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Resources>();
			__Game_City_ServiceFee_RO_BufferLookup = state.GetBufferLookup<ServiceFee>(isReadOnly: true);
		}
	}

	private const int kUpdatesPerDay = 128;

	private SimulationSystem m_SimulationSystem;

	private CitySystem m_CitySystem;

	private CityStatisticsSystem m_CityStatisticsSystem;

	private ServiceFeeSystem m_ServiceFeeSystem;

	private EntityQuery m_ConsumerGroup;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 128;
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_CitySystem = base.World.GetOrCreateSystemManaged<CitySystem>();
		m_CityStatisticsSystem = base.World.GetOrCreateSystemManaged<CityStatisticsSystem>();
		m_ServiceFeeSystem = base.World.GetOrCreateSystemManaged<ServiceFeeSystem>();
		m_ConsumerGroup = GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[3]
			{
				ComponentType.ReadOnly<Building>(),
				ComponentType.ReadOnly<Renter>(),
				ComponentType.ReadOnly<UpdateFrame>()
			},
			Any = new ComponentType[2]
			{
				ComponentType.ReadOnly<ElectricityConsumer>(),
				ComponentType.ReadOnly<WaterConsumer>()
			},
			None = new ComponentType[2]
			{
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Temp>()
			}
		});
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, 128, 16);
		__TypeHandle.__Game_City_ServiceFee_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_WaterConsumer_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_ElectricityConsumer_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Renter_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		SellUtilitiesJob sellUtilitiesJob = default(SellUtilitiesJob);
		sellUtilitiesJob.m_RenterType = __TypeHandle.__Game_Buildings_Renter_RO_BufferTypeHandle;
		sellUtilitiesJob.m_ElectricityConsumerType = __TypeHandle.__Game_Buildings_ElectricityConsumer_RO_ComponentTypeHandle;
		sellUtilitiesJob.m_WaterConsumerType = __TypeHandle.__Game_Buildings_WaterConsumer_RO_ComponentTypeHandle;
		sellUtilitiesJob.m_UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>();
		sellUtilitiesJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		sellUtilitiesJob.m_Fees = __TypeHandle.__Game_City_ServiceFee_RO_BufferLookup;
		sellUtilitiesJob.m_City = m_CitySystem.City;
		sellUtilitiesJob.m_StatQueue = m_CityStatisticsSystem.GetStatisticsEventQueue(out var deps).AsParallelWriter();
		sellUtilitiesJob.m_UpdateFrameIndex = updateFrame;
		sellUtilitiesJob.m_FeeQueue = m_ServiceFeeSystem.GetFeeQueue(out var deps2).AsParallelWriter();
		sellUtilitiesJob.m_RandomSeed = RandomSeed.Next();
		SellUtilitiesJob jobData = sellUtilitiesJob;
		base.Dependency = JobChunkExtensions.ScheduleParallel(jobData, m_ConsumerGroup, JobHandle.CombineDependencies(base.Dependency, deps, deps2));
		m_CityStatisticsSystem.AddWriter(base.Dependency);
		m_ServiceFeeSystem.AddQueueWriter(base.Dependency);
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
	public ModifiedUtilityFeeSystem()
	{
	}
}
