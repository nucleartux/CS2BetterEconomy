using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Colossal.Collections;
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
using Game.Prefabs;
using Game.Serialization;
using Game.Simulation;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;
using Version = Game.Version;

namespace BetterEconomy.Systems;

public partial class ModifiedProcessingCompanySystem : GameSystemBase
{
	[BurstCompile]
	private struct UpdateProcessingJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> m_PrefabType;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> m_PropertyType;

		[ReadOnly]
		public BufferTypeHandle<Employee> m_EmployeeType;

		[ReadOnly]
		public ComponentTypeHandle<WorkProvider> m_WorkProviderType;

		[ReadOnly]
		public ComponentTypeHandle<ServiceAvailable> m_ServiceAvailableType;

		public BufferTypeHandle<Game.Economy.Resources> m_ResourceType;

		public BufferTypeHandle<TradeCost> m_TradeCostType;

		public ComponentTypeHandle<CompanyData> m_CompanyDataType;

		public ComponentTypeHandle<TaxPayer> m_TaxPayerType;

		public ComponentTypeHandle<Profitability> m_ProfitabilityType;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_Prefabs;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> m_IndustrialProcessDatas;

		[ReadOnly]
		public ComponentLookup<WorkplaceData> m_WorkplaceDatas;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildingDatas;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<StorageLimitData> m_Limits;

		[ReadOnly]
		public ComponentLookup<Building> m_Buildings;

		[ReadOnly]
		public ComponentLookup<Citizen> m_Citizens;

		[ReadOnly]
		public BufferLookup<SpecializationBonus> m_Specializations;

		[ReadOnly]
		public BufferLookup<CityModifier> m_CityModifiers;

		[NativeDisableParallelForRestriction]
		public BufferLookup<Efficiency> m_BuildingEfficiencies;

		[ReadOnly]
		public NativeArray<int> m_TaxRates;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		[ReadOnly]
		public DeliveryTruckSelectData m_DeliveryTruckSelectData;

		public NativeArray<long> m_ProducedResources;

		public NativeQueue<ProductionSpecializationSystem.ProducedResource>.ParallelWriter m_ProductionQueue;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public EconomyParameterData m_EconomyParameters;

		public RandomSeed m_RandomSeed;

		public Entity m_City;

		public uint m_UpdateFrameIndex;

