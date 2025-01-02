using System.Runtime.CompilerServices;
using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace BetterEconomy.Systems;

public partial class ModifiedPayWageSystem : GameSystemBase
{
	private struct Payment
	{
		public Entity m_Target;

		public int m_Amount;
	}

	[BurstCompile]
	private struct PayJob : IJob
	{
		public NativeQueue<Payment> m_PaymentQueue;

		public BufferLookup<Game.Economy.Resources> m_Resources;

		public void Execute()
		{
			Payment item;
			while (m_PaymentQueue.TryDequeue(out item))
			{
				if (m_Resources.HasBuffer(item.m_Target))
				{
					EconomyUtils.AddResources(Resource.Money, item.m_Amount, m_Resources[item.m_Target]);
						// BetterEconomy.log.Info($"[positive]wage queue {item.m_Amount} for {item.m_Target.Index}");
					
				}
			}
		}
	}

	[BurstCompile]
	private struct PayWageJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		public ComponentTypeHandle<TaxPayer> m_TaxPayerType;

		[ReadOnly]
		public BufferTypeHandle<HouseholdCitizen> m_HouseholdCitizenType;

		public BufferTypeHandle<Game.Economy.Resources> m_ResourcesType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		[ReadOnly]
		public ComponentTypeHandle<CommuterHousehold> m_CommuterHouseholdType;

		[ReadOnly]
		public ComponentLookup<CompanyData> m_Companies;

		[ReadOnly]
		public BufferLookup<Employee> m_EmployeeBufs;

		[ReadOnly]
		public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;

		[NativeDisableParallelForRestriction]
		public ComponentLookup<Citizen> m_Citizens;

		[ReadOnly]
		public ComponentLookup<Worker> m_Workers;

		[ReadOnly]
		public NativeArray<int> m_TaxRates;

		public EconomyParameterData m_EconomyParameters;

		public uint m_UpdateFrameIndex;

		public NativeQueue<Payment>.ParallelWriter m_PaymentQueue;

