using System.Runtime.CompilerServices;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Objects;
using Game.Prefabs;
using Game.Serialization;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;

public partial class ModifiedServiceFeeSystem : GameSystemBase, IServiceFeeSystem, IDefaultSerializable, ISerializable, IPostDeserialize
{
	public struct FeeEvent : ISerializable
	{
		public PlayerResource m_Resource;

		public float m_Amount;

		public float m_Cost;

		public bool m_Outside;

		public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
		{
			writer.Write((int)m_Resource);
			writer.Write(m_Amount);
			writer.Write(m_Cost);
			writer.Write(m_Outside);
		}

		public void Deserialize<TReader>(TReader reader) where TReader : IReader
		{
			reader.Read(out int value);
			m_Resource = (PlayerResource)value;
			reader.Read(out m_Amount);
			reader.Read(out m_Cost);
			reader.Read(out m_Outside);
		}
	}

	[BurstCompile]
	private struct PayFeeJob : IJobChunk
	{
		[ReadOnly]
		public BufferTypeHandle<Patient> m_PatientType;

		[ReadOnly]
		public BufferTypeHandle<Game.Buildings.Student> m_StudentType;

		[ReadOnly]
		public ComponentLookup<HouseholdMember> m_HouseholdMembers;

		[ReadOnly]
		public ComponentLookup<Household> m_Households;

		[ReadOnly]
		public ComponentLookup<Game.Citizens.Student> m_Students;

		[ReadOnly]
		public BufferLookup<ServiceFee> m_Fees;

		public BufferLookup<Resources> m_Resources;

		public NativeQueue<FeeEvent> m_FeeEvents;

		public NativeQueue<StatisticsEvent>.ParallelWriter m_StatisticsEventQueue;

		public Entity m_City;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			BufferAccessor<Patient> bufferAccessor = chunk.GetBufferAccessor(ref m_PatientType);
			BufferAccessor<Game.Buildings.Student> bufferAccessor2 = chunk.GetBufferAccessor(ref m_StudentType);
			if (bufferAccessor.Length != 0)
			{
				for (int i = 0; i < chunk.Count; i++)
				{
					DynamicBuffer<Patient> dynamicBuffer = bufferAccessor[i];
					for (int j = 0; j < dynamicBuffer.Length; j++)
					{
						PayFee(dynamicBuffer[j].m_Patient, PlayerResource.Healthcare);
					}
				}
			}
			if (bufferAccessor2.Length == 0)
			{
				return;
			}
			for (int k = 0; k < chunk.Count; k++)
			{
				DynamicBuffer<Game.Buildings.Student> dynamicBuffer2 = bufferAccessor2[k];
				for (int l = 0; l < dynamicBuffer2.Length; l++)
				{
					Entity student = dynamicBuffer2[l].m_Student;
					if (m_Students.TryGetComponent(student, out var componentData))
					{
						PayFee(student, GetEducationResource(componentData.m_Level));
					}
				}
			}
		}

