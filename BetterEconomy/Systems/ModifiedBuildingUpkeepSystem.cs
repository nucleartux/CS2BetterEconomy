using System;
using System.Runtime.CompilerServices;
using Colossal.Collections;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Net;
using Game.Notifications;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Game.Vehicles;
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

public partial class ModifiedBuildingUpkeepSystem : GameSystemBase
{
	private struct UpkeepPayment
	{
		public Entity m_RenterEntity;

		public int m_Price;
	}

	[BurstCompile]
	private struct BuildingUpkeepJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		public ComponentTypeHandle<BuildingCondition> m_ConditionType;

		[ReadOnly]
		public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> m_PrefabType;

		[ReadOnly]
		public ComponentTypeHandle<Building> m_BuildingType;

		[ReadOnly]
		public BufferTypeHandle<Renter> m_RenterType;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildingDatas;

		[ReadOnly]
		public ComponentLookup<ZoneData> m_ZoneDatas;

		[ReadOnly]
		public BufferLookup<Game.Economy.Resources> m_Resources;

		[ReadOnly]
		public ResourcePrefabs m_ResourcePrefabs;

		[ReadOnly]
		public ComponentLookup<ResourceData> m_ResourceDatas;

		[ReadOnly]
		public ComponentLookup<BuildingData> m_BuildingDatas;

		[ReadOnly]
		public ComponentLookup<BuildingPropertyData> m_BuildingPropertyDatas;

		[ReadOnly]
		public BufferLookup<CityModifier> m_CityModifierBufs;

		[ReadOnly]
		public ComponentLookup<Abandoned> m_Abandoned;

		[ReadOnly]
		public ComponentLookup<Destroyed> m_Destroyed;

		[ReadOnly]
		public ComponentLookup<SignatureBuildingData> m_SignatureDatas;

		[ReadOnly]
		public ComponentLookup<Household> m_Households;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> m_OwnedVehicles;

		[ReadOnly]
		public BufferLookup<LayoutElement> m_LayoutElements;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.DeliveryTruck> m_DeliveryTrucks;

		[ReadOnly]
		public BuildingConfigurationData m_BuildingConfigurationData;

		[ReadOnly]
		public ComponentLookup<ConsumptionData> m_ConsumptionDatas;

		[ReadOnly]
		public BufferLookup<ResourceAvailability> m_Availabilities;

		[ReadOnly]
		public Entity m_City;

		public uint m_UpdateFrameIndex;

		public uint m_SimulationFrame;

		public float m_TemperatureUpkeep;

		public bool m_DebugFastLeveling;

		public NativeQueue<UpkeepPayment>.ParallelWriter m_UpkeepExpenseQueue;

		public NativeQueue<Entity>.ParallelWriter m_LevelupQueue;