		 private float GetBuildingEfficiency(DynamicBuffer<Game.Buildings.Efficiency> bufferEfficiency)
            {
                // Logic to get building efficiency is adapted from overloaded method BuildingUtils.GetEfficiency() for a buffer of Efficiency.
                // BuildingUtils.GetEfficiency() cannot be used directly because one of the overloads references Span<float>,
                // which for unknown reasons cannot be resolved by Visual Studio.
                // This unresolved overload causes a compile error, even though that is not the overload which would actually be used.

                // Do each efficiency in the buffer.
                float efficiency = 1f;
                foreach (Game.Buildings.Efficiency item in bufferEfficiency)
                {
                    // Efficiency is multiplicative.
                    efficiency *= math.max(0f, item.m_Efficiency);
                }

                // If efficiency is zero, return zero.
                if (efficiency == 0f)
                {
                    return 0f;
                }

                // Round efficiency to 2 decimal places and make sure it is at least 0.01 (i.e. 1%).
                return math.max(0.01f, math.round(100f * efficiency) / 100f);
            }

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(unfilteredChunkIndex);
			DynamicBuffer<CityModifier> cityModifiers = m_CityModifiers[m_City];
			DynamicBuffer<SpecializationBonus> specializations = m_Specializations[m_City];
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<PrefabRef> nativeArray2 = chunk.GetNativeArray(ref m_PrefabType);
			NativeArray<PropertyRenter> nativeArray3 = chunk.GetNativeArray(ref m_PropertyType);
			BufferAccessor<Game.Economy.Resources> bufferAccessor = chunk.GetBufferAccessor(ref m_ResourceType);
			BufferAccessor<Employee> bufferAccessor2 = chunk.GetBufferAccessor(ref m_EmployeeType);
			NativeArray<CompanyData> nativeArray4 = chunk.GetNativeArray(ref m_CompanyDataType);
			NativeArray<Profitability> nativeArray5 = chunk.GetNativeArray(ref m_ProfitabilityType);
			NativeArray<TaxPayer> nativeArray6 = chunk.GetNativeArray(ref m_TaxPayerType);
			chunk.GetBufferAccessor(ref m_TradeCostType);
			bool flag = chunk.Has(ref m_ServiceAvailableType);
			for (int i = 0; i < chunk.Count; i++)
			{
				Entity entity = nativeArray[i];
				Entity prefab = nativeArray2[i].m_Prefab;
				Entity property = nativeArray3[i].m_Property;
				Profitability value = nativeArray5[i];
				ref CompanyData reference = ref nativeArray4.ElementAt(i);
				if (!m_Buildings.HasComponent(property))
				{
					continue;
				}
				DynamicBuffer<Game.Economy.Resources> resources = bufferAccessor[i];
				IndustrialProcessData industrialProcessData = m_IndustrialProcessDatas[prefab];
				StorageLimitData storageLimitData = m_Limits[prefab];
				float buildingEfficiency = 1f;
				if (m_BuildingEfficiencies.TryGetBuffer(property, out var bufferData))
				{
					UpdateEfficiencyFactors(industrialProcessData, flag, bufferData, cityModifiers, specializations);
					buildingEfficiency = GetBuildingEfficiency(bufferData);
				}
				int companyProductionPerDay = EconomyUtils.GetCompanyProductionPerDay(buildingEfficiency, !flag, bufferAccessor2[i], industrialProcessData, m_ResourcePrefabs, m_ResourceDatas, m_Citizens, ref m_EconomyParameters);
				// bug 7
				float numFloat =  (float)companyProductionPerDay / (float)EconomyUtils.kCompanyUpdatesPerDay;
				int num = MathUtils.RoundToIntRandom(ref random, 1f * numFloat);

				ResourceStack input = industrialProcessData.m_Input1;
				ResourceStack input2 = industrialProcessData.m_Input2;
				ResourceStack output = industrialProcessData.m_Output;
				float num2 = 1f;
				float num3 = 1f;
				int num4 = 0;
				int num5 = 0;
				if (input.m_Resource != Resource.NoResource && (float)input.m_Amount > 0f)
				{
					int resources2 = EconomyUtils.GetResources(input.m_Resource, resources);
					num2 = (float)input.m_Amount * 1f / (float)output.m_Amount;
					num = math.min(num, MathUtils.RoundToIntRandom(ref random,((float)resources2 / num2)));
				}
				if (input2.m_Resource != Resource.NoResource && (float)input2.m_Amount > 0f)
				{
					int resources3 = EconomyUtils.GetResources(input2.m_Resource, resources);
					num3 = (float)input2.m_Amount * 1f / (float)output.m_Amount;
					num = math.min(num, MathUtils.RoundToIntRandom(ref random,((float)resources3 / num3)));
				}
				float num6 = (flag ? EconomyUtils.GetMarketPrice(output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) : EconomyUtils.GetIndustrialPrice(output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)) - (float)input.m_Amount * EconomyUtils.GetIndustrialPrice(input.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) / (float)output.m_Amount - (float)input2.m_Amount * EconomyUtils.GetIndustrialPrice(input2.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) / (float)output.m_Amount;
				int resources4;
				if ((float)num > 0f)
				{
					int num7 = 0;
					if (flag && EconomyUtils.GetResources(output.m_Resource, resources) > 5000)
					{
						continue;
					}
					if (input.m_Resource != Resource.NoResource)
					{
						// bug 10
						num4 = -num;
						int num8 = EconomyUtils.AddResources(input.m_Resource, num4, resources);
						num7 += ((EconomyUtils.GetWeight(input.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) > 0f) ? num8 : 0);
					}
					if (input2.m_Resource != Resource.NoResource)
					{
						// bug 10
						num5 = -num;
						int num9 = EconomyUtils.AddResources(input2.m_Resource, num5, resources);
						num7 += ((EconomyUtils.GetWeight(input2.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) > 0f) ? num9 : 0);
					}
					int x = storageLimitData.m_Limit - num7;
					if (EconomyUtils.GetWeight(output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) > 0f)
					{
						num = math.min(x, num);
					}
					else
					{
						resources4 = EconomyUtils.GetResources(output.m_Resource, resources);
						num = math.clamp(IndustrialAISystem.kMaxVirtualResourceStorage - resources4, 0, num);
					}
					resources4 = EconomyUtils.AddResources(output.m_Resource, num, resources);
					AddProducedResource(output.m_Resource, num);
					if (!flag && reference.m_RandomSeed.NextInt(400000) < num)
					{
						Resource randomUpkeepResource = GetRandomUpkeepResource(reference, output.m_Resource);
						if (EconomyUtils.IsMaterial(randomUpkeepResource, m_ResourcePrefabs, ref m_ResourceDatas))
						{
							Entity e = m_CommandBuffer.CreateEntity(unfilteredChunkIndex);
							m_CommandBuffer.AddComponent(unfilteredChunkIndex, e, new GoodsDeliveryRequest
							{
								m_Amount = 2000,
								m_Flags = (GoodsDeliveryFlags.BuildingUpkeep | GoodsDeliveryFlags.CommercialAllowed | GoodsDeliveryFlags.IndustrialAllowed | GoodsDeliveryFlags.ImportAllowed),
								m_Resource = randomUpkeepResource,
								m_Target = entity
							});
						}
					}
				}
				else
				{
					resources4 = EconomyUtils.GetResources(output.m_Resource, resources);
				}
				value.m_Profitability = (byte)math.min(255f, (float)(num * EconomyUtils.kCompanyUpdatesPerDay) * num6 / 100f);
				//BetterEconomy.log.Info($"profitability id:{entity.Index} total {value.m_Profitability}, production {num}, price {num6}, ppd {companyProductionPerDay}");
				nativeArray5[i] = value;
				TaxPayer value2 = nativeArray6[i];
				int num10 = (flag ? TaxSystem.GetCommercialTaxRate(output.m_Resource, m_TaxRates) : TaxSystem.GetIndustrialTaxRate(output.m_Resource, m_TaxRates));
				if (input.m_Resource != output.m_Resource && (float)num > 0f)
				{
					// bug 3
					int num11 = (int)((float)num * EconomyUtils.GetIndustrialPrice(output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) + ((float)num4 * EconomyUtils.GetIndustrialPrice(input.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) + (float)num5 * EconomyUtils.GetIndustrialPrice(input2.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)));
					// 	BetterEconomy.log.Info($"current tax rate {value2.m_AverageTaxRate}, sum {num}, output {EconomyUtils.GetIndustrialPrice(output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)}, 1 price {num4}, {EconomyUtils.GetIndustrialPrice(input.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)}, 2 price {num5}, {EconomyUtils.GetIndustrialPrice(input2.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)}, total {num11}");
					if (num11 > 0)
					{
						value2.m_AverageTaxRate = Mathf.RoundToInt(math.lerp(value2.m_AverageTaxRate, num10, (float)num11 / (float)(num11 + value2.m_UntaxedIncome)));
						//BetterEconomy.log.Info($"current tax rate  ${num11}, out {num}, inp1 {num4}, inp2 {num5}");
						// bug 11
						value2.m_UntaxedIncome += num11;
						nativeArray6[i] = value2;
					}
				}
				if (!flag && EconomyUtils.IsMaterial(output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) && resources4 > 0)
				{
					m_DeliveryTruckSelectData.TrySelectItem(ref random, output.m_Resource, resources4, out var item);
					if ((float)item.m_Cost / (float)math.min(resources4, item.m_Capacity) < 0.03f)
					{
						_ = 100;
						m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, new ResourceExporter
						{
							m_Resource = output.m_Resource,
							m_Amount = math.max(0, math.min(item.m_Capacity, resources4))
						});
					}
				}
			}
		}

		private void UpdateEfficiencyFactors(IndustrialProcessData process, bool isCommercial, DynamicBuffer<Efficiency> efficiencies, DynamicBuffer<CityModifier> cityModifiers, DynamicBuffer<SpecializationBonus> specializations)
		{
			if (IsOffice(process))
			{
				float value = 1f;
				CityUtils.ApplyModifier(ref value, cityModifiers, CityModifierType.OfficeEfficiency);
				BuildingUtils.SetEfficiencyFactor(efficiencies, EfficiencyFactor.CityModifierOfficeEfficiency, value);
			}
			else if (!isCommercial)
			{
				float value2 = 1f;
				CityUtils.ApplyModifier(ref value2, cityModifiers, CityModifierType.IndustrialEfficiency);
				BuildingUtils.SetEfficiencyFactor(efficiencies, EfficiencyFactor.CityModifierIndustrialEfficiency, value2);
			}
			if (process.m_Output.m_Resource == Resource.Software)
			{
				float value3 = 1f;
				CityUtils.ApplyModifier(ref value3, cityModifiers, CityModifierType.OfficeSoftwareEfficiency);
				BuildingUtils.SetEfficiencyFactor(efficiencies, EfficiencyFactor.CityModifierSoftware, value3);
			}
			else if (process.m_Output.m_Resource == Resource.Electronics)
			{
				float value4 = 1f;
				CityUtils.ApplyModifier(ref value4, cityModifiers, CityModifierType.IndustrialElectronicsEfficiency);
				BuildingUtils.SetEfficiencyFactor(efficiencies, EfficiencyFactor.CityModifierElectronics, value4);
			}
			int resourceIndex = EconomyUtils.GetResourceIndex(process.m_Output.m_Resource);
			if (specializations.Length > resourceIndex)
			{
				float efficiency = 1f + specializations[resourceIndex].GetBonus(m_EconomyParameters.m_MaxCitySpecializationBonus, m_EconomyParameters.m_ResourceProductionCoefficient);
				BuildingUtils.SetEfficiencyFactor(efficiencies, EfficiencyFactor.SpecializationBonus, efficiency);
			}
		}

		private bool IsOffice(IndustrialProcessData process)
		{
			return !EconomyUtils.IsMaterial(process.m_Output.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas);
		}

		private Resource GetRandomUpkeepResource(CompanyData companyData, Resource outputResource)
		{
			switch (companyData.m_RandomSeed.NextInt(4))
			{
			case 0:
				return Resource.Software;
			case 1:
				return Resource.Telecom;
			case 2:
				return Resource.Financial;
			case 3:
				if (EconomyUtils.IsMaterial(outputResource, m_ResourcePrefabs, ref m_ResourceDatas))
				{
					return Resource.Machinery;
				}
				if (!companyData.m_RandomSeed.NextBool())
				{
					return Resource.Furniture;
				}
				return Resource.Paper;
			default:
				return Resource.NoResource;
			}
		}

		private unsafe void AddProducedResource(Resource resource, int amount)
		{
			if (resource != Resource.NoResource)
			{
				long* unsafePtr = (long*)m_ProducedResources.GetUnsafePtr();
				unsafePtr += EconomyUtils.GetResourceIndex(resource);
				Interlocked.Add(ref *unsafePtr, amount);
				m_ProductionQueue.Enqueue(new ProductionSpecializationSystem.ProducedResource
				{
					m_Resource = resource,
					m_Amount = amount
				});
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
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		[ReadOnly]
		public EntityTypeHandle __Unity_Entities_Entity_TypeHandle;

		public SharedComponentTypeHandle<UpdateFrame> __Game_Simulation_UpdateFrame_SharedComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<PropertyRenter> __Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;

		[ReadOnly]
		public BufferTypeHandle<Employee> __Game_Companies_Employee_RO_BufferTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<WorkProvider> __Game_Companies_WorkProvider_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ServiceAvailable> __Game_Companies_ServiceAvailable_RO_ComponentTypeHandle;

		public BufferTypeHandle<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferTypeHandle;

		public BufferTypeHandle<TradeCost> __Game_Companies_TradeCost_RW_BufferTypeHandle;

		public ComponentTypeHandle<CompanyData> __Game_Companies_CompanyData_RW_ComponentTypeHandle;

		public ComponentTypeHandle<TaxPayer> __Game_Agents_TaxPayer_RW_ComponentTypeHandle;

		public ComponentTypeHandle<Profitability> __Game_Companies_Profitability_RW_ComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<IndustrialProcessData> __Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<WorkplaceData> __Game_Prefabs_WorkplaceData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> __Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<StorageLimitData> __Game_Companies_StorageLimitData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Building> __Game_Buildings_Building_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<SpecializationBonus> __Game_City_SpecializationBonus_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<CityModifier> __Game_City_CityModifier_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<Citizen> __Game_Citizens_Citizen_RO_ComponentLookup;

		public BufferLookup<Efficiency> __Game_Buildings_Efficiency_RW_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Simulation_UpdateFrame_SharedComponentTypeHandle = state.GetSharedComponentTypeHandle<UpdateFrame>();
			__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PrefabRef>(isReadOnly: true);
			__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PropertyRenter>(isReadOnly: true);
			__Game_Companies_Employee_RO_BufferTypeHandle = state.GetBufferTypeHandle<Employee>(isReadOnly: true);
			__Game_Companies_WorkProvider_RO_ComponentTypeHandle = state.GetComponentTypeHandle<WorkProvider>(isReadOnly: true);
			__Game_Companies_ServiceAvailable_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ServiceAvailable>(isReadOnly: true);
			__Game_Economy_Resources_RW_BufferTypeHandle = state.GetBufferTypeHandle<Game.Economy.Resources>();
			__Game_Companies_TradeCost_RW_BufferTypeHandle = state.GetBufferTypeHandle<TradeCost>();
			__Game_Companies_CompanyData_RW_ComponentTypeHandle = state.GetComponentTypeHandle<CompanyData>();
			__Game_Agents_TaxPayer_RW_ComponentTypeHandle = state.GetComponentTypeHandle<TaxPayer>();
			__Game_Companies_Profitability_RW_ComponentTypeHandle = state.GetComponentTypeHandle<Profitability>();
			__Game_Prefabs_PrefabRef_RO_ComponentLookup = state.GetComponentLookup<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup = state.GetComponentLookup<IndustrialProcessData>(isReadOnly: true);
			__Game_Prefabs_WorkplaceData_RO_ComponentLookup = state.GetComponentLookup<WorkplaceData>(isReadOnly: true);
			__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup = state.GetComponentLookup<SpawnableBuildingData>(isReadOnly: true);
			__Game_Companies_StorageLimitData_RO_ComponentLookup = state.GetComponentLookup<StorageLimitData>(isReadOnly: true);
			__Game_Buildings_Building_RO_ComponentLookup = state.GetComponentLookup<Building>(isReadOnly: true);
			__Game_City_SpecializationBonus_RO_BufferLookup = state.GetBufferLookup<SpecializationBonus>(isReadOnly: true);
			__Game_City_CityModifier_RO_BufferLookup = state.GetBufferLookup<CityModifier>(isReadOnly: true);
			__Game_Citizens_Citizen_RO_ComponentLookup = state.GetComponentLookup<Citizen>(isReadOnly: true);
			__Game_Buildings_Efficiency_RW_BufferLookup = state.GetBufferLookup<Efficiency>();
		}
	}

	public const int kMaxCommercialOutputResource = 5000;

	public const float kMaximumTransportUnitCost = 0.03f;

	private SimulationSystem m_SimulationSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private ResourceSystem m_ResourceSystem;

	private TaxSystem m_TaxSystem;

	private VehicleCapacitySystem m_VehicleCapacitySystem;

	private ProductionSpecializationSystem m_ProductionSpecializationSystem;

	private CitySystem m_CitySystem;

	private EntityQuery m_CompanyGroup;

	private NativeArray<long> m_ProducedResources;

	private JobHandle m_ProducedResourcesDeps;

	private TypeHandle __TypeHandle;

	private EntityQuery __query_1038562630_0;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 262144 / (EconomyUtils.kCompanyUpdatesPerDay * 16);
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_TaxSystem = base.World.GetOrCreateSystemManaged<TaxSystem>();
		m_VehicleCapacitySystem = base.World.GetOrCreateSystemManaged<VehicleCapacitySystem>();
		m_ProductionSpecializationSystem = base.World.GetOrCreateSystemManaged<ProductionSpecializationSystem>();
		m_CitySystem = base.World.GetExistingSystemManaged<CitySystem>();
		m_CompanyGroup = GetEntityQuery(ComponentType.ReadWrite<Game.Companies.ProcessingCompany>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadWrite<Game.Economy.Resources>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<WorkProvider>(), ComponentType.ReadOnly<UpdateFrame>(), ComponentType.ReadWrite<Employee>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Companies.ExtractorCompany>());
		RequireForUpdate(m_CompanyGroup);
		RequireForUpdate<EconomyParameterData>();
		m_ProducedResources = new NativeArray<long>(EconomyUtils.ResourceCount, Allocator.Persistent);
	}

	public void PostDeserialize(Context context)
	{
		if (!(context.version < Version.officeFix))
		{
			return;
		}
		ResourcePrefabs prefabs = m_ResourceSystem.GetPrefabs();
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		ComponentLookup<ResourceData> _Game_Prefabs_ResourceData_RO_ComponentLookup = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		NativeArray<Entity> nativeArray = m_CompanyGroup.ToEntityArray(Allocator.Temp);
		for (int i = 0; i < nativeArray.Length; i++)
		{
			Entity prefab = base.EntityManager.GetComponentData<PrefabRef>(nativeArray[i]).m_Prefab;
			IndustrialProcessData componentData = base.EntityManager.GetComponentData<IndustrialProcessData>(prefab);
			if (!base.EntityManager.HasComponent<ServiceAvailable>(nativeArray[i]) && _Game_Prefabs_ResourceData_RO_ComponentLookup[prefabs[componentData.m_Output.m_Resource]].m_Weight == 0f)
			{
				DynamicBuffer<Game.Economy.Resources> buffer = base.EntityManager.GetBuffer<Game.Economy.Resources>(nativeArray[i]);
				if (EconomyUtils.GetResources(componentData.m_Output.m_Resource, buffer) >= 500)
				{
					EconomyUtils.AddResources(componentData.m_Output.m_Resource, -500, buffer);
				}
			}
		}
		nativeArray.Dispose();
	}

	[Preserve]
	protected override void OnDestroy()
	{
		m_ProducedResources.Dispose();
		base.OnDestroy();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, EconomyUtils.kCompanyUpdatesPerDay, 16);
		__TypeHandle.__Game_Buildings_Efficiency_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Citizen_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_City_CityModifier_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_City_SpecializationBonus_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Building_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_StorageLimitData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_WorkplaceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_Profitability_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Agents_TaxPayer_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_CompanyData_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_TradeCost_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Economy_Resources_RW_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_WorkProvider_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Companies_Employee_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		UpdateProcessingJob updateProcessingJob = default(UpdateProcessingJob);
		updateProcessingJob.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		updateProcessingJob.m_UpdateFrameType = __TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle;
		updateProcessingJob.m_PrefabType = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;
		updateProcessingJob.m_PropertyType = __TypeHandle.__Game_Buildings_PropertyRenter_RO_ComponentTypeHandle;
		updateProcessingJob.m_EmployeeType = __TypeHandle.__Game_Companies_Employee_RO_BufferTypeHandle;
		updateProcessingJob.m_WorkProviderType = __TypeHandle.__Game_Companies_WorkProvider_RO_ComponentTypeHandle;
		updateProcessingJob.m_ServiceAvailableType = __TypeHandle.__Game_Companies_ServiceAvailable_RO_ComponentTypeHandle;
		updateProcessingJob.m_ResourceType = __TypeHandle.__Game_Economy_Resources_RW_BufferTypeHandle;
		updateProcessingJob.m_TradeCostType = __TypeHandle.__Game_Companies_TradeCost_RW_BufferTypeHandle;
		updateProcessingJob.m_CompanyDataType = __TypeHandle.__Game_Companies_CompanyData_RW_ComponentTypeHandle;
		updateProcessingJob.m_TaxPayerType = __TypeHandle.__Game_Agents_TaxPayer_RW_ComponentTypeHandle;
		updateProcessingJob.m_ProfitabilityType = __TypeHandle.__Game_Companies_Profitability_RW_ComponentTypeHandle;
		updateProcessingJob.m_Prefabs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
		updateProcessingJob.m_IndustrialProcessDatas = __TypeHandle.__Game_Prefabs_IndustrialProcessData_RO_ComponentLookup;
		updateProcessingJob.m_WorkplaceDatas = __TypeHandle.__Game_Prefabs_WorkplaceData_RO_ComponentLookup;
		updateProcessingJob.m_SpawnableBuildingDatas = __TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;
		updateProcessingJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		updateProcessingJob.m_Limits = __TypeHandle.__Game_Companies_StorageLimitData_RO_ComponentLookup;
		updateProcessingJob.m_Buildings = __TypeHandle.__Game_Buildings_Building_RO_ComponentLookup;
		updateProcessingJob.m_Specializations = __TypeHandle.__Game_City_SpecializationBonus_RO_BufferLookup;
		updateProcessingJob.m_CityModifiers = __TypeHandle.__Game_City_CityModifier_RO_BufferLookup;
		updateProcessingJob.m_Citizens = __TypeHandle.__Game_Citizens_Citizen_RO_ComponentLookup;
		updateProcessingJob.m_BuildingEfficiencies = __TypeHandle.__Game_Buildings_Efficiency_RW_BufferLookup;
		updateProcessingJob.m_TaxRates = m_TaxSystem.GetTaxRates();
		updateProcessingJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
		updateProcessingJob.m_DeliveryTruckSelectData = m_VehicleCapacitySystem.GetDeliveryTruckSelectData();
		updateProcessingJob.m_ProducedResources = m_ProducedResources;
		updateProcessingJob.m_ProductionQueue = m_ProductionSpecializationSystem.GetQueue(out var deps).AsParallelWriter();
		updateProcessingJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		updateProcessingJob.m_EconomyParameters = __query_1038562630_0.GetSingleton<EconomyParameterData>();
		updateProcessingJob.m_RandomSeed = RandomSeed.Next();
		updateProcessingJob.m_City = m_CitySystem.City;
		updateProcessingJob.m_UpdateFrameIndex = updateFrame;
		UpdateProcessingJob jobData = updateProcessingJob;
		base.Dependency = JobChunkExtensions.ScheduleParallel(jobData, m_CompanyGroup, JobHandle.CombineDependencies(m_ProducedResourcesDeps, deps, base.Dependency));
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		m_ResourceSystem.AddPrefabsReader(base.Dependency);
		m_ProductionSpecializationSystem.AddQueueWriter(base.Dependency);
		m_TaxSystem.AddReader(base.Dependency);
		m_ProducedResourcesDeps = default(JobHandle);
	}

	public NativeArray<long> GetProducedResourcesArray(out JobHandle dependencies)
	{
		dependencies = base.Dependency;
		return m_ProducedResources;
	}

	public void AddProducedResourcesReader(JobHandle handle)
	{
		m_ProducedResourcesDeps = JobHandle.CombineDependencies(m_ProducedResourcesDeps, handle);
	}

	public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
	{
		writer.Write((byte)m_ProducedResources.Length);
		for (int i = 0; i < m_ProducedResources.Length; i++)
		{
			writer.Write(m_ProducedResources[i]);
		}
	}

	public void Deserialize<TReader>(TReader reader) where TReader : IReader
	{
		reader.Read(out byte value);
		for (int i = 0; i < value; i++)
		{
			reader.Read(out long value2);
			if (i < m_ProducedResources.Length)
			{
				m_ProducedResources[i] = value2;
			}
		}
		for (int j = value; j < m_ProducedResources.Length; j++)
		{
			m_ProducedResources[j] = 0L;
		}
	}

	public void SetDefaults(Context context)
	{
		for (int i = 0; i < m_ProducedResources.Length; i++)
		{
			m_ProducedResources[i] = 0L;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void __AssignQueries(ref SystemState state)
	{
		__query_1038562630_0 = state.GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[1] { ComponentType.ReadOnly<EconomyParameterData>() },
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
	public ModifiedProcessingCompanySystem()
	{
	}
}