		private void PayFee(Entity citizen, PlayerResource resource)
		{
			if (m_HouseholdMembers.TryGetComponent(citizen, out var componentData) && m_Resources.TryGetBuffer(componentData.m_Household, out var bufferData))
			{
				float num = GetFee(resource, m_Fees[m_City]) / 128f;
				float amount = 1f / 128f;
				EconomyUtils.AddResources(Resource.Money, (int)(0f - math.round(num)), bufferData);
					// BetterEconomy.log.Info($"[nagative] service fee {-num} for {citizen.Index}");
				m_FeeEvents.Enqueue(new FeeEvent
				{
					m_Resource = resource,
					m_Amount = amount,
					m_Cost = num,
					m_Outside = false
				});
				IncomeSource incomeSource = EconomyUtils.GetIncomeSource(resource);
				if (incomeSource != IncomeSource.Count)
				{
					m_StatisticsEventQueue.Enqueue(new StatisticsEvent
					{
						m_Statistic = StatisticType.Income,
						m_Change = num,
						m_Parameter = (int)incomeSource
					});
				}
			}
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	[BurstCompile]
	private struct FeeToCityJob : IJob
	{
		public BufferLookup<CollectedCityServiceFeeData> m_FeeDatas;

		[ReadOnly]
		public NativeList<Entity> m_FeeDataEntities;

		public NativeQueue<FeeEvent> m_FeeEvents;

		public Entity m_City;

		public void Execute()
		{
			for (int i = 0; i < m_FeeDataEntities.Length; i++)
			{
				DynamicBuffer<CollectedCityServiceFeeData> dynamicBuffer = m_FeeDatas[m_FeeDataEntities[i]];
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					CollectedCityServiceFeeData value = dynamicBuffer[j];
					value.m_Export = 0f;
					value.m_ExportCount = 0f;
					value.m_Import = 0f;
					value.m_ImportCount = 0f;
					value.m_Internal = 0f;
					value.m_InternalCount = 0f;
					dynamicBuffer[j] = value;
				}
			}
			FeeEvent item;
			while (m_FeeEvents.TryDequeue(out item))
			{
				for (int k = 0; k < m_FeeDataEntities.Length; k++)
				{
					DynamicBuffer<CollectedCityServiceFeeData> dynamicBuffer2 = m_FeeDatas[m_FeeDataEntities[k]];
					for (int l = 0; l < dynamicBuffer2.Length; l++)
					{
						if (dynamicBuffer2[l].m_PlayerResource != (int)item.m_Resource)
						{
							continue;
						}
						CollectedCityServiceFeeData value2 = dynamicBuffer2[l];
						if (item.m_Amount > 0f)
						{
							if (item.m_Outside)
							{
								value2.m_Export += item.m_Cost * 128f;
								value2.m_ExportCount += item.m_Amount * 128f;
							}
							else
							{
								value2.m_Internal += item.m_Cost * 128f;
								value2.m_InternalCount += item.m_Amount * 128f;
							}
						}
						else
						{
							value2.m_Import += item.m_Cost * 128f;
							value2.m_ImportCount += (0f - item.m_Amount) * 128f;
						}
						dynamicBuffer2[l] = value2;
					}
				}
			}
		}
	}

	[BurstCompile]
	private struct TriggerJob : IJob
	{
		[ReadOnly]
		[DeallocateOnJobCompletion]
		public NativeArray<Entity> m_Entities;

		[ReadOnly]
		public BufferLookup<CollectedCityServiceFeeData> m_FeeDatas;

		public NativeQueue<TriggerAction> m_ActionQueue;

		public void Execute()
		{
			float num = 0f;
			NativeArray<float> nativeArray = new NativeArray<float>(13, Allocator.Temp);
			for (int i = 0; i < m_Entities.Length; i++)
			{
				DynamicBuffer<CollectedCityServiceFeeData> dynamicBuffer = m_FeeDatas[m_Entities[i]];
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					num += dynamicBuffer[j].m_Export - dynamicBuffer[j].m_Import;
					nativeArray[dynamicBuffer[j].m_PlayerResource] += dynamicBuffer[j].m_Export - dynamicBuffer[j].m_Import;
				}
			}
			for (int k = 0; k < 13; k++)
			{
				SendTradeResourceTrigger((PlayerResource)k, nativeArray[k]);
			}
			m_ActionQueue.Enqueue(new TriggerAction(TriggerType.ServiceTradeBalance, Entity.Null, num));
		}