		public NativeQueue<Entity>.ParallelWriter m_LevelDownQueue;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrameIndex)
			{
				return;
			}
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<PrefabRef> nativeArray2 = chunk.GetNativeArray(ref m_PrefabType);
			NativeArray<BuildingCondition> nativeArray3 = chunk.GetNativeArray(ref m_ConditionType);
			NativeArray<Building> nativeArray4 = chunk.GetNativeArray(ref m_BuildingType);
			BufferAccessor<Renter> bufferAccessor = chunk.GetBufferAccessor(ref m_RenterType);
			for (int i = 0; i < chunk.Count; i++)
			{
				int num = 0;
				Entity target = nativeArray[i];
				Entity prefab = nativeArray2[i].m_Prefab;
				ConsumptionData consumptionData = m_ConsumptionDatas[prefab];
				BuildingData buildingData = m_BuildingDatas[prefab];
				BuildingPropertyData buildingPropertyData = m_BuildingPropertyDatas[prefab];
				DynamicBuffer<CityModifier> cityEffects = m_CityModifierBufs[m_City];
				SpawnableBuildingData spawnableBuildingData = m_SpawnableBuildingDatas[prefab];
				AreaType areaType = m_ZoneDatas[spawnableBuildingData.m_ZonePrefab].m_AreaType;
				BuildingPropertyData propertyData = m_BuildingPropertyDatas[prefab];
				int levelingCost = BuildingUtils.GetLevelingCost(areaType, propertyData, spawnableBuildingData.m_Level, cityEffects);
				int num2 = ((spawnableBuildingData.m_Level == 5) ? BuildingUtils.GetLevelingCost(areaType, propertyData, 4, cityEffects) : levelingCost);
				if (areaType == AreaType.Residential && propertyData.m_ResidentialProperties > 1)
				{
					num2 = Mathf.RoundToInt((float)(num2 * (6 - spawnableBuildingData.m_Level)) / math.sqrt(propertyData.m_ResidentialProperties));
				}
				DynamicBuffer<Renter> dynamicBuffer = bufferAccessor[i];
				int num3 = consumptionData.m_Upkeep / kUpdatesPerDay;
				int num4 = num3 / kMaterialUpkeep;
				num += num3 - num4;
				Unity.Mathematics.Random random = new Unity.Mathematics.Random((uint)(1 + target.Index * m_SimulationFrame));
				Resource resource = (Resource)(random.NextBool() ? 128 : 268435456);
				float marketPrice = EconomyUtils.GetMarketPrice(m_ResourceDatas[m_ResourcePrefabs[resource]]);
				float num5 = math.sqrt(buildingData.m_LotSize.x * buildingData.m_LotSize.y * buildingPropertyData.CountProperties()) * m_TemperatureUpkeep / (float)kUpdatesPerDay;
				Entity e = m_CommandBuffer.CreateEntity(unfilteredChunkIndex);
				if (random.NextInt(Mathf.RoundToInt(4000f * marketPrice)) < num4)
				{
					m_CommandBuffer.AddComponent(unfilteredChunkIndex, e, new GoodsDeliveryRequest
					{
						m_Amount = Math.Max(num3, 4000),
						m_Flags = (GoodsDeliveryFlags.BuildingUpkeep | GoodsDeliveryFlags.CommercialAllowed | GoodsDeliveryFlags.IndustrialAllowed | GoodsDeliveryFlags.ImportAllowed),
						m_Resource = resource,
						m_Target = target
					});
				}
				Building building = nativeArray4[i];
				if (m_Availabilities.HasBuffer(building.m_RoadEdge))
				{
					float availability = NetUtils.GetAvailability(m_Availabilities[building.m_RoadEdge], AvailableResource.WoodSupply, building.m_CurvePosition);
					float availability2 = NetUtils.GetAvailability(m_Availabilities[building.m_RoadEdge], AvailableResource.PetrochemicalsSupply, building.m_CurvePosition);
					float num6 = availability + availability2;
					if (num6 < 0.001f)
					{
						resource = (Resource)(random.NextBool() ? 64 : 65536);
					}
					else
					{
						resource = (Resource)((random.NextFloat(num6) <= availability) ? 64 : 65536);
						num3 = ((resource == Resource.Wood) ? 4000 : 800);
					}
					marketPrice = EconomyUtils.GetMarketPrice(m_ResourceDatas[m_ResourcePrefabs[resource]]);
					if (random.NextFloat((float)num3 * marketPrice) < num5)
					{
						e = m_CommandBuffer.CreateEntity(unfilteredChunkIndex);
						int num7 = Mathf.RoundToInt((float)num3 * marketPrice);
						m_CommandBuffer.AddComponent(unfilteredChunkIndex, e, new GoodsDeliveryRequest
						{
							m_Amount = num3,
							m_Flags = (GoodsDeliveryFlags.BuildingUpkeep | GoodsDeliveryFlags.CommercialAllowed | GoodsDeliveryFlags.IndustrialAllowed | GoodsDeliveryFlags.ImportAllowed),
							m_Resource = resource,
							m_Target = target
						});
						num += num7;
					}
				}
				int num8 = 0;
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					if (m_Resources.TryGetBuffer(dynamicBuffer[j].m_Renter, out var bufferData))
					{
						num8 = ((!m_Households.HasComponent(dynamicBuffer[j].m_Renter)) ? ((!m_OwnedVehicles.HasBuffer(dynamicBuffer[j].m_Renter)) ? (num8 + EconomyUtils.GetCompanyTotalWorth(bufferData, m_ResourcePrefabs, m_ResourceDatas)) : (num8 + EconomyUtils.GetCompanyTotalWorth(bufferData, m_OwnedVehicles[dynamicBuffer[j].m_Renter], m_LayoutElements, m_DeliveryTrucks, m_ResourcePrefabs, m_ResourceDatas))) : (num8 + EconomyUtils.GetResources(Resource.Money, bufferData)));
					}
				}
				BuildingCondition value = nativeArray3[i];
				int num9 = 0;
				//BetterEconomy.log.Info($"total wealth {num8}, money {money}, upkeep cost {num}, id:{target.Index}");
				if (num > num8)
				{
					// bug 1
					num9 = -math.max(1, m_BuildingConfigurationData.m_BuildingConditionDecrement * (int)math.pow(2f, (int)spawnableBuildingData.m_Level) * math.max(1, dynamicBuffer.Length));
				}
				else if (dynamicBuffer.Length > 0)
				{
					num9 = m_BuildingConfigurationData.m_BuildingConditionIncrement * (int)math.pow(2f, (int)spawnableBuildingData.m_Level) * math.max(1, dynamicBuffer.Length);
					int price = num / dynamicBuffer.Length;
					//		BetterEconomy.log.Info($"upkeep payment {price}, id:{target.Index}");
					for (int k = 0; k < dynamicBuffer.Length; k++)
					{
						m_UpkeepExpenseQueue.Enqueue(new UpkeepPayment
						{
							m_RenterEntity = dynamicBuffer[k].m_Renter,
							m_Price = price
						});
					}
				}
				if (m_DebugFastLeveling)
				{
					value.m_Condition = levelingCost;
				}
				else
				{
					value.m_Condition += num9;
				}
				if (value.m_Condition >= levelingCost)
				{
					m_LevelupQueue.Enqueue(nativeArray[i]);
					value.m_Condition -= levelingCost;
				}
				if (!m_Abandoned.HasComponent(nativeArray[i]) && !m_Destroyed.HasComponent(nativeArray[i]) && nativeArray3[i].m_Condition <= -num2 && !m_SignatureDatas.HasComponent(prefab))
				{
					m_LevelDownQueue.Enqueue(nativeArray[i]);
					value.m_Condition += levelingCost;
				}
				nativeArray3[i] = value;
			}
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	[BurstCompile]
	private struct UpkeepPaymentJob : IJob
	{
		public BufferLookup<Game.Economy.Resources> m_Resources;

		public NativeQueue<UpkeepPayment> m_UpkeepExpenseQueue;

		public void Execute()
		{
			UpkeepPayment item;
			while (m_UpkeepExpenseQueue.TryDequeue(out item))
			{
				if (m_Resources.HasBuffer(item.m_RenterEntity))
				{
					// bug 2
					EconomyUtils.AddResources(Resource.Money, -item.m_Price, m_Resources[item.m_RenterEntity]);
				}
			}
		}
	}

	[BurstCompile]
	private struct LeveldownJob : IJob
	{
		[ReadOnly]
		public ComponentLookup<PrefabRef> m_Prefabs;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildings;

		[ReadOnly]
		public ComponentLookup<BuildingData> m_BuildingDatas;

		public ComponentLookup<Building> m_Buildings;

		[ReadOnly]
		public ComponentLookup<ElectricityConsumer> m_ElectricityConsumers;

		[ReadOnly]
		public ComponentLookup<WaterConsumer> m_WaterConsumers;

		[ReadOnly]
		public ComponentLookup<GarbageProducer> m_GarbageProducers;

		[ReadOnly]
		public ComponentLookup<MailProducer> m_MailProducers;

		[ReadOnly]
		public ComponentLookup<BuildingPropertyData> m_BuildingPropertyDatas;

		[ReadOnly]
		public ComponentLookup<OfficeBuilding> m_OfficeBuilding;

		public NativeQueue<TriggerAction> m_TriggerBuffer;

		public ComponentLookup<CrimeProducer> m_CrimeProducers;

		public BufferLookup<Renter> m_Renters;

		[ReadOnly]
		public BuildingConfigurationData m_BuildingConfigurationData;

		public NativeQueue<Entity> m_LeveldownQueue;

		public EntityCommandBuffer m_CommandBuffer;

		public NativeQueue<Entity> m_UpdatedElectricityRoadEdges;

		public NativeQueue<Entity> m_UpdatedWaterPipeRoadEdges;

		public IconCommandBuffer m_IconCommandBuffer;

		public uint m_SimulationFrame;

		public void Execute()
		{
			Entity item;
			while (m_LeveldownQueue.TryDequeue(out item))
			{
				if (!m_Prefabs.HasComponent(item))
				{
					continue;
				}
				Entity prefab = m_Prefabs[item].m_Prefab;
				if (!m_SpawnableBuildings.HasComponent(prefab))
				{
					continue;
				}
				_ = m_SpawnableBuildings[prefab];
				_ = m_BuildingDatas[prefab];
				BuildingPropertyData buildingPropertyData = m_BuildingPropertyDatas[prefab];
				m_CommandBuffer.AddComponent(item, new Abandoned
				{
					m_AbandonmentTime = m_SimulationFrame
				});
				m_CommandBuffer.AddComponent(item, default(Updated));
				if (m_ElectricityConsumers.HasComponent(item))
				{
					m_CommandBuffer.RemoveComponent<ElectricityConsumer>(item);
					Entity roadEdge = m_Buildings[item].m_RoadEdge;
					if (roadEdge != Entity.Null)
					{
						m_UpdatedElectricityRoadEdges.Enqueue(roadEdge);
					}
				}
				if (m_WaterConsumers.HasComponent(item))
				{
					m_CommandBuffer.RemoveComponent<WaterConsumer>(item);
					Entity roadEdge2 = m_Buildings[item].m_RoadEdge;
					if (roadEdge2 != Entity.Null)
					{
						m_UpdatedWaterPipeRoadEdges.Enqueue(roadEdge2);
					}
				}
				if (m_GarbageProducers.HasComponent(item))
				{
					m_CommandBuffer.RemoveComponent<GarbageProducer>(item);
				}
				if (m_MailProducers.HasComponent(item))
				{
					m_CommandBuffer.RemoveComponent<MailProducer>(item);
				}
				if (m_CrimeProducers.HasComponent(item))
				{
					CrimeProducer crimeProducer = m_CrimeProducers[item];
					m_CommandBuffer.SetComponent(item, new CrimeProducer
					{
						m_Crime = crimeProducer.m_Crime * 2f,
						m_PatrolRequest = crimeProducer.m_PatrolRequest
					});
				}
				if (m_Renters.HasBuffer(item))
				{
					DynamicBuffer<Renter> dynamicBuffer = m_Renters[item];
					for (int num = dynamicBuffer.Length - 1; num >= 0; num--)
					{
						m_CommandBuffer.RemoveComponent<PropertyRenter>(dynamicBuffer[num].m_Renter);
						dynamicBuffer.RemoveAt(num);
					}
				}
				if ((m_Buildings[item].m_Flags & Game.Buildings.BuildingFlags.HighRentWarning) != 0)
				{
					Building value = m_Buildings[item];
					m_IconCommandBuffer.Remove(item, m_BuildingConfigurationData.m_HighRentNotification);
					value.m_Flags &= ~Game.Buildings.BuildingFlags.HighRentWarning;
					m_Buildings[item] = value;
				}
				m_IconCommandBuffer.Remove(item, IconPriority.Problem);
				m_IconCommandBuffer.Remove(item, IconPriority.FatalProblem);
				m_IconCommandBuffer.Add(item, m_BuildingConfigurationData.m_AbandonedNotification, IconPriority.FatalProblem);
				if (buildingPropertyData.CountProperties(AreaType.Commercial) > 0)
				{
					m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelDownCommercialBuilding, Entity.Null, item, item));
				}
				if (buildingPropertyData.CountProperties(AreaType.Industrial) > 0)
				{
					if (m_OfficeBuilding.HasComponent(prefab))
					{
						m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelDownOfficeBuilding, Entity.Null, item, item));
					}
					else
					{
						m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelDownIndustrialBuilding, Entity.Null, item, item));
					}
				}
			}
		}
	}

	[BurstCompile]
	private struct LevelupJob : IJob
	{
		private struct Iterator : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
		{
			public Bounds2 m_Bounds;

			public int2 m_LotSize;

			public float2 m_StartPosition;

			public float2 m_Right;

			public float2 m_Forward;

			public int m_MaxHeight;

			public ComponentLookup<Block> m_BlockData;

			public ComponentLookup<ValidArea> m_ValidAreaData;

			public BufferLookup<Cell> m_Cells;

			public bool Intersect(Bounds2 bounds)
			{
				return MathUtils.Intersect(bounds, m_Bounds);
			}

			public void Iterate(Bounds2 bounds, Entity blockEntity)
			{
				if (!MathUtils.Intersect(bounds, m_Bounds))
				{
					return;
				}
				ValidArea validArea = m_ValidAreaData[blockEntity];
				if (validArea.m_Area.y <= validArea.m_Area.x)
				{
					return;
				}
				Block block = m_BlockData[blockEntity];
				DynamicBuffer<Cell> dynamicBuffer = m_Cells[blockEntity];
				float2 startPosition = m_StartPosition;
				int2 @int = default(int2);
				@int.y = 0;
				while (@int.y < m_LotSize.y)
				{
					float2 position = startPosition;
					@int.x = 0;
					while (@int.x < m_LotSize.x)
					{
						int2 cellIndex = ZoneUtils.GetCellIndex(block, position);
						if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
						{
							int index = cellIndex.y * block.m_Size.x + cellIndex.x;
							Cell cell = dynamicBuffer[index];
							if ((cell.m_State & CellFlags.Visible) != 0)
							{
								m_MaxHeight = math.min(m_MaxHeight, cell.m_Height);
							}
						}
						position -= m_Right;
						@int.x++;
					}
					startPosition -= m_Forward;
					@int.y++;
				}
			}
		}

		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public ComponentTypeHandle<SpawnableBuildingData> m_SpawnableBuildingType;

		[ReadOnly]
		public ComponentTypeHandle<BuildingData> m_BuildingType;

		[ReadOnly]
		public ComponentTypeHandle<BuildingPropertyData> m_BuildingPropertyType;

		[ReadOnly]
		public ComponentTypeHandle<ObjectGeometryData> m_ObjectGeometryType;

		[ReadOnly]
		public SharedComponentTypeHandle<BuildingSpawnGroupData> m_BuildingSpawnGroupType;

		[ReadOnly]
		public ComponentLookup<Game.Objects.Transform> m_TransformData;

		[ReadOnly]
		public ComponentLookup<Block> m_BlockData;

		[ReadOnly]
		public ComponentLookup<ValidArea> m_ValidAreaData;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_Prefabs;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildings;

		[ReadOnly]
		public ComponentLookup<BuildingData> m_Buildings;

		[ReadOnly]
		public ComponentLookup<BuildingPropertyData> m_BuildingPropertyDatas;

		[ReadOnly]
		public ComponentLookup<OfficeBuilding> m_OfficeBuilding;

		[ReadOnly]
		public ComponentLookup<ZoneData> m_ZoneData;

		[ReadOnly]
		public BufferLookup<Cell> m_Cells;

		[ReadOnly]
		public BuildingConfigurationData m_BuildingConfigurationData;

		[ReadOnly]
		public NativeList<ArchetypeChunk> m_SpawnableBuildingChunks;

		[ReadOnly]
		public NativeQuadTree<Entity, Bounds2> m_ZoneSearchTree;

		[ReadOnly]
		public RandomSeed m_RandomSeed;

		public IconCommandBuffer m_IconCommandBuffer;

		public NativeQueue<Entity> m_LevelupQueue;

		public EntityCommandBuffer m_CommandBuffer;

		public NativeQueue<TriggerAction> m_TriggerBuffer;

		public NativeQueue<ZoneBuiltLevelUpdate> m_ZoneBuiltLevelQueue;

		public void Execute()
		{
			Unity.Mathematics.Random random = m_RandomSeed.GetRandom(0);
			Entity item;
			while (m_LevelupQueue.TryDequeue(out item))
			{
				Entity prefab = m_Prefabs[item].m_Prefab;
				if (!m_SpawnableBuildings.HasComponent(prefab))
				{
					continue;
				}
				SpawnableBuildingData spawnableBuildingData = m_SpawnableBuildings[prefab];
				BuildingData prefabBuildingData = m_Buildings[prefab];
				BuildingPropertyData buildingPropertyData = m_BuildingPropertyDatas[prefab];
				ZoneData zoneData = m_ZoneData[spawnableBuildingData.m_ZonePrefab];
				float maxHeight = GetMaxHeight(item, prefabBuildingData);
				Entity entity = SelectSpawnableBuilding(zoneData.m_ZoneType, spawnableBuildingData.m_Level + 1, prefabBuildingData.m_LotSize, maxHeight, prefabBuildingData.m_Flags & (Game.Prefabs.BuildingFlags.LeftAccess | Game.Prefabs.BuildingFlags.RightAccess), buildingPropertyData, ref random);
				if (!(entity != Entity.Null))
				{
					continue;
				}
				m_CommandBuffer.AddComponent(item, new UnderConstruction
				{
					m_NewPrefab = entity,
					m_Progress = byte.MaxValue
				});
				if (buildingPropertyData.CountProperties(AreaType.Residential) > 0)
				{
					m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpResidentialBuilding, Entity.Null, item, item));
				}
				if (buildingPropertyData.CountProperties(AreaType.Commercial) > 0)
				{
					m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpCommercialBuilding, Entity.Null, item, item));
				}
				if (buildingPropertyData.CountProperties(AreaType.Industrial) > 0)
				{
					if (m_OfficeBuilding.HasComponent(prefab))
					{
						m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpOfficeBuilding, Entity.Null, item, item));
					}
					else
					{
						m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpIndustrialBuilding, Entity.Null, item, item));
					}
				}
				m_ZoneBuiltLevelQueue.Enqueue(new ZoneBuiltLevelUpdate
				{
					m_Zone = spawnableBuildingData.m_ZonePrefab,
					m_FromLevel = spawnableBuildingData.m_Level,
					m_ToLevel = spawnableBuildingData.m_Level + 1,
					m_Squares = prefabBuildingData.m_LotSize.x * prefabBuildingData.m_LotSize.y
				});
				m_IconCommandBuffer.Add(item, m_BuildingConfigurationData.m_LevelUpNotification, IconPriority.Info, IconClusterLayer.Transaction);
			}
		}

		private Entity SelectSpawnableBuilding(ZoneType zoneType, int level, int2 lotSize, float maxHeight, Game.Prefabs.BuildingFlags accessFlags, BuildingPropertyData buildingPropertyData, ref Unity.Mathematics.Random random)
		{
			int num = 0;
			Entity result = Entity.Null;
			for (int i = 0; i < m_SpawnableBuildingChunks.Length; i++)
			{
				ArchetypeChunk archetypeChunk = m_SpawnableBuildingChunks[i];
				if (!archetypeChunk.GetSharedComponent(m_BuildingSpawnGroupType).m_ZoneType.Equals(zoneType))
				{
					continue;
				}
				NativeArray<Entity> nativeArray = archetypeChunk.GetNativeArray(m_EntityType);
				NativeArray<SpawnableBuildingData> nativeArray2 = archetypeChunk.GetNativeArray(ref m_SpawnableBuildingType);
				NativeArray<BuildingData> nativeArray3 = archetypeChunk.GetNativeArray(ref m_BuildingType);
				NativeArray<BuildingPropertyData> nativeArray4 = archetypeChunk.GetNativeArray(ref m_BuildingPropertyType);
				NativeArray<ObjectGeometryData> nativeArray5 = archetypeChunk.GetNativeArray(ref m_ObjectGeometryType);
				for (int j = 0; j < archetypeChunk.Count; j++)
				{
					SpawnableBuildingData spawnableBuildingData = nativeArray2[j];
					BuildingData buildingData = nativeArray3[j];
					BuildingPropertyData buildingPropertyData2 = nativeArray4[j];
					ObjectGeometryData objectGeometryData = nativeArray5[j];
					if (level == spawnableBuildingData.m_Level && lotSize.Equals(buildingData.m_LotSize) && objectGeometryData.m_Size.y <= maxHeight && (buildingData.m_Flags & (Game.Prefabs.BuildingFlags.LeftAccess | Game.Prefabs.BuildingFlags.RightAccess)) == accessFlags && buildingPropertyData.m_ResidentialProperties <= buildingPropertyData2.m_ResidentialProperties && buildingPropertyData.m_AllowedManufactured == buildingPropertyData2.m_AllowedManufactured && buildingPropertyData.m_AllowedSold == buildingPropertyData2.m_AllowedSold && buildingPropertyData.m_AllowedStored == buildingPropertyData2.m_AllowedStored)
					{
						int num2 = 100;
						num += num2;
						if (random.NextInt(num) < num2)
						{
							result = nativeArray[j];
						}
					}
				}
			}
			return result;
		}

		private float GetMaxHeight(Entity building, BuildingData prefabBuildingData)
		{
			Game.Objects.Transform transform = m_TransformData[building];
			float2 xz = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
			float2 xz2 = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
			float2 @float = xz * ((float)prefabBuildingData.m_LotSize.x * 0.5f - 0.5f);
			float2 float2 = xz2 * ((float)prefabBuildingData.m_LotSize.y * 0.5f - 0.5f);
			float2 float3 = math.abs(float2) + math.abs(@float);
			Iterator iterator = default(Iterator);
			iterator.m_Bounds = new Bounds2(transform.m_Position.xz - float3, transform.m_Position.xz + float3);
			iterator.m_LotSize = prefabBuildingData.m_LotSize;
			iterator.m_StartPosition = transform.m_Position.xz + float2 + @float;
			iterator.m_Right = xz;
			iterator.m_Forward = xz2;
			iterator.m_MaxHeight = int.MaxValue;
			iterator.m_BlockData = m_BlockData;
			iterator.m_ValidAreaData = m_ValidAreaData;
			iterator.m_Cells = m_Cells;
			Iterator iterator2 = iterator;
			m_ZoneSearchTree.Iterate(ref iterator2);
			return (float)iterator2.m_MaxHeight - transform.m_Position.y;
		}
	}

	private struct TypeHandle
	{
		public ComponentTypeHandle<BuildingCondition> __Game_Buildings_BuildingCondition_RW_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;

		[ReadOnly]
		public EntityTypeHandle __Unity_Entities_Entity_TypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<Building> __Game_Buildings_Building_RO_ComponentTypeHandle;

		[ReadOnly]
		public BufferTypeHandle<Renter> __Game_Buildings_Renter_RO_BufferTypeHandle;

		public SharedComponentTypeHandle<UpdateFrame> __Game_Simulation_UpdateFrame_SharedComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<ConsumptionData> __Game_Prefabs_ConsumptionData_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<ResourceAvailability> __Game_Net_ResourceAvailability_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<BuildingData> __Game_Prefabs_BuildingData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<BuildingPropertyData> __Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<CityModifier> __Game_City_CityModifier_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<SignatureBuildingData> __Game_Prefabs_SignatureBuildingData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Abandoned> __Game_Buildings_Abandoned_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Destroyed> __Game_Common_Destroyed_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<SpawnableBuildingData> __Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ZoneData> __Game_Prefabs_ZoneData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Household> __Game_Citizens_Household_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<OwnedVehicle> __Game_Vehicles_OwnedVehicle_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<LayoutElement> __Game_Vehicles_LayoutElement_RO_BufferLookup;

		[ReadOnly]
		public ComponentLookup<Game.Vehicles.DeliveryTruck> __Game_Vehicles_DeliveryTruck_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ResourceData> __Game_Prefabs_ResourceData_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RO_BufferLookup;

		[ReadOnly]
		public ComponentTypeHandle<SpawnableBuildingData> __Game_Prefabs_SpawnableBuildingData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<BuildingData> __Game_Prefabs_BuildingData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<BuildingPropertyData> __Game_Prefabs_BuildingPropertyData_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<ObjectGeometryData> __Game_Prefabs_ObjectGeometryData_RO_ComponentTypeHandle;

		public SharedComponentTypeHandle<BuildingSpawnGroupData> __Game_Prefabs_BuildingSpawnGroupData_SharedComponentTypeHandle;

		[ReadOnly]
		public ComponentLookup<Game.Objects.Transform> __Game_Objects_Transform_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Block> __Game_Zones_Block_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ValidArea> __Game_Zones_ValidArea_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<OfficeBuilding> __Game_Prefabs_OfficeBuilding_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<Cell> __Game_Zones_Cell_RO_BufferLookup;

		public ComponentLookup<Building> __Game_Buildings_Building_RW_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ElectricityConsumer> __Game_Buildings_ElectricityConsumer_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<GarbageProducer> __Game_Buildings_GarbageProducer_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<MailProducer> __Game_Buildings_MailProducer_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<WaterConsumer> __Game_Buildings_WaterConsumer_RO_ComponentLookup;

		public ComponentLookup<CrimeProducer> __Game_Buildings_CrimeProducer_RW_ComponentLookup;

		public BufferLookup<Renter> __Game_Buildings_Renter_RW_BufferLookup;

		public BufferLookup<Game.Economy.Resources> __Game_Economy_Resources_RW_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Game_Buildings_BuildingCondition_RW_ComponentTypeHandle = state.GetComponentTypeHandle<BuildingCondition>();
			__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle = state.GetComponentTypeHandle<PrefabRef>(isReadOnly: true);
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Buildings_Building_RO_ComponentTypeHandle = state.GetComponentTypeHandle<Building>(isReadOnly: true);
			__Game_Buildings_Renter_RO_BufferTypeHandle = state.GetBufferTypeHandle<Renter>(isReadOnly: true);
			__Game_Simulation_UpdateFrame_SharedComponentTypeHandle = state.GetSharedComponentTypeHandle<UpdateFrame>();
			__Game_Prefabs_ConsumptionData_RO_ComponentLookup = state.GetComponentLookup<ConsumptionData>(isReadOnly: true);
			__Game_Net_ResourceAvailability_RO_BufferLookup = state.GetBufferLookup<ResourceAvailability>(isReadOnly: true);
			__Game_Prefabs_BuildingData_RO_ComponentLookup = state.GetComponentLookup<BuildingData>(isReadOnly: true);
			__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup = state.GetComponentLookup<BuildingPropertyData>(isReadOnly: true);
			__Game_City_CityModifier_RO_BufferLookup = state.GetBufferLookup<CityModifier>(isReadOnly: true);
			__Game_Prefabs_SignatureBuildingData_RO_ComponentLookup = state.GetComponentLookup<SignatureBuildingData>(isReadOnly: true);
			__Game_Buildings_Abandoned_RO_ComponentLookup = state.GetComponentLookup<Abandoned>(isReadOnly: true);
			__Game_Common_Destroyed_RO_ComponentLookup = state.GetComponentLookup<Destroyed>(isReadOnly: true);
			__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup = state.GetComponentLookup<SpawnableBuildingData>(isReadOnly: true);
			__Game_Prefabs_ZoneData_RO_ComponentLookup = state.GetComponentLookup<ZoneData>(isReadOnly: true);
			__Game_Citizens_Household_RO_ComponentLookup = state.GetComponentLookup<Household>(isReadOnly: true);
			__Game_Vehicles_OwnedVehicle_RO_BufferLookup = state.GetBufferLookup<OwnedVehicle>(isReadOnly: true);
			__Game_Vehicles_LayoutElement_RO_BufferLookup = state.GetBufferLookup<LayoutElement>(isReadOnly: true);
			__Game_Vehicles_DeliveryTruck_RO_ComponentLookup = state.GetComponentLookup<Game.Vehicles.DeliveryTruck>(isReadOnly: true);
			__Game_Prefabs_ResourceData_RO_ComponentLookup = state.GetComponentLookup<ResourceData>(isReadOnly: true);
			__Game_Economy_Resources_RO_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>(isReadOnly: true);
			__Game_Prefabs_SpawnableBuildingData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<SpawnableBuildingData>(isReadOnly: true);
			__Game_Prefabs_BuildingData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<BuildingData>(isReadOnly: true);
			__Game_Prefabs_BuildingPropertyData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<BuildingPropertyData>(isReadOnly: true);
			__Game_Prefabs_ObjectGeometryData_RO_ComponentTypeHandle = state.GetComponentTypeHandle<ObjectGeometryData>(isReadOnly: true);
			__Game_Prefabs_BuildingSpawnGroupData_SharedComponentTypeHandle = state.GetSharedComponentTypeHandle<BuildingSpawnGroupData>();
			__Game_Objects_Transform_RO_ComponentLookup = state.GetComponentLookup<Game.Objects.Transform>(isReadOnly: true);
			__Game_Zones_Block_RO_ComponentLookup = state.GetComponentLookup<Block>(isReadOnly: true);
			__Game_Zones_ValidArea_RO_ComponentLookup = state.GetComponentLookup<ValidArea>(isReadOnly: true);
			__Game_Prefabs_PrefabRef_RO_ComponentLookup = state.GetComponentLookup<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_OfficeBuilding_RO_ComponentLookup = state.GetComponentLookup<OfficeBuilding>(isReadOnly: true);
			__Game_Zones_Cell_RO_BufferLookup = state.GetBufferLookup<Cell>(isReadOnly: true);
			__Game_Buildings_Building_RW_ComponentLookup = state.GetComponentLookup<Building>();
			__Game_Buildings_ElectricityConsumer_RO_ComponentLookup = state.GetComponentLookup<ElectricityConsumer>(isReadOnly: true);
			__Game_Buildings_GarbageProducer_RO_ComponentLookup = state.GetComponentLookup<GarbageProducer>(isReadOnly: true);
			__Game_Buildings_MailProducer_RO_ComponentLookup = state.GetComponentLookup<MailProducer>(isReadOnly: true);
			__Game_Buildings_WaterConsumer_RO_ComponentLookup = state.GetComponentLookup<WaterConsumer>(isReadOnly: true);
			__Game_Buildings_CrimeProducer_RW_ComponentLookup = state.GetComponentLookup<CrimeProducer>();
			__Game_Buildings_Renter_RW_BufferLookup = state.GetBufferLookup<Renter>();
			__Game_Economy_Resources_RW_BufferLookup = state.GetBufferLookup<Game.Economy.Resources>();
		}
	}

	public static readonly int kUpdatesPerDay = 16;

	public static readonly int kMaterialUpkeep = 4;

	private SimulationSystem m_SimulationSystem;

	private EndFrameBarrier m_EndFrameBarrier;

	private ResourceSystem m_ResourceSystem;

	private ClimateSystem m_ClimateSystem;

	private CitySystem m_CitySystem;

	private IconCommandSystem m_IconCommandSystem;

	private TriggerSystem m_TriggerSystem;

	private ZoneBuiltRequirementSystem m_ZoneBuiltRequirementSystemSystem;

	private Game.Zones.SearchSystem m_ZoneSearchSystem;

	private ElectricityRoadConnectionGraphSystem m_ElectricityRoadConnectionGraphSystem;

	private WaterPipeRoadConnectionGraphSystem m_WaterPipeRoadConnectionGraphSystem;

	private NativeQueue<UpkeepPayment> m_UpkeepExpenseQueue;

	private NativeQueue<Entity> m_LevelupQueue;

	private NativeQueue<Entity> m_LeveldownQueue;

	private EntityQuery m_BuildingPrefabGroup;

	private EntityQuery m_BuildingSettingsQuery;

	private EntityQuery m_BuildingGroup;

	public bool debugFastLeveling;

	private TypeHandle __TypeHandle;

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 262144 / (kUpdatesPerDay * 16);
	}

	public static float GetHeatingMultiplier(float temperature)
	{
		return math.max(0f, 15f - temperature);
	}

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
		m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
		m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
		m_ClimateSystem = base.World.GetOrCreateSystemManaged<ClimateSystem>();
		m_IconCommandSystem = base.World.GetOrCreateSystemManaged<IconCommandSystem>();
		m_TriggerSystem = base.World.GetOrCreateSystemManaged<TriggerSystem>();
		m_CitySystem = base.World.GetOrCreateSystemManaged<CitySystem>();
		m_ZoneBuiltRequirementSystemSystem = base.World.GetOrCreateSystemManaged<ZoneBuiltRequirementSystem>();
		m_ZoneSearchSystem = base.World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
		m_ElectricityRoadConnectionGraphSystem = base.World.GetOrCreateSystemManaged<ElectricityRoadConnectionGraphSystem>();
		m_WaterPipeRoadConnectionGraphSystem = base.World.GetOrCreateSystemManaged<WaterPipeRoadConnectionGraphSystem>();
		m_UpkeepExpenseQueue = new NativeQueue<UpkeepPayment>(Allocator.Persistent);
		m_BuildingSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
		m_LevelupQueue = new NativeQueue<Entity>(Allocator.Persistent);
		m_LeveldownQueue = new NativeQueue<Entity>(Allocator.Persistent);
		m_BuildingGroup = GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[3]
			{
				ComponentType.ReadOnly<BuildingCondition>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.ReadOnly<UpdateFrame>()
			},
			Any = new ComponentType[0],
			None = new ComponentType[4]
			{
				ComponentType.ReadOnly<Abandoned>(),
				ComponentType.ReadOnly<Destroyed>(),
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Temp>()
			}
		});
		m_BuildingPrefabGroup = GetEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<BuildingSpawnGroupData>(), ComponentType.ReadOnly<PrefabData>());
		RequireForUpdate(m_BuildingGroup);
		RequireForUpdate(m_BuildingSettingsQuery);
	}

	[Preserve]
	protected override void OnDestroy()
	{
		base.OnDestroy();
		m_UpkeepExpenseQueue.Dispose();
		m_LevelupQueue.Dispose();
		m_LeveldownQueue.Dispose();
	}

	[Preserve]
	protected override void OnUpdate()
	{
		uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16);
		BuildingConfigurationData singleton = m_BuildingSettingsQuery.GetSingleton<BuildingConfigurationData>();
		__TypeHandle.__Game_Economy_Resources_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Vehicles_DeliveryTruck_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Vehicles_LayoutElement_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Citizens_Household_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ZoneData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Common_Destroyed_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Abandoned_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SignatureBuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_City_CityModifier_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Net_ResourceAvailability_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ConsumptionData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Renter_RO_BufferTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Building_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_BuildingCondition_RW_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		BuildingUpkeepJob buildingUpkeepJob = default(BuildingUpkeepJob);
		buildingUpkeepJob.m_ConditionType = __TypeHandle.__Game_Buildings_BuildingCondition_RW_ComponentTypeHandle;
		buildingUpkeepJob.m_PrefabType = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentTypeHandle;
		buildingUpkeepJob.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		buildingUpkeepJob.m_BuildingType = __TypeHandle.__Game_Buildings_Building_RO_ComponentTypeHandle;
		buildingUpkeepJob.m_RenterType = __TypeHandle.__Game_Buildings_Renter_RO_BufferTypeHandle;
		buildingUpkeepJob.m_UpdateFrameType = __TypeHandle.__Game_Simulation_UpdateFrame_SharedComponentTypeHandle;
		buildingUpkeepJob.m_ConsumptionDatas = __TypeHandle.__Game_Prefabs_ConsumptionData_RO_ComponentLookup;
		buildingUpkeepJob.m_Availabilities = __TypeHandle.__Game_Net_ResourceAvailability_RO_BufferLookup;
		buildingUpkeepJob.m_BuildingDatas = __TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup;
		buildingUpkeepJob.m_BuildingPropertyDatas = __TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;
		buildingUpkeepJob.m_CityModifierBufs = __TypeHandle.__Game_City_CityModifier_RO_BufferLookup;
		buildingUpkeepJob.m_SignatureDatas = __TypeHandle.__Game_Prefabs_SignatureBuildingData_RO_ComponentLookup;
		buildingUpkeepJob.m_Abandoned = __TypeHandle.__Game_Buildings_Abandoned_RO_ComponentLookup;
		buildingUpkeepJob.m_Destroyed = __TypeHandle.__Game_Common_Destroyed_RO_ComponentLookup;
		buildingUpkeepJob.m_SpawnableBuildingDatas = __TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;
		buildingUpkeepJob.m_ZoneDatas = __TypeHandle.__Game_Prefabs_ZoneData_RO_ComponentLookup;
		buildingUpkeepJob.m_Households = __TypeHandle.__Game_Citizens_Household_RO_ComponentLookup;
		buildingUpkeepJob.m_OwnedVehicles = __TypeHandle.__Game_Vehicles_OwnedVehicle_RO_BufferLookup;
		buildingUpkeepJob.m_LayoutElements = __TypeHandle.__Game_Vehicles_LayoutElement_RO_BufferLookup;
		buildingUpkeepJob.m_DeliveryTrucks = __TypeHandle.__Game_Vehicles_DeliveryTruck_RO_ComponentLookup;
		buildingUpkeepJob.m_City = m_CitySystem.City;
		buildingUpkeepJob.m_ResourcePrefabs = m_ResourceSystem.GetPrefabs();
		buildingUpkeepJob.m_ResourceDatas = __TypeHandle.__Game_Prefabs_ResourceData_RO_ComponentLookup;
		buildingUpkeepJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RO_BufferLookup;
		buildingUpkeepJob.m_BuildingConfigurationData = singleton;
		buildingUpkeepJob.m_UpdateFrameIndex = updateFrame;
		buildingUpkeepJob.m_SimulationFrame = m_SimulationSystem.frameIndex;
		buildingUpkeepJob.m_UpkeepExpenseQueue = m_UpkeepExpenseQueue.AsParallelWriter();
		buildingUpkeepJob.m_LevelupQueue = m_LevelupQueue.AsParallelWriter();
		buildingUpkeepJob.m_LevelDownQueue = m_LeveldownQueue.AsParallelWriter();
		buildingUpkeepJob.m_DebugFastLeveling = debugFastLeveling;
		buildingUpkeepJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter();
		buildingUpkeepJob.m_TemperatureUpkeep = GetHeatingMultiplier(m_ClimateSystem.temperature);
		BuildingUpkeepJob jobData = buildingUpkeepJob;
		base.Dependency = JobChunkExtensions.ScheduleParallel(jobData, m_BuildingGroup, base.Dependency);
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		m_ResourceSystem.AddPrefabsReader(base.Dependency);
		__TypeHandle.__Game_Zones_Cell_RO_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ZoneData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_OfficeBuilding_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Zones_ValidArea_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Zones_Block_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Objects_Transform_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingSpawnGroupData_SharedComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_ObjectGeometryData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentTypeHandle.Update(ref base.CheckedStateRef);
		__TypeHandle.__Unity_Entities_Entity_TypeHandle.Update(ref base.CheckedStateRef);
		LevelupJob levelupJob = default(LevelupJob);
		levelupJob.m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle;
		levelupJob.m_SpawnableBuildingType = __TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentTypeHandle;
		levelupJob.m_BuildingType = __TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentTypeHandle;
		levelupJob.m_BuildingPropertyType = __TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentTypeHandle;
		levelupJob.m_ObjectGeometryType = __TypeHandle.__Game_Prefabs_ObjectGeometryData_RO_ComponentTypeHandle;
		levelupJob.m_BuildingSpawnGroupType = __TypeHandle.__Game_Prefabs_BuildingSpawnGroupData_SharedComponentTypeHandle;
		levelupJob.m_TransformData = __TypeHandle.__Game_Objects_Transform_RO_ComponentLookup;
		levelupJob.m_BlockData = __TypeHandle.__Game_Zones_Block_RO_ComponentLookup;
		levelupJob.m_ValidAreaData = __TypeHandle.__Game_Zones_ValidArea_RO_ComponentLookup;
		levelupJob.m_Prefabs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
		levelupJob.m_SpawnableBuildings = __TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;
		levelupJob.m_Buildings = __TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup;
		levelupJob.m_BuildingPropertyDatas = __TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;
		levelupJob.m_OfficeBuilding = __TypeHandle.__Game_Prefabs_OfficeBuilding_RO_ComponentLookup;
		levelupJob.m_ZoneData = __TypeHandle.__Game_Prefabs_ZoneData_RO_ComponentLookup;
		levelupJob.m_Cells = __TypeHandle.__Game_Zones_Cell_RO_BufferLookup;
		levelupJob.m_BuildingConfigurationData = singleton;
		levelupJob.m_SpawnableBuildingChunks = m_BuildingPrefabGroup.ToArchetypeChunkListAsync(base.World.UpdateAllocator.ToAllocator, out var outJobHandle);
		levelupJob.m_ZoneSearchTree = m_ZoneSearchSystem.GetSearchTree(readOnly: true, out var dependencies);
		levelupJob.m_RandomSeed = RandomSeed.Next();
		levelupJob.m_IconCommandBuffer = m_IconCommandSystem.CreateCommandBuffer();
		levelupJob.m_LevelupQueue = m_LevelupQueue;
		levelupJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
		levelupJob.m_TriggerBuffer = m_TriggerSystem.CreateActionBuffer();
		levelupJob.m_ZoneBuiltLevelQueue = m_ZoneBuiltRequirementSystemSystem.GetZoneBuiltLevelQueue(out var deps);
		LevelupJob jobData2 = levelupJob;
		base.Dependency = IJobExtensions.Schedule(jobData2, JobUtils.CombineDependencies(base.Dependency, outJobHandle, dependencies, deps));
		m_ZoneSearchSystem.AddSearchTreeReader(base.Dependency);
		m_ZoneBuiltRequirementSystemSystem.AddWriter(base.Dependency);
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		m_TriggerSystem.AddActionBufferWriter(base.Dependency);
		__TypeHandle.__Game_Buildings_Renter_RW_BufferLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_CrimeProducer_RW_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_OfficeBuilding_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_WaterConsumer_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_MailProducer_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_GarbageProducer_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_ElectricityConsumer_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Buildings_Building_RW_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		__TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup.Update(ref base.CheckedStateRef);
		LeveldownJob leveldownJob = default(LeveldownJob);
		leveldownJob.m_BuildingDatas = __TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup;
		leveldownJob.m_Prefabs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
		leveldownJob.m_SpawnableBuildings = __TypeHandle.__Game_Prefabs_SpawnableBuildingData_RO_ComponentLookup;
		leveldownJob.m_Buildings = __TypeHandle.__Game_Buildings_Building_RW_ComponentLookup;
		leveldownJob.m_ElectricityConsumers = __TypeHandle.__Game_Buildings_ElectricityConsumer_RO_ComponentLookup;
		leveldownJob.m_GarbageProducers = __TypeHandle.__Game_Buildings_GarbageProducer_RO_ComponentLookup;
		leveldownJob.m_MailProducers = __TypeHandle.__Game_Buildings_MailProducer_RO_ComponentLookup;
		leveldownJob.m_WaterConsumers = __TypeHandle.__Game_Buildings_WaterConsumer_RO_ComponentLookup;
		leveldownJob.m_BuildingPropertyDatas = __TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;
		leveldownJob.m_OfficeBuilding = __TypeHandle.__Game_Prefabs_OfficeBuilding_RO_ComponentLookup;
		leveldownJob.m_TriggerBuffer = m_TriggerSystem.CreateActionBuffer();
		leveldownJob.m_CrimeProducers = __TypeHandle.__Game_Buildings_CrimeProducer_RW_ComponentLookup;
		leveldownJob.m_Renters = __TypeHandle.__Game_Buildings_Renter_RW_BufferLookup;
		leveldownJob.m_BuildingConfigurationData = singleton;
		leveldownJob.m_LeveldownQueue = m_LeveldownQueue;
		leveldownJob.m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
		leveldownJob.m_UpdatedElectricityRoadEdges = m_ElectricityRoadConnectionGraphSystem.GetEdgeUpdateQueue(out var deps2);
		leveldownJob.m_UpdatedWaterPipeRoadEdges = m_WaterPipeRoadConnectionGraphSystem.GetEdgeUpdateQueue(out var deps3);
		leveldownJob.m_IconCommandBuffer = m_IconCommandSystem.CreateCommandBuffer();
		leveldownJob.m_SimulationFrame = m_SimulationSystem.frameIndex;
		LeveldownJob jobData3 = leveldownJob;
		base.Dependency = IJobExtensions.Schedule(jobData3, JobHandle.CombineDependencies(base.Dependency, deps2, deps3));
		m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
		m_ElectricityRoadConnectionGraphSystem.AddQueueWriter(base.Dependency);
		m_IconCommandSystem.AddCommandBufferWriter(base.Dependency);
		m_TriggerSystem.AddActionBufferWriter(base.Dependency);
		__TypeHandle.__Game_Economy_Resources_RW_BufferLookup.Update(ref base.CheckedStateRef);
		UpkeepPaymentJob upkeepPaymentJob = default(UpkeepPaymentJob);
		upkeepPaymentJob.m_Resources = __TypeHandle.__Game_Economy_Resources_RW_BufferLookup;
		upkeepPaymentJob.m_UpkeepExpenseQueue = m_UpkeepExpenseQueue;
		UpkeepPaymentJob jobData4 = upkeepPaymentJob;
		base.Dependency = IJobExtensions.Schedule(jobData4, base.Dependency);
	}

	public void DebugLevelUp(Entity building, ComponentLookup<BuildingCondition> conditions, ComponentLookup<SpawnableBuildingData> spawnables, ComponentLookup<PrefabRef> prefabRefs, ComponentLookup<ZoneData> zoneDatas, ComponentLookup<BuildingPropertyData> propertyDatas)
	{
		if (!conditions.HasComponent(building) || !prefabRefs.HasComponent(building))
		{
			return;
		}
		_ = conditions[building];
		Entity prefab = prefabRefs[building].m_Prefab;
		if (spawnables.HasComponent(prefab) && propertyDatas.HasComponent(prefab))
		{
			SpawnableBuildingData spawnableBuildingData = spawnables[prefab];
			if (zoneDatas.HasComponent(spawnableBuildingData.m_ZonePrefab))
			{
				_ = zoneDatas[spawnableBuildingData.m_ZonePrefab];
				m_LevelupQueue.Enqueue(building);
			}
		}
	}

	public void DebugLevelDown(Entity building, ComponentLookup<BuildingCondition> conditions, ComponentLookup<SpawnableBuildingData> spawnables, ComponentLookup<PrefabRef> prefabRefs, ComponentLookup<ZoneData> zoneDatas, ComponentLookup<BuildingPropertyData> propertyDatas)
	{
		if (!conditions.HasComponent(building) || !prefabRefs.HasComponent(building))
		{
			return;
		}
		BuildingCondition value = conditions[building];
		Entity prefab = prefabRefs[building].m_Prefab;
		if (spawnables.HasComponent(prefab) && propertyDatas.HasComponent(prefab))
		{
			SpawnableBuildingData spawnableBuildingData = spawnables[prefab];
			if (zoneDatas.HasComponent(spawnableBuildingData.m_ZonePrefab))
			{
				int levelingCost = BuildingUtils.GetLevelingCost(zoneDatas[spawnableBuildingData.m_ZonePrefab].m_AreaType, propertyDatas[prefab], spawnableBuildingData.m_Level, base.EntityManager.GetBuffer<CityModifier>(m_CitySystem.City, isReadOnly: true));
				value.m_Condition = -3 * levelingCost / 2;
				conditions[building] = value;
				m_LeveldownQueue.Enqueue(building);
			}
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
	public ModifiedBuildingUpkeepSystem()
	{
	}
}