		private void PayWage(Entity workplace, Entity worker, Entity household, Worker workerData, ref TaxPayer taxPayer, DynamicBuffer<Game.Economy.Resources> resources, CitizenAge age, bool isCommuter, ref EconomyParameterData economyParameters)
		{
			int num = 0;
			Citizen value = m_Citizens[worker];
			if (workplace != Entity.Null && m_EmployeeBufs.HasBuffer(workplace))
			{
				bool flag = false;
				for (int i = 0; i < m_EmployeeBufs[workplace].Length; i++)
				{
					if (m_EmployeeBufs[workplace][i].m_Worker == worker)
					{
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					return;
				}
				int num2 = economyParameters.GetWage(workerData.m_Level);
				if (isCommuter)
				{
					num2 = Mathf.RoundToInt((float)num2 * economyParameters.m_CommuterWageMultiplier);
				}
				num = num2 / kUpdatesPerDay;
				if (m_Companies.HasComponent(workplace))
				{
					m_PaymentQueue.Enqueue(new Payment
					{
						m_Target = workplace,
						m_Amount = -num
					});
				}
				if (value.m_UnemploymentCounter > 0)
				{
					value.m_UnemploymentCounter = 0;
					m_Citizens[worker] = value;
				}
			}
			else
			{
				switch (age)
				{
				case CitizenAge.Child:
					num = economyParameters.m_FamilyAllowance / kUpdatesPerDay;
					break;
				case CitizenAge.Elderly:
					num = economyParameters.m_Pension / kUpdatesPerDay;
					break;
				default:
					if ((float)value.m_UnemploymentCounter < economyParameters.m_UnemploymentAllowanceMaxDays * (float)kUpdatesPerDay)
					{
						value.m_UnemploymentCounter++;
						m_Citizens[worker] = value;
						num = economyParameters.m_UnemploymentBenefit / kUpdatesPerDay;
					}
					break;
				}
			}
			EconomyUtils.AddResources(Resource.Money, num, resources);
									// BetterEconomy.log.Info($"[positive]wage  {num} for {worker.Index}");
			int residentialTaxRate = TaxSystem.GetResidentialTaxRate(workerData.m_Level, m_TaxRates);
			bool flag2 = m_OutsideConnections.HasComponent(workplace);
			num -= economyParameters.m_ResidentialMinimumEarnings / kUpdatesPerDay;
			if (!isCommuter && !flag2 && residentialTaxRate != 0 && num > 0)
			{
				taxPayer.m_AverageTaxRate = Mathf.RoundToInt(math.lerp(taxPayer.m_AverageTaxRate, residentialTaxRate, (float)num / (float)(num + taxPayer.m_UntaxedIncome)));
				taxPayer.m_UntaxedIncome += num;
			}
		}

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (m_UpdateFrameIndex != chunk.GetSharedComponent(m_UpdateFrameType).m_Index)
			{
				return;
			}
			bool isCommuter = chunk.Has(ref m_CommuterHouseholdType);
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<TaxPayer> nativeArray2 = chunk.GetNativeArray(ref m_TaxPayerType);
			BufferAccessor<HouseholdCitizen> bufferAccessor = chunk.GetBufferAccessor(ref m_HouseholdCitizenType);
			BufferAccessor<Game.Economy.Resources> bufferAccessor2 = chunk.GetBufferAccessor(ref m_ResourcesType);
			for (int i = 0; i < chunk.Count; i++)
			{
				Entity household = nativeArray[i];
				DynamicBuffer<HouseholdCitizen> dynamicBuffer = bufferAccessor[i];
				DynamicBuffer<Game.Economy.Resources> resources = bufferAccessor2[i];
				TaxPayer taxPayer = nativeArray2[i];
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					Entity citizen = dynamicBuffer[j].m_Citizen;
					Entity workplace = Entity.Null;
					Worker workerData = default(Worker);
					if (m_Workers.HasComponent(citizen))
					{
						workplace = m_Workers[citizen].m_Workplace;
						workerData = m_Workers[citizen];
					}
					CitizenAge age = m_Citizens[citizen].GetAge();
					PayWage(workplace, citizen, household, workerData, ref taxPayer, resources, age, isCommuter, ref m_EconomyParameters);
				}
				nativeArray2[i] = taxPayer;
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
		public BufferTypeHandle<HouseholdCitizen> __Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle;

		public BufferTypeHandle<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferTypeHandle;

		public ComponentTypeHandle<TaxPayer> __Game_Agents_TaxPayer_RW_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<CommuterHousehold> __Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<Worker> __Game_Citizens_Worker_RO_ComponentLookup;

		public ComponentLookup<Citizen> __Game_Citizens_Citizen_RW_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CompanyData> __Game_Companies_CompanyData_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<Employee> __Game_Companies_Employee_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<Game.Objects.OutsideConnection> __Game_Objects_OutsideConnection_RO_ComponentLookup;

		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle = state.GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferTypeHandle = state.GetBufferTypeHandle<Game.Economy.Resources>();
			__Game_Agents_TaxPayer_RW_ComponentTypeHandle = state.GetComponentTypeHandle<TaxPayer>();
			__Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle = state.GetComponentTypeHandle<CommuterHousehold>(isReadOnly: true);
			__Game_Citizens_Worker_RO_ComponentLookup = state.GetComponentLookup<Worker>(isReadOnly: true);
			__Game_Citizens_Citizen_RW_ComponentLookup = state.GetComponentLookup<Citizen>();
			__Game_Companies_CompanyData_RO_ComponentLookup = state.GetComponentLookup<CompanyData>(isReadOnly: true);
			__Game_Companies_Employee_RO_BufferLookup = state.GetBufferLookup<Employee>(isReadOnly: true);
			__Game_Objects_OutsideConnection_RO_ComponentLookup = state.GetComponentLookup<Game.Objects.OutsideConnection>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>();
		}
	}

	public static readonly int kUpdatesPerDay = 32;

	private SimulationSystem m_SimulationSystem;

	private TaxSystem m_TaxSystem;

	private EntityQuery m_EconomyParameterGroup;

	private EntityQuery m_HouseholdGroup;

	private NativeQueue<Payment> m_PaymentQueue;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 262144 / (kUpdatesPerDay * 16);
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_TaxSystem = base.World.GetOrCreateSystemManaged<TaxSystem>();
		m_PaymentQueue = new NativeQueue<Payment>(Allocator.Persistent);
		m_EconomyParameterGroup = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
		m_HouseholdGroup = GetEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<UpdateFrame>(), ComponentType.ReadOnly<HouseholdCitizen>(), ComponentType.Exclude<TouristHousehold>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
		RequireForUpdate(m_EconomyParameterGroup);
		RequireForUpdate(m_HouseholdGroup);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		m_PaymentQueue.Dispose();
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16);
		__TypeHandle.__Game_Objects_OutsideConnection_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_Employee_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_CompanyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Citizen_RW_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Agents_TaxPayer_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		PayWageJob jobData = default(PayWageJob);
		jobData.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		jobData.m_HouseholdCitizenType = __TypeHandle.__Game_Citizens_HouseholdCitizen_RO_BufferTypeHandle;
		jobData.m_ResourcesType = __TypeHandle.__Game_Economy_Resources_RW_BufferTypeHandle;
		jobData.m_TaxPayerType = __TypeHandle.__Game_Agents_TaxPayer_RW_ComponentTypeHandle;
		jobData.m_CommuterHouseholdType = __TypeHandle.__Game_Citizens_CommuterHousehold_RO_ComponentTypeHandle;
		jobData.m_UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>();
		jobData.m_Workers = __TypeHandle.__Game_Citizens_Worker_RO_ComponentLookup;
		jobData.m_Citizens = __TypeHandle.__Game_Citizens_Citizen_RW_ComponentLookup;
		jobData.m_Companies = __TypeHandle.__Game_Companies_CompanyData_RO_ComponentLookup;
		jobData.m_EmployeeBufs = __TypeHandle.__Game_Companies_Employee_RO_BufferLookup;
		jobData.m_OutsideConnections = __TypeHandle.__Game_Objects_OutsideConnection_RO_ComponentLookup;
		jobData.m_EconomyParameters = m_EconomyParameterGroup.GetSingleton<EconomyParameterData>();
		jobData.m_UpdateFrameIndex = updateFrame;
		jobData.m_PaymentQueue = m_PaymentQueue.AsParallelWriter();
		jobData.m_TaxRates = m_TaxSystem.GetTaxRates();
		JobHandle jobHandle = JobChunkExtensions.ScheduleParallel(jobData, m_HouseholdGroup, base.Dependency);
		m_TaxSystem.AddReader(jobHandle);
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		PayJob payJob = default(PayJob);
		payJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		payJob.m_PaymentQueue = m_PaymentQueue;
		PayJob jobData2 = payJob;
		base.Dependency = IJobExtensions.Schedule(jobData2, jobHandle);
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
	public ModifiedPayWageSystem()
	{
	}
}