		private void SendTradeResourceTrigger(PlayerResource resource, float total)
		{
			bool flag = true;
			TriggerType triggerType = TriggerType.NewNotification;
			switch (resource)
			{
			case PlayerResource.Electricity:
				triggerType = TriggerType.CityServiceElectricity;
				break;
			case PlayerResource.Healthcare:
				triggerType = TriggerType.CityServiceHealthcare;
				break;
			case PlayerResource.BasicEducation:
			case PlayerResource.SecondaryEducation:
			case PlayerResource.HigherEducation:
				triggerType = TriggerType.CityServiceEducation;
				break;
			case PlayerResource.Garbage:
				triggerType = TriggerType.CityServiceGarbage;
				break;
			case PlayerResource.Water:
			case PlayerResource.Sewage:
				triggerType = TriggerType.CityServiceWaterSewage;
				break;
			case PlayerResource.Mail:
				triggerType = TriggerType.CityServicePost;
				break;
			case PlayerResource.FireResponse:
				triggerType = TriggerType.CityServiceFireAndRescue;
				break;
			case PlayerResource.Police:
				triggerType = TriggerType.CityServicePolice;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				m_ActionQueue.Enqueue(new TriggerAction(triggerType, Entity.Null, total));
			}
		}
	}

	private struct TypeHandle
	{
		[ReadOnly]
		public BufferTypeHandle<Patient> __Game_Buildings_Patient_RO_BufferTypeHandle;

		[ReadOnly]
		public BufferTypeHandle<Game.Buildings.Student> __Game_Buildings_Student_RO_BufferTypeHandle;

		[ReadOnly]
		public ComponentLookup<HouseholdMember> __Game_Citizens_HouseholdMember_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Household> __Game_Citizens_Household_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Game.Citizens.Student> __Game_Citizens_Student_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<ServiceFee> __Game_City_ServiceFee_RO_BufferLookup;

		public BufferLookup<Resources> __Game_Economy_Resources_RW_BufferLookup;

		public BufferLookup<CollectedCityServiceFeeData> __Game_Simulation_CollectedCityServiceFeeData_RW_BufferLookup;

		[ReadOnly]
		public BufferLookup<CollectedCityServiceFeeData> __Game_Simulation_CollectedCityServiceFeeData_RO_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Game_Buildings_Patient_RO_BufferTypeHandle = state.GetBufferTypeHandle<Patient>(isReadOnly: true);
			__Game_Buildings_Student_RO_BufferTypeHandle = state.GetBufferTypeHandle<Game.Buildings.Student>(isReadOnly: true);
			__Game_Citizens_HouseholdMember_RO_ComponentLookup = state.GetComponentLookup<HouseholdMember>(isReadOnly: true);
			__Game_Citizens_Household_RO_ComponentLookup = state.GetComponentLookup<Household>(isReadOnly: true);
			__Game_Citizens_Student_RO_ComponentLookup = state.GetComponentLookup<Game.Citizens.Student>(isReadOnly: true);
			__Game_City_ServiceFee_RO_BufferLookup = state.GetBufferLookup<ServiceFee>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Resources>();
			__Game_Simulation_CollectedCityServiceFeeData_RW_BufferLookup = state.GetBufferLookup<CollectedCityServiceFeeData>();
			__Game_Simulation_CollectedCityServiceFeeData_RO_BufferLookup = state.GetBufferLookup<CollectedCityServiceFeeData>(isReadOnly: true);
		}
	}

	private const int kUpdatesPerDay = 128;

	private CityStatisticsSystem m_CityStatisticsSystem;

	private CitySystem m_CitySystem;

	private TriggerSystem m_TriggerSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private EntityQuery m_FeeCollectorGroup;

	private EntityQuery m_CollectedFeeGroup;

	private NativeQueue<FeeEvent> m_FeeQueue;

	private NativeList<CollectedCityServiceFeeData> m_CityServiceFees;

	private JobHandle m_Writers;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 2048;
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_CityStatisticsSystem = base.World.GetOrCreateSystemManaged<CityStatisticsSystem>();
		m_CitySystem = base.World.GetOrCreateSystemManaged<CitySystem>();
		m_TriggerSystem = base.World.GetOrCreateSystemManaged<TriggerSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_CollectedFeeGroup = GetEntityQuery(ComponentType.ReadOnly<CollectedCityServiceFeeData>());
		m_FeeCollectorGroup = GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[2]
			{
				ComponentType.ReadOnly<Game.City.ServiceFeeCollector>(),
				ComponentType.ReadOnly<PrefabRef>()
			},
			Any = new ComponentType[2]
			{
				ComponentType.ReadOnly<Patient>(),
				ComponentType.ReadOnly<Game.Buildings.Student>()
			},
			None = new ComponentType[3]
			{
				ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Temp>()
			}
		});
		RequireForUpdate(m_CollectedFeeGroup);
		m_FeeQueue = new NativeQueue<FeeEvent>(Allocator.Persistent);
		m_CityServiceFees = new NativeList<CollectedCityServiceFeeData>(13, Allocator.Persistent);
	}

	public void PostDeserialize(Context context)
	{
		if (context.purpose == Colossal.Serialization.Entities.Purpose.NewGame)
		{
			CacheFees(reset: true);
		}
		else
		{
			CacheFees();
		}
	}

	[Preserve]
	protected override void OnDestroy()
	{
		m_Writers.Complete();
		m_FeeQueue.Dispose();
		m_CityServiceFees.Dispose();
		base.OnDestroy();
	}

	public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
	{
		m_Writers.Complete();
		NativeArray<FeeEvent> nativeArray = m_FeeQueue.ToArray(Allocator.Temp);
		writer.Write(nativeArray.Length);
		foreach (FeeEvent item in nativeArray)
		{
			writer.Write(item);
		}
	}

	public void Deserialize<TReader>(TReader reader) where TReader : IReader
	{
		m_Writers.Complete();
		m_FeeQueue.Clear();
		reader.Read(out int value);
		for (int i = 0; i < value; i++)
		{
			reader.Read(out FeeEvent value2);
			m_FeeQueue.Enqueue(value2);
		}
	}

	public void SetDefaults(Context context)
	{
		m_Writers.Complete();
		m_FeeQueue.Clear();
	}

	public NativeList<CollectedCityServiceFeeData> GetServiceFees()
	{
		return m_CityServiceFees;
	}

	public NativeQueue<FeeEvent> GetFeeQueue(out JobHandle deps)
	{
		deps = m_Writers;
		return m_FeeQueue;
	}

	public void AddQueueWriter(JobHandle deps)
	{
		m_Writers = JobHandle.CombineDependencies(m_Writers, deps);
	}

	public int3 GetServiceFees(PlayerResource resource)
	{
		return GetServiceFees(resource, m_CityServiceFees);
	}

	public int GetServiceFeeIncomeEstimate(PlayerResource resource, float fee)
	{
		return GetServiceFeeIncomeEstimate(resource, fee, m_CityServiceFees);
	}

	public static PlayerResource GetEducationResource(int level)
	{
		switch (level)
		{
		case 1:
			return PlayerResource.BasicEducation;
		case 2:
			return PlayerResource.SecondaryEducation;
		case 3:
		case 4:
			return PlayerResource.HigherEducation;
		default:
			return PlayerResource.Count;
		}
	}

	public static float GetFee(PlayerResource resource, DynamicBuffer<ServiceFee> fees)
	{
		for (int i = 0; i < fees.Length; i++)
		{
			ServiceFee serviceFee = fees[i];
			if (serviceFee.m_Resource == resource)
			{
				return serviceFee.m_Fee;
			}
		}
		return 0f;
	}

	public static bool TryGetFee(PlayerResource resource, DynamicBuffer<ServiceFee> fees, out float fee)
	{
		for (int i = 0; i < fees.Length; i++)
		{
			ServiceFee serviceFee = fees[i];
			if (serviceFee.m_Resource == resource)
			{
				fee = serviceFee.m_Fee;
				return true;
			}
		}
		fee = 0f;
		return false;
	}

	public static void SetFee(PlayerResource resource, DynamicBuffer<ServiceFee> fees, float value)
	{
		for (int i = 0; i < fees.Length; i++)
		{
			ServiceFee value2 = fees[i];
			if (value2.m_Resource == resource)
			{
				value2.m_Fee = value;
				fees[i] = value2;
				return;
			}
		}
		fees.Add(new ServiceFee
		{
			m_Fee = value,
			m_Resource = resource
		});
	}

	public static float GetConsumptionMultiplier(PlayerResource resource, float relativeFee, in ServiceFeeParameterData feeParameters)
	{
		return resource switch
		{
			PlayerResource.Electricity => AdjustElectricityConsumptionSystem.GetFeeConsumptionMultiplier(relativeFee, in feeParameters), 
			PlayerResource.Water => AdjustWaterConsumptionSystem.GetFeeConsumptionMultiplier(relativeFee, in feeParameters), 
			_ => 1f, 
		};
	}

	public static float GetEfficiencyMultiplier(PlayerResource resource, float relativeFee, in BuildingEfficiencyParameterData efficiencyParameters)
	{
		return resource switch
		{
			PlayerResource.Electricity => AdjustElectricityConsumptionSystem.GetFeeEfficiencyFactor(relativeFee, in efficiencyParameters), 
			PlayerResource.Water => AdjustWaterConsumptionSystem.GetFeeEfficiencyFactor(relativeFee, in efficiencyParameters), 
			_ => 1f, 
		};
	}

	public static int GetHappinessEffect(PlayerResource resource, float relativeFee, in CitizenHappinessParameterData happinessParameters)
	{
		return resource switch
		{
			PlayerResource.Electricity => CitizenHappinessSystem.GetElectricityFeeHappinessEffect(relativeFee, in happinessParameters), 
			PlayerResource.Water => CitizenHappinessSystem.GetWaterFeeHappinessEffect(relativeFee, in happinessParameters), 
			_ => 1, 
		};
	}

	public static int3 GetServiceFees(PlayerResource resource, NativeList<CollectedCityServiceFeeData> fees)
	{
		float3 x = default(float3);
		foreach (CollectedCityServiceFeeData item in fees)
		{
			if (item.m_PlayerResource == (int)resource)
			{
				x += new float3(item.m_Internal, item.m_Export, item.m_Import);
			}
		}
		return new int3(math.round(x));
	}

	public static int GetServiceFeeIncomeEstimate(PlayerResource resource, float fee, NativeList<CollectedCityServiceFeeData> fees)
	{
		float num = 0f;
		foreach (CollectedCityServiceFeeData item in fees)
		{
			if (item.m_PlayerResource == (int)resource)
			{
				num += item.m_InternalCount * fee;
			}
		}
		return (int)math.round(num);
	}

	private void CacheFees(bool reset = false)
	{
		NativeArray<Entity> nativeArray = m_CollectedFeeGroup.ToEntityArray(Allocator.TempJob);
		m_CityServiceFees.Clear();
		for (int i = 0; i < nativeArray.Length; i++)
		{
			Entity entity = nativeArray[i];
			DynamicBuffer<CollectedCityServiceFeeData> buffer = base.EntityManager.GetBuffer<CollectedCityServiceFeeData>(entity, !reset);
			for (int j = 0; j < buffer.Length; j++)
			{
				CollectedCityServiceFeeData collectedCityServiceFeeData;
				if (reset)
				{
					collectedCityServiceFeeData = default(CollectedCityServiceFeeData);
					collectedCityServiceFeeData.m_PlayerResource = buffer[j].m_PlayerResource;
					CollectedCityServiceFeeData value = collectedCityServiceFeeData;
					buffer[j] = value;
				}
				ref NativeList<CollectedCityServiceFeeData> cityServiceFees = ref m_CityServiceFees;
				collectedCityServiceFeeData = buffer[j];
				cityServiceFees.Add(in collectedCityServiceFeeData);
			}
		}
		nativeArray.Dispose();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		CacheFees();
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_City_ServiceFee_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Student_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Household_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Student_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Patient_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		PayFeeJob payFeeJob = default(PayFeeJob);
		payFeeJob.m_PatientType = __TypeHandle.__Game_Buildings_Patient_RO_BufferTypeHandle;
		payFeeJob.m_StudentType = __TypeHandle.__Game_Buildings_Student_RO_BufferTypeHandle;
		payFeeJob.m_HouseholdMembers = __TypeHandle.__Game_Citizens_HouseholdMember_RO_ComponentLookup;
		payFeeJob.m_Households = __TypeHandle.__Game_Citizens_Household_RO_ComponentLookup;
		payFeeJob.m_Students = __TypeHandle.__Game_Citizens_Student_RO_ComponentLookup;
		payFeeJob.m_Fees = __TypeHandle.__Game_City_ServiceFee_RO_BufferLookup;
		payFeeJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		payFeeJob.m_FeeEvents = m_FeeQueue;
		payFeeJob.m_StatisticsEventQueue = m_CityStatisticsSystem.GetStatisticsEventQueue(out var deps).AsParallelWriter();
		payFeeJob.m_City = m_CitySystem.City;
		PayFeeJob jobData = payFeeJob;
		base.Dependency = JobChunkExtensions.Schedule(jobData, m_FeeCollectorGroup, JobHandle.CombineDependencies(base.Dependency, deps));
		m_CityStatisticsSystem.AddWriter(base.Dependency);
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		__TypeHandle.__Game_Simulation_CollectedCityServiceFeeData_RW_BufferLookup.Update(ref base.CheckedStateRef);
		FeeToCityJob feeToCityJob = default(FeeToCityJob);
		feeToCityJob.m_FeeDatas = __TypeHandle.__Game_Simulation_CollectedCityServiceFeeData_RW_BufferLookup;
		feeToCityJob.m_FeeDataEntities = m_CollectedFeeGroup.ToEntityListAsync(base.World.UpdateAllocator.ToAllocator, out var outJobHandle);
		feeToCityJob.m_FeeEvents = m_FeeQueue;
		feeToCityJob.m_City = m_CitySystem.City;
		FeeToCityJob jobData2 = feeToCityJob;
		base.Dependency = IJobExtensions.Schedule(jobData2, JobHandle.CombineDependencies(base.Dependency, outJobHandle, m_Writers));
		m_Writers = base.Dependency;
		__TypeHandle.__Game_Simulation_CollectedCityServiceFeeData_RO_BufferLookup.Update(ref base.CheckedStateRef);
		TriggerJob triggerJob = default(TriggerJob);
		triggerJob.m_Entities = m_CollectedFeeGroup.ToEntityArray(Allocator.TempJob);
		triggerJob.m_FeeDatas = __TypeHandle.__Game_Simulation_CollectedCityServiceFeeData_RO_BufferLookup;
		triggerJob.m_ActionQueue = m_TriggerSystem.CreateActionBuffer();
		TriggerJob jobData3 = triggerJob;
		base.Dependency = IJobExtensions.Schedule(jobData3, base.Dependency);
		m_TriggerSystem.AddActionBufferWriter(base.Dependency);
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
	public ModifiedServiceFeeSystem()
	{
	}
}
