﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using UnityEngine;

namespace TrafficManager
{
    public class CustomPathFind : PathFind
    {
		const float FOO_FACTOR = 0.003921569f;

		const int PATHFINDFLAG_NOSTARTINGNODE = 8;

        private struct BufferItem
        {
            public PathUnit.Position m_position;
            public float m_comparisonValue;
            public float m_methodDistance;
            public uint m_laneID;
            public NetInfo.Direction m_direction;
            public NetInfo.LaneType m_lanesUsed;
        }

        //Expose the private fields
        FieldInfo fieldpathUnits;
        FieldInfo fieldQueueFirst;
        FieldInfo fieldQueueLast;
        FieldInfo fieldQueueLock;
        FieldInfo fieldCalculating;
        FieldInfo fieldTerminated;
        FieldInfo fieldPathFindThread;

        private Array32<PathUnit> m_pathUnits
        {
            get { return fieldpathUnits.GetValue(this) as Array32<PathUnit>; }
            set { fieldpathUnits.SetValue(this, value); }
        }

        private uint m_queueFirst
        {
            get { return (uint)fieldQueueFirst.GetValue(this); }
            set { fieldQueueFirst.SetValue(this, value); }
        }

        private uint m_queueLast
        {
            get { return (uint)fieldQueueLast.GetValue(this); }
            set { fieldQueueLast.SetValue(this, value); }
        }

        private uint m_calculating
        {
            get { return (uint)fieldCalculating.GetValue(this); }
            set { fieldCalculating.SetValue(this, value); }
        }

        private object m_queueLock
        {
            get { return fieldQueueLock.GetValue(this); }
            set { fieldQueueLock.SetValue(this, value); }
        }

        private object m_bufferLock;
        private Thread m_pathFindThread
        {
            get { return (Thread)fieldPathFindThread.GetValue(this); }
            set { fieldPathFindThread.SetValue(this, value); }
        }

        private bool m_terminated
        {
            get { return (bool)fieldTerminated.GetValue(this); }
            set { fieldTerminated.SetValue(this, value); }
        }
        private int m_bufferMinPos;
        private int m_bufferMaxPos;
        private uint[] m_laneLocation;
        private PathUnit.Position[] m_laneTarget;
        private CustomPathFind.BufferItem[] m_buffer;
        private int[] m_bufferMin;
        private int[] m_bufferMax;
        private float m_maxLength;
        private uint m_startLaneA;
        private uint m_startLaneB;
        private uint m_endLaneA;
        private uint m_endLaneB;
        private uint m_vehicleLane;
        private byte m_startOffsetA;
        private byte m_startOffsetB;
        private byte m_vehicleOffset;
        private bool m_isHeavyVehicle;
        private bool m_ignoreBlocked;
        private bool m_stablePath;
        private TrafficRoadRestrictions.VehicleType m_vehicleType;
        private Randomizer m_pathRandomizer;
        private uint m_pathFindIndex;
        private NetInfo.LaneType m_laneTypes;
        private VehicleInfo.VehicleType m_vehicleTypes;

        private void Awake()
        {
            Type stockPathFindType = typeof(PathFind);
            BindingFlags fieldFlags = BindingFlags.NonPublic | BindingFlags.Instance;


            fieldpathUnits = stockPathFindType.GetField("m_pathUnits", fieldFlags);
            fieldQueueFirst = stockPathFindType.GetField("m_queueFirst", fieldFlags);
            fieldQueueLast = stockPathFindType.GetField("m_queueLast", fieldFlags);
            fieldQueueLock = stockPathFindType.GetField("m_queueLock", fieldFlags);
            fieldTerminated = stockPathFindType.GetField("m_terminated", fieldFlags);
            fieldCalculating = stockPathFindType.GetField("m_calculating", fieldFlags);
            fieldPathFindThread = stockPathFindType.GetField("m_pathFindThread", fieldFlags);

            this.m_buffer = new CustomPathFind.BufferItem[65536];
            this.m_bufferLock = PathManager.instance.m_bufferLock;
            this.m_pathUnits = PathManager.instance.m_pathUnits;
            this.m_queueLock = new object();
            this.m_laneLocation = new uint[262144];
            this.m_laneTarget = new PathUnit.Position[262144];
            this.m_bufferMin = new int[1024];
            this.m_bufferMax = new int[1024];

            this.m_pathfindProfiler = new ThreadProfiler();

            this.m_pathFindThread = new Thread(new ThreadStart(this.PathFindThread));
            this.m_pathFindThread.Name = "Pathfind";
            this.m_pathFindThread.Start();
            if (!this.m_pathFindThread.IsAlive)
            {
                CODebugBase<LogChannel>.Error(LogChannel.Core, "Path find thread failed to start!");
            }

        }

        //Unmodified from stock
        private void OnDestroy()
        {
            while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
            {
            }
            try
            {
                this.m_terminated = true;
                Monitor.PulseAll(this.m_queueLock);
            }
            finally
            {
                Monitor.Exit(this.m_queueLock);
            }
        }

        //Stock code
        public new bool CalculatePath(uint unit, bool skipQueue)
        {
            if (Singleton<PathManager>.instance.AddPathReference(unit))
            {
                while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                {
                }
                try
                {
                    if (skipQueue)
                    {
                        if (this.m_queueLast == 0u)
                        {
                            this.m_queueLast = unit;
                        }
                        else
                        {
                            this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_nextPathUnit = this.m_queueFirst;
                        }
                        this.m_queueFirst = unit;
                    }
                    else
                    {
                        if (this.m_queueLast == 0u)
                        {
                            this.m_queueFirst = unit;
                        }
                        else
                        {
                            this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_queueLast)].m_nextPathUnit = unit;
                        }
                        this.m_queueLast = unit;
                    }
                    PathUnit[] expr_BD_cp_0 = this.m_pathUnits.m_buffer;
                    UIntPtr expr_BD_cp_1 = (UIntPtr)unit;
                    expr_BD_cp_0[(int)expr_BD_cp_1].m_pathFindFlags = (byte)(expr_BD_cp_0[(int)expr_BD_cp_1].m_pathFindFlags | 1);
                    this.m_queuedPathFindCount++;
                    Monitor.Pulse(this.m_queueLock);
                }
                finally
                {
                    Monitor.Exit(this.m_queueLock);
                }
                return true;
            }
            return false;
        }

        //Stock code
        public new void WaitForAllPaths()
        {
            while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
            {
            }
            try
            {
                while ((this.m_queueFirst != 0u || this.m_calculating != 0u) && !this.m_terminated)
                {
                    Monitor.Wait(this.m_queueLock);
                }
            }
            finally
            {
                Monitor.Exit(this.m_queueLock);
            }
        }

		void assignPathFindingParameters (uint unit, PathUnit pathUnit)
		{
			this.m_laneTypes = (NetInfo.LaneType)pathUnit.m_laneTypes;
			this.m_vehicleTypes = (VehicleInfo.VehicleType)pathUnit.m_vehicleTypes;
			this.m_maxLength = pathUnit.m_length;
			this.m_pathFindIndex = (this.m_pathFindIndex + 1u & 32767u);
			this.m_pathRandomizer = new Randomizer (unit);
			this.m_isHeavyVehicle = ((pathUnit.m_simulationFlags & 16) != 0);
			this.m_ignoreBlocked = ((pathUnit.m_simulationFlags & 32) != 0);
			this.m_stablePath = ((pathUnit.m_simulationFlags & 64) != 0);
			//this.m_vehicleType =
			//    TrafficRoadRestrictions.vehicleType(this.m_pathUnits.m_buffer[(int) ((UIntPtr) unit)].m_simulationFlags);
		}
			
		private CustomPathFind.BufferItem createStartPosition (PathUnit.Position dataPosition, ref uint laneID, ref byte offset, int num, int num2)
		{				
			offset = (dataPosition.m_segment != 0 && num >= num2) ? dataPosition.m_offset : (byte)0;
			return createEndPosition(dataPosition, ref laneID, num, num2);
		}

		private CustomPathFind.BufferItem createEndPosition (PathUnit.Position dataPosition, ref uint laneID, int num, int num2)
		{
			CustomPathFind.BufferItem position = default(CustomPathFind.BufferItem);	
			if (dataPosition.m_segment != 0 && num >= num2) {
				laneID = PathManager.GetLaneID (dataPosition);
				position.m_laneID = laneID;
				position.m_position = dataPosition;
				this.GetLaneDirection (dataPosition, out position.m_direction, out position.m_lanesUsed);
				position.m_methodDistance = 0f;		
				position.m_comparisonValue = 0f;
				return position;
			}
			else {
				laneID = 0u;
				return position;
			}
		}

		private void assignVehicleInformation (int num2, PathUnit.Position m_position11)
		{
			if (m_position11.m_segment != 0 && num2 >= 1) {
				this.m_vehicleLane = PathManager.GetLaneID (m_position11);
				this.m_vehicleOffset = m_position11.m_offset;
			}
			else {
				this.m_vehicleLane = 0u;
				this.m_vehicleOffset = 0;
			}
		}

		static bool isStartPosition (BufferItem currentItem, BufferItem startPosition, byte startOffset)
		{
			if (currentItem.m_position.m_segment == startPosition.m_position.m_segment && currentItem.m_position.m_lane == startPosition.m_position.m_lane) {
				if ((byte)(currentItem.m_direction & NetInfo.Direction.Forward) != 0 && currentItem.m_position.m_offset >= startOffset) {
					return true;
				}
				if ((byte)(currentItem.m_direction & NetInfo.Direction.Backward) != 0 && currentItem.m_position.m_offset <= startOffset) {
					return true;
				}
			}
			return false;
		}

		static bool isItemAtPosition (BufferItem item, PathUnit.Position position)
		{
			return position.m_segment == item.m_position.m_segment && position.m_lane == item.m_position.m_lane && position.m_offset == item.m_position.m_offset;
		}

        private void PathFindImplementation(uint unit, ref PathUnit data)
        {
            NetManager instance = Singleton<NetManager>.instance;
			PathUnit pathUnit = this.m_pathUnits.m_buffer [unit];
            
			assignPathFindingParameters (unit, pathUnit);

            int num = (pathUnit.m_positionCount & 15);
            int num2 = pathUnit.m_positionCount >> 4;
            
			CustomPathFind.BufferItem startPosA = createStartPosition (data.m_position00, ref m_startLaneA, ref m_startOffsetA, num, 1);
			CustomPathFind.BufferItem startPosB = createStartPosition (data.m_position02, ref m_startLaneB, ref m_startOffsetB, num, 3);
			CustomPathFind.BufferItem endPosA = createEndPosition (data.m_position01, ref m_endLaneA, num, 2);
			CustomPathFind.BufferItem endPosB = createEndPosition (data.m_position03, ref m_endLaneB, num, 4);

			var m_position11 = data.m_position11;
            assignVehicleInformation (num2, data.m_position11);
            CustomPathFind.BufferItem goalItem = default(CustomPathFind.BufferItem);
            byte startOffset = 0;
            this.m_bufferMinPos = 0;
            this.m_bufferMaxPos = -1;
            if (this.m_pathFindIndex == 0u)
            {
                uint num3 = 4294901760u;
                for (int i = 0; i < 262144; i++)
                {
                    this.m_laneLocation[i] = num3;
                }
            }
            for (int j = 0; j < 1024; j++)
            {
                this.m_bufferMin[j] = 0;
                this.m_bufferMax[j] = -1;
            }
            if (endPosA.m_position.m_segment != 0)
            {
                this.m_bufferMax[0]++;
                this.m_buffer[++this.m_bufferMaxPos] = endPosA;
            }
            if (endPosB.m_position.m_segment != 0)
            {
                this.m_bufferMax[0]++;
                this.m_buffer[++this.m_bufferMaxPos] = endPosB;
            }
            bool isStartingNode = false;
            while (this.m_bufferMinPos <= this.m_bufferMaxPos)
            {
                int num4 = this.m_bufferMin[this.m_bufferMinPos];
                int num5 = this.m_bufferMax[this.m_bufferMinPos];
                if (num4 > num5)
                {
                    this.m_bufferMinPos++;
                }
                else
                {
                    this.m_bufferMin[this.m_bufferMinPos] = num4 + 1;
                    CustomPathFind.BufferItem currentItem = this.m_buffer[(this.m_bufferMinPos << 6) + num4];

					if(isStartPosition(currentItem, startPosA, m_startOffsetA)){
						goalItem = currentItem;
						startOffset = this.m_startOffsetA;
						isStartingNode = true;
						break;
					}
					if(isStartPosition(currentItem, startPosB, m_startOffsetB)){
						goalItem = currentItem;
						startOffset = this.m_startOffsetB;
						isStartingNode = true;
						break;
					}
                    
					if ((byte)(currentItem.m_direction & NetInfo.Direction.Forward) != 0)
                    {
                        ushort startNode = instance.m_segments.m_buffer[(int)currentItem.m_position.m_segment].m_startNode;
                        this.advanceToNextNode(currentItem, startNode, ref instance.m_nodes.m_buffer[(int)startNode], 0, false, ref data);
                    }
                    if ((byte)(currentItem.m_direction & NetInfo.Direction.Backward) != 0)
                    {
                        ushort endNode = instance.m_segments.m_buffer[(int)currentItem.m_position.m_segment].m_endNode;
                        this.advanceToNextNode(currentItem, endNode, ref instance.m_nodes.m_buffer[(int)endNode], 255, false, ref data);
                    }

					int overloadProtection = 0;
                    ushort possibleNextNode = instance.m_lanes.m_buffer[(int)(currentItem.m_laneID)].m_nodes;
					if (possibleNextNode != 0)
                    {
                        ushort startNode2 = instance.m_segments.m_buffer[(int)currentItem.m_position.m_segment].m_startNode;
                        ushort endNode2 = instance.m_segments.m_buffer[(int)currentItem.m_position.m_segment].m_endNode;
						bool isSegmentNodeDisabled = ((instance.m_nodes.m_buffer[(int)startNode2].m_flags | instance.m_nodes.m_buffer[(int)endNode2].m_flags) & NetNode.Flags.Disabled) != NetNode.Flags.None;
                        while (possibleNextNode != 0)
                        {
                            NetInfo.Direction direction = NetInfo.Direction.None;
                            byte laneOffset = instance.m_nodes.m_buffer[(int)possibleNextNode].m_laneOffset;
                            if (laneOffset <= currentItem.m_position.m_offset)
                            {
                                direction |= NetInfo.Direction.Forward;
                            }
                            if (laneOffset >= currentItem.m_position.m_offset)
                            {
                                direction |= NetInfo.Direction.Backward;
                            }
							bool isLaneAndCurrentItemDirectionEqual = (byte)(currentItem.m_direction & direction) != 0;
							bool isPossibleNextNodeDisabled = (instance.m_nodes.m_buffer [(int)possibleNextNode].m_flags & NetNode.Flags.Disabled) == NetNode.Flags.None;
							if (isLaneAndCurrentItemDirectionEqual && (!isSegmentNodeDisabled || !isPossibleNextNodeDisabled))
                            {
                                this.advanceToNextNode(currentItem, possibleNextNode, ref instance.m_nodes.m_buffer[(int)possibleNextNode], laneOffset, true, ref data);
                            }
                            possibleNextNode = instance.m_nodes.m_buffer[(int)possibleNextNode].m_nextLaneNode;
                            if (++overloadProtection == 32768)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            if (!isStartingNode)
            {
				PathUnit[] pathUnits = this.m_pathUnits.m_buffer;
				pathUnits[(int)unit].m_pathFindFlags = (byte)(pathUnits [(int)unit].m_pathFindFlags | PATHFINDFLAG_NOSTARTINGNODE);
                return;
            }
			float pathLength = goalItem.m_comparisonValue * this.m_maxLength;
            pathUnit.m_length = pathLength;
			uint currentPathUnit = unit;
			int positionIndex = 0;
            int num11 = 0;
            PathUnit.Position position = goalItem.m_position;

			// Adds a piece of path if position is not at the start
			if (!isItemAtPosition (endPosA, position) && !isItemAtPosition(endPosB, position))
            {
                if (startOffset != position.m_offset)
                {
                    PathUnit.Position position2 = position;
                    position2.m_offset = startOffset;
                    this.m_pathUnits.m_buffer[(int)(currentPathUnit)].SetPosition(positionIndex++, position2);
                }
                this.m_pathUnits.m_buffer[(int)(currentPathUnit)].SetPosition(positionIndex++, position);
                position = this.m_laneTarget[(int)(goalItem.m_laneID)];
            }

            for (int k = 0; k < 262144; k++)
            {
                this.m_pathUnits.m_buffer[(int)(currentPathUnit)].SetPosition(positionIndex++, position);
				if (isItemAtPosition (endPosA, position) || isItemAtPosition(endPosB, position))
                {
                    this.m_pathUnits.m_buffer[(int)(currentPathUnit)].m_positionCount = (byte)positionIndex;
                    num11 += positionIndex;
                    if (num11 != 0)
                    {
                        currentPathUnit = pathUnit.m_nextPathUnit;
                        positionIndex = (int)pathUnit.m_positionCount;
                        int num12 = 0;
                        while (currentPathUnit != 0u)
                        {
                            this.m_pathUnits.m_buffer[(int)((UIntPtr)currentPathUnit)].m_length = pathLength * (float)(num11 - positionIndex) / (float)num11;
                            positionIndex += (int)this.m_pathUnits.m_buffer[(int)((UIntPtr)currentPathUnit)].m_positionCount;
                            currentPathUnit = this.m_pathUnits.m_buffer[(int)((UIntPtr)currentPathUnit)].m_nextPathUnit;
                            if (++num12 >= 262144)
                            {
                                CODebugBase<LogChannel>.Error(LogChannel.Core, "Invalid list detected!\n" + Environment.StackTrace);
                                break;
                            }
                        }
                    }
                    PathUnit[] expr_BE2_cp_0 = this.m_pathUnits.m_buffer;
                    UIntPtr expr_BE2_cp_1 = (UIntPtr)unit;
                    expr_BE2_cp_0[(int)expr_BE2_cp_1].m_pathFindFlags = (byte)(expr_BE2_cp_0[(int)expr_BE2_cp_1].m_pathFindFlags | 4);
                    return;
                }
                if (positionIndex == 12)
                {
                    while (!Monitor.TryEnter(this.m_bufferLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                    {
                    }
                    uint num13;
                    try
                    {
                        Randomizer localRandom = this.m_pathRandomizer;
                        if (!this.m_pathUnits.CreateItem(out num13, ref localRandom))
                        {
                            PathUnit[] expr_CE1_cp_0 = this.m_pathUnits.m_buffer;
                            UIntPtr expr_CE1_cp_1 = (UIntPtr)unit;
                            expr_CE1_cp_0[(int)expr_CE1_cp_1].m_pathFindFlags = (byte)(expr_CE1_cp_0[(int)expr_CE1_cp_1].m_pathFindFlags | 8);
                            return;
                        }
                        this.m_pathRandomizer = localRandom;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num13)] = this.m_pathUnits.m_buffer[(int)((UIntPtr)currentPathUnit)];
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num13)].m_referenceCount = 1;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num13)].m_pathFindFlags = 4;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)currentPathUnit)].m_nextPathUnit = num13;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)currentPathUnit)].m_positionCount = (byte)positionIndex;
                        num11 += positionIndex;
                        Singleton<PathManager>.instance.m_pathUnitCount = (int)(this.m_pathUnits.ItemCount() - 1u);
                    }
                    finally
                    {
                        Monitor.Exit(this.m_bufferLock);
                    }
                    currentPathUnit = num13;
                    positionIndex = 0;
                }
                uint laneID = PathManager.GetLaneID(position);
                position = this.m_laneTarget[(int)((UIntPtr)laneID)];
            }
            PathUnit[] expr_D65_cp_0 = this.m_pathUnits.m_buffer;
            UIntPtr expr_D65_cp_1 = (UIntPtr)unit;
            expr_D65_cp_0[(int)expr_D65_cp_1].m_pathFindFlags = (byte)(expr_D65_cp_0[(int)expr_D65_cp_1].m_pathFindFlags | 8);
        }

        private void advanceToNextNode(CustomPathFind.BufferItem item, ushort nodeID, ref NetNode node, byte connectOffset, bool isMiddle, ref PathUnit data)
        {
            NetManager instance = Singleton<NetManager>.instance;
            bool isPedestrianLane = false;
			int tmp_nextLaneID = 0;
            NetInfo info = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            if ((int)item.m_position.m_lane < info.m_lanes.Length)
            {
                NetInfo.Lane lane = info.m_lanes[(int)item.m_position.m_lane];
				isPedestrianLane = (lane.m_laneType == NetInfo.LaneType.Pedestrian);
                if ((byte)(lane.m_finalDirection & NetInfo.Direction.Forward) != 0)
                {
                    tmp_nextLaneID = lane.m_similarLaneIndex;
                }
                else
                {
                    tmp_nextLaneID = lane.m_similarLaneCount - lane.m_similarLaneIndex - 1;
                }
            }
            if (isMiddle)
            {
                for (int j = 0; j < 8; j++)
                {
                    ushort segmentID = node.GetSegment(j);
                    if (segmentID != 0)
                    {
                        this.ProcessItem4(item, nodeID, segmentID, ref instance.m_segments.m_buffer[(int)segmentID], ref tmp_nextLaneID, connectOffset, !isPedestrianLane, isPedestrianLane);
                    }
                }
            }
            else if (isPedestrianLane)
            {
                ushort segment2 = item.m_position.m_segment;
                int lane2 = (int)item.m_position.m_lane;
                if (node.Info.m_class.m_service != ItemClass.Service.Beautification)
                {
                    bool flag2 = (node.m_flags & (NetNode.Flags.End | NetNode.Flags.Bend | NetNode.Flags.Junction)) != NetNode.Flags.None;
                    int laneIndex;
                    int laneIndex2;
                    uint num2;
                    uint num3;
                    instance.m_segments.m_buffer[(int)segment2].GetLeftAndRightLanes(nodeID, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, lane2, out laneIndex, out laneIndex2, out num2, out num3);
                    ushort num4 = segment2;
                    ushort num5 = segment2;
                    if (num2 == 0u || num3 == 0u)
                    {
                        ushort leftSegment;
                        ushort rightSegment;
                        instance.m_segments.m_buffer[(int)segment2].GetLeftAndRightSegments(nodeID, out leftSegment, out rightSegment);
                        int num6 = 0;
                        while (leftSegment != 0 && leftSegment != segment2 && num2 == 0u)
                        {
                            int num7;
                            int num8;
                            uint num9;
                            uint num10;
                            instance.m_segments.m_buffer[(int)leftSegment].GetLeftAndRightLanes(nodeID, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, -1, out num7, out num8, out num9, out num10);
                            if (num10 != 0u)
                            {
                                num4 = leftSegment;
                                laneIndex = num8;
                                num2 = num10;
                            }
                            else
                            {
                                leftSegment = instance.m_segments.m_buffer[(int)leftSegment].GetLeftSegment(nodeID);
                            }
                            if (++num6 == 8)
                            {
                                break;
                            }
                        }
                        num6 = 0;
                        while (rightSegment != 0 && rightSegment != segment2 && num3 == 0u)
                        {
                            int num11;
                            int num12;
                            uint num13;
                            uint num14;
                            instance.m_segments.m_buffer[(int)rightSegment].GetLeftAndRightLanes(nodeID, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, -1, out num11, out num12, out num13, out num14);
                            if (num13 != 0u)
                            {
                                num5 = rightSegment;
                                laneIndex2 = num11;
                                num3 = num13;
                            }
                            else
                            {
                                rightSegment = instance.m_segments.m_buffer[(int)rightSegment].GetRightSegment(nodeID);
                            }
                            if (++num6 == 8)
                            {
                                break;
                            }
                        }
                    }
                    if (num2 != 0u && (num4 != segment2 || flag2))
                    {
                        this.ProcessItem5(item, nodeID, num4, ref instance.m_segments.m_buffer[(int)num4], connectOffset, laneIndex, num2);
                    }
                    if (num3 != 0u && num3 != num2 && (num5 != segment2 || flag2))
                    {
                        this.ProcessItem5(item, nodeID, num5, ref instance.m_segments.m_buffer[(int)num5], connectOffset, laneIndex2, num3);
                    }
                }
                else
                {
                    for (int j = 0; j < 8; j++)
                    {
                        ushort segment3 = node.GetSegment(j);
                        if (segment3 != 0 && segment3 != segment2)
                        {
                            this.ProcessItem4(item, nodeID, segment3, ref instance.m_segments.m_buffer[(int)segment3], ref tmp_nextLaneID, connectOffset, false, true);
                        }
                    }
                }
                NetInfo.LaneType laneType = this.m_laneTypes & ~NetInfo.LaneType.Pedestrian;
                laneType &= ~(item.m_lanesUsed & NetInfo.LaneType.Vehicle);
                int num15;
                uint lane3;
                if (laneType != NetInfo.LaneType.None && instance.m_segments.m_buffer[(int)segment2].GetClosestLane(lane2, laneType, this.m_vehicleTypes, out num15, out lane3))
                {
                    NetInfo.Lane lane4 = info.m_lanes[num15];
                    byte connectOffset2;
                    if ((instance.m_segments.m_buffer[(int)segment2].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None == ((byte)(lane4.m_finalDirection & NetInfo.Direction.Backward) != 0))
                    {
                        connectOffset2 = 1;
                    }
                    else
                    {
                        connectOffset2 = 254;
                    }
                    this.ProcessItem5(item, nodeID, segment2, ref instance.m_segments.m_buffer[(int)segment2], connectOffset2, num15, lane3);
                }
            }
            else
            {
                bool flag3 = (node.m_flags & (NetNode.Flags.End | NetNode.Flags.OneWayOut)) != NetNode.Flags.None;
                bool flag4 = (byte)(this.m_laneTypes & NetInfo.LaneType.Pedestrian) != 0;
                byte connectOffset3 = 0;
                if (flag4)
                {
                    if (this.m_vehicleLane != 0u)
                    {
                        if (this.m_vehicleLane != item.m_laneID)
                        {
                            flag4 = false;
                        }
                        else
                        {
                            connectOffset3 = this.m_vehicleOffset;
                        }
                    }
                    else if (this.m_stablePath)
                    {
                        connectOffset3 = 128;
                    }
                    else
                    {
                        connectOffset3 = (byte)this.m_pathRandomizer.UInt32(1u, 254u);
                    }
                }

                ushort num16 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].GetRightSegment(nodeID);
                for (int k = 0; k < 8; k++)
                {
                    if (num16 == 0 || num16 == item.m_position.m_segment)
                    {
                        break;
                    }

                    if (TrafficPriority.isPrioritySegment(nodeID, num16) && data.m_position00.m_segment != num16)
                    {
                        var segment = instance.m_segments.m_buffer[num16];

                        var info2 = segment.Info;

                        uint segmentLanes = segment.m_lanes;
                        int infoLanes = 0;

                        var lanes = 0;

                        int[] laneNums = new int[16];
                        uint[] laneIds = new uint[16];

                        NetInfo.Direction dir = NetInfo.Direction.Forward;
                        if (segment.m_startNode == nodeID)
                            dir = NetInfo.Direction.Backward;
                        var dir2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? dir : NetInfo.InvertDirection(dir);
                        var dir3 = TrafficPriority.leftHandDrive ? NetInfo.InvertDirection(dir2) : dir2;

                        var m_lanes = info2.m_lanes;

                        if (TrafficPriority.leftHandDrive)
                        {
                            m_lanes.Reverse();
                        }

                        var laneArrows = 0;

                        while (infoLanes < m_lanes.Length && segmentLanes != 0u)
                        {
                            if (((NetLane.Flags) instance.m_lanes.m_buffer[segmentLanes].m_flags & NetLane.Flags.Left) ==
                                NetLane.Flags.Left || ((NetLane.Flags)instance.m_lanes.m_buffer[segmentLanes].m_flags & NetLane.Flags.Right) ==
                                        NetLane.Flags.Right || ((NetLane.Flags)instance.m_lanes.m_buffer[segmentLanes].m_flags & NetLane.Flags.Forward) ==
                                        NetLane.Flags.Forward)
                            {
                                laneArrows++;
                            }

                            if (m_lanes[infoLanes].m_laneType == NetInfo.LaneType.Vehicle && m_lanes[infoLanes].m_direction == dir3)
                            {
                                if (TrafficPriority.isLeftSegment(num16, item.m_position.m_segment, nodeID, true) >= 0)
                                {
                                    if (((NetLane.Flags) instance.m_lanes.m_buffer[segmentLanes].m_flags & NetLane.Flags.Left) ==
                                        NetLane.Flags.Left)
                                    {
                                        laneNums[lanes] = infoLanes;
                                        laneIds[lanes] = segmentLanes;
                                        lanes++;
                                    } 
                                }
                                else if (TrafficPriority.isRightSegment(num16, item.m_position.m_segment, nodeID, true) >= 0)
                                {
                                    if (((NetLane.Flags)instance.m_lanes.m_buffer[segmentLanes].m_flags & NetLane.Flags.Right) ==
                                        NetLane.Flags.Right)
                                    {
                                        laneNums[lanes] = infoLanes;
                                        laneIds[lanes] = segmentLanes;
                                        lanes++;
                                    } 
                                }
                                else
                                {
                                    if (((NetLane.Flags)instance.m_lanes.m_buffer[segmentLanes].m_flags & NetLane.Flags.Forward) ==
                                        NetLane.Flags.Forward)
                                    {
                                        laneNums[lanes] = infoLanes;
                                        laneIds[lanes] = segmentLanes;
                                        lanes++;
                                    } 
                                }
                            }

                            segmentLanes = instance.m_lanes.m_buffer[(int) ((UIntPtr) segmentLanes)].m_nextLane;
                            infoLanes++;
                        }

                        if (laneArrows > 0)
                        {
                            var newLaneNum = 0;
                            var newLaneId = 0u;
                            var newLane = -1;

                            if (lanes > 0)
                            {
                                if (lanes == 1)
                                {
                                    newLaneNum = laneNums[0];
                                    newLaneId = laneIds[0];
                                }
                                else
                                {
                                    var laneFound = false;

                                    if (info2.m_lanes.Length == info.m_lanes.Length)
                                    {

                                        for (var i = 0; i < laneNums.Length; i++)
                                        {
                                            if (laneNums[i] == item.m_position.m_lane)
                                            {
                                                newLaneNum = laneNums[i];
                                                newLaneId = laneIds[i];
                                                laneFound = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (!laneFound)
                                    {
                                        var lanePos = Mathf.Abs(info.m_lanes[item.m_position.m_lane].m_position);
                                        var closest = 100f;
                                        for (var i = 0; i < lanes; i++)
                                        {
                                            var newLanePos = Mathf.Abs(info2.m_lanes[laneNums[i]].m_position);

                                            if (Math.Abs(newLanePos - lanePos) < closest)
                                            {
                                                closest = Mathf.Abs(newLanePos - lanePos);
                                                newLane = i;
                                            }
                                        }

                                        if (newLane == -1)
                                        {
                                            newLaneNum = laneNums[0];
                                            newLaneId = laneIds[0];
                                        }
                                        else
                                        {
                                            newLaneNum = laneNums[newLane];
                                            newLaneId = laneIds[newLane];
                                        }
                                    }
                                }

                                this.ProcessItem2(item, nodeID, num16, ref instance.m_segments.m_buffer[(int) num16],
                                    ref tmp_nextLaneID,
                                    connectOffset, true, false, newLaneId, newLaneNum);
                            }
                        }
                        else
                        {
                            if (this.ProcessItem4(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16], ref tmp_nextLaneID,
                                connectOffset, true, false))
                            {
                                flag3 = true;
                            }
                        }
                    } 
                    //else if (TrafficRoadRestrictions.isSegment(num16))
                    //{
                    //    var restSegment = TrafficRoadRestrictions.getSegment(num16);

                    //    var preferedLaneAllows = 100;
                    //    uint preferedLaneId = 0;
                    //    int preferedLaneNum = 0;

                    //    NetInfo.Lane lane = info.m_lanes[(int)item.m_position.m_lane];

                    //    for (var i = 0; i < restSegment.lanes.Count; i++)
                    //    {
                    //        if ((byte) (lane.m_finalDirection & NetInfo.Direction.Forward) != 0)
                    //        {
                    //            if ((byte) (restSegment.lanes[i].direction & NetInfo.Direction.Forward) == 0)
                    //            {
                    //                continue;
                    //            }
                    //        }
                    //        else
                    //        {
                    //            if ((byte) (restSegment.lanes[i].direction & NetInfo.Direction.Backward) == 0)
                    //            {
                    //                continue;
                    //            }
                    //        }

                    //        if (this.m_vehicleType == TrafficRoadRestrictions.VehicleType.Car)
                    //        {
                    //            if (restSegment.lanes[i].enableCars)
                    //            {
                    //                if (restSegment.lanes[i].laneNum == num)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    break;
                    //                }
                    //                else if (restSegment.lanes[i].enabledTypes < preferedLaneAllows)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    preferedLaneAllows = restSegment.lanes[i].enabledTypes;
                    //                }
                    //            }
                    //        }
                    //        else if (this.m_vehicleType == TrafficRoadRestrictions.VehicleType.Service)
                    //        {
                    //            if (restSegment.lanes[i].enableService)
                    //            {
                    //                if (restSegment.lanes[i].laneNum == num)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    break;
                    //                }
                    //                else if (restSegment.lanes[i].enabledTypes < preferedLaneAllows)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    preferedLaneAllows = restSegment.lanes[i].enabledTypes;
                    //                }
                    //            }
                    //        }
                    //        else if (this.m_vehicleType == TrafficRoadRestrictions.VehicleType.Cargo)
                    //        {
                    //            if (restSegment.lanes[i].enableCargo)
                    //            {
                    //                if (restSegment.lanes[i].laneNum == num)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    break;
                    //                }
                    //                else if (restSegment.lanes[i].enabledTypes < preferedLaneAllows)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    preferedLaneAllows = restSegment.lanes[i].enabledTypes;
                    //                }
                    //            }
                    //        }
                    //        else if (this.m_vehicleType == TrafficRoadRestrictions.VehicleType.Transport)
                    //        {
                    //            if (restSegment.lanes[i].enableTransport)
                    //            {
                    //                if (restSegment.lanes[i].laneNum == num)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    break;
                    //                }
                    //                else if (restSegment.lanes[i].enabledTypes < preferedLaneAllows)
                    //                {
                    //                    preferedLaneId = restSegment.lanes[i].laneID;
                    //                    preferedLaneNum = restSegment.lanes[i].laneNum;
                    //                    preferedLaneAllows = restSegment.lanes[i].enabledTypes;
                    //                }
                    //            }
                    //        }
                    //    }

                    //    if (preferedLaneId != 0)
                    //    {
                    //        this.ProcessItem2(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16],
                    //            ref num,
                    //            connectOffset, true, false, preferedLaneId, preferedLaneNum);
                    //    }
                    //} 
                    else
                    {
                        if (this.ProcessItem4(item, nodeID, num16, ref instance.m_segments.m_buffer[(int) num16], ref tmp_nextLaneID,
                            connectOffset, true, false))
                        {
                            flag3 = true;
                        }
                    }

                    num16 = instance.m_segments.m_buffer[(int)num16].GetRightSegment(nodeID);
                }
                if (flag3)
                {
                    num16 = item.m_position.m_segment;
                    this.ProcessItem4(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16], ref tmp_nextLaneID, connectOffset, true, false);
                }
                int laneIndex3;
                uint lane5;
                if (flag4 && instance.m_segments.m_buffer[(int)num16].GetClosestLane((int)item.m_position.m_lane, NetInfo.LaneType.Pedestrian, this.m_vehicleTypes, out laneIndex3, out lane5))
                {
                    this.ProcessItem5(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16], connectOffset3, laneIndex3, lane5);
                }
            }
            if (node.m_lane != 0u)
            {
                bool targetDisabled = (node.m_flags & NetNode.Flags.Disabled) != NetNode.Flags.None;
                ushort segment4 = instance.m_lanes.m_buffer[(int)((UIntPtr)node.m_lane)].m_segment;
                if (segment4 != 0 && segment4 != item.m_position.m_segment)
                {
                    this.ProcessItem3(item, nodeID, targetDisabled, segment4, ref instance.m_segments.m_buffer[(int)segment4], node.m_lane, node.m_laneOffset, connectOffset);
                }
            }
        }

		static bool isSegmentAvailable(NetSegment segment)
		{
			return (segment.m_flags & (NetSegment.Flags.PathFailed | NetSegment.Flags.Flooded)) == NetSegment.Flags.None;
		}

		static bool isTurnPossible (ushort targetNode, NetSegment segment, NetSegment segmentAtItemPosition, NetInfo info, NetInfo info2, NetInfo.Direction direction)
		{
			float maxTurnAngle = 0.01f - Mathf.Min (info.m_maxTurnAngleCos, info2.m_maxTurnAngleCos);
			if (maxTurnAngle < 1f) {

				Vector3 targetDirection = (targetNode == segmentAtItemPosition.m_startNode) ? segmentAtItemPosition.m_startDirection : segmentAtItemPosition.m_endDirection;
				Vector3 segmentDirection = ((byte)(direction & NetInfo.Direction.Forward) != 0) ? segment.m_endDirection : segment.m_startDirection;

				float neededAngle = targetDirection.x * segmentDirection.x + targetDirection.z * segmentDirection.z;
				if (neededAngle >= maxTurnAngle) {
					return false;
				}
			}
			return true;
		}

		static float getSpeedLimit (PathUnit.Position itemPosition, NetInfo.Lane lane)
		{
			if (TrafficRoadRestrictions.isSegment (itemPosition.m_segment)) {
				SegmentRestrictions restrictionSegment = TrafficRoadRestrictions.getSegment (itemPosition.m_segment);
				if (restrictionSegment.speedLimits [itemPosition.m_lane] > 0.1f) {
					return restrictionSegment.speedLimits [itemPosition.m_lane];
				}
				else {
					return lane.m_speedLimit;
				}
			}
			else {
				return lane.m_speedLimit;
			}
		}

		float getCalculatedSegmentLength (PathUnit.Position itemPosition, NetSegment segmentAtItemPosition)
		{
			float num7 = segmentAtItemPosition.m_averageLength;
			if (!this.m_stablePath) {
				Randomizer randomizer = new Randomizer (this.m_pathFindIndex << 16 | (uint)itemPosition.m_segment);
				num7 *= (float)(randomizer.Int32 (900, 1000 + (int)(segmentAtItemPosition.m_trafficDensity * 10)) + this.m_pathRandomizer.Int32 (20u)) * 0.001f;
			}
			if (this.m_isHeavyVehicle && (segmentAtItemPosition.m_flags & NetSegment.Flags.HeavyBan) != NetSegment.Flags.None) {
				num7 *= 10f;
			}
			return num7;
		}

		float calculateComparisonValue (NetSegment segment, NetInfo.Lane lane, byte startOffset, byte itemOffset)
		{
			float calculatedLaneSpeed = this.CalculateLaneSpeed (startOffset, itemOffset, segment, lane);
			float num16 = (float)Mathf.Abs (itemOffset - startOffset) * FOO_FACTOR;
			return num16 * segment.m_averageLength / (calculatedLaneSpeed * this.m_maxLength);
		}

        private bool ProcessItem2(CustomPathFind.BufferItem item, ushort targetNode, ushort segmentID, ref NetSegment segment, ref int currentTargetIndex, byte connectOffset, bool enableVehicle, bool enablePedestrian, uint laneID, int laneNum)
        {
            
			if (!isSegmentAvailable (segment)) {
				return false;
			}

            NetManager instance = Singleton<NetManager>.instance;
			NetSegment segmentAtItemPosition = instance.m_segments.m_buffer [(int)item.m_position.m_segment];

			NetInfo info = segment.Info;
            NetInfo info2 = segmentAtItemPosition.Info;

            int num = info.m_lanes.Length;

            NetInfo.Direction direction = (targetNode != segment.m_startNode) ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
            NetInfo.Direction direction2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? direction : NetInfo.InvertDirection(direction);
            
			if(!isTurnPossible (targetNode, segment, segmentAtItemPosition, info, info2, direction))
				return false;

			bool result = false;

			float num5 = 1f;
            float laneSpeed = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
			VehicleInfo.VehicleType vehicleType = VehicleInfo.VehicleType.None;
			PathUnit.Position itemPosition = item.m_position;
            
			if ((int)item.m_position.m_lane < info2.m_lanes.Length)
			{    
				NetInfo.Lane lane = info2.m_lanes[itemPosition.m_lane];
                laneType = lane.m_laneType;
                vehicleType = lane.m_vehicleType;

                num5 = getSpeedLimit (itemPosition, lane);

				laneSpeed = this.CalculateLaneSpeed(connectOffset, itemPosition.m_offset, segmentAtItemPosition, lane);
            }

			float calculatedSegmentLength = getCalculatedSegmentLength (itemPosition, segmentAtItemPosition);

			float num8 = (float)Mathf.Abs (connectOffset - itemPosition.m_offset) * FOO_FACTOR * calculatedSegmentLength;
            float num9 = item.m_methodDistance + num8;
            float num10 = item.m_comparisonValue + num8 / (laneSpeed * this.m_maxLength);
			Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * FOO_FACTOR);
            int num11 = laneNum;
            bool flag = (instance.m_nodes.m_buffer[(int)targetNode].m_flags & NetNode.Flags.Transition) != NetNode.Flags.None;
            NetInfo.LaneType laneType2 = this.m_laneTypes;
            if (!enableVehicle)
            {
                laneType2 &= ~NetInfo.LaneType.Vehicle;
            }
            if (!enablePedestrian)
            {
                laneType2 &= ~NetInfo.LaneType.Pedestrian;
            }
            int num12 = laneNum;

            NetInfo.Lane lane2 = info.m_lanes[num12];

            float speedLimit = lane2.m_speedLimit;

            if (TrafficRoadRestrictions.isSegment(instance.m_lanes.m_buffer[(int)((UIntPtr)laneID)].m_segment))
            {
                var restrictionSegment = TrafficRoadRestrictions.getSegment(instance.m_lanes.m_buffer[(int)((UIntPtr)laneID)].m_segment);

                if (restrictionSegment.speedLimits[(int)item.m_position.m_lane] > 0.1f)
                {
                    speedLimit = restrictionSegment.speedLimits[(int)item.m_position.m_lane];
                }
            }

            if (lane2.CheckType(laneType2, this.m_vehicleTypes) && (segmentID != item.m_position.m_segment || num12 != (int)item.m_position.m_lane) && (byte)(lane2.m_finalDirection & direction2) != 0)
            {
                Vector3 a;
                if ((byte)(direction & NetInfo.Direction.Forward) != 0)
                {
                    a = instance.m_lanes.m_buffer[(int)((UIntPtr)laneID)].m_bezier.d;
                }
                else
                {
                    a = instance.m_lanes.m_buffer[(int)((UIntPtr)laneID)].m_bezier.a;
                }
                float num13 = Vector3.Distance(a, b);
                if (flag)
                {
                    num13 *= 2f;
                }
                float num14 = num13 / ((num5 + lane2.m_speedLimit) * 0.5f * this.m_maxLength);
                CustomPathFind.BufferItem item2;
                item2.m_position.m_segment = segmentID;
                item2.m_position.m_lane = (byte)laneNum;
                item2.m_position.m_offset = (byte)(((byte)(direction & NetInfo.Direction.Forward) == 0) ? 0 : 255);
                if (laneType != lane2.m_laneType)
                {
                    item2.m_methodDistance = 0f;
                }
                else
                {
                    item2.m_methodDistance = num9 + num13;
                }

                item2.m_comparisonValue = num10 + num14;
                if (laneID == this.m_startLaneA)
                {
					item2.m_comparisonValue += calculateComparisonValue (segment, lane2, this.m_startOffsetA, item2.m_position.m_offset);
                }
                if (laneID == this.m_startLaneB)
                {
					item2.m_comparisonValue += calculateComparisonValue (segment, lane2, this.m_startOffsetB, item2.m_position.m_offset);
                }
                if (!this.m_ignoreBlocked && (segment.m_flags & NetSegment.Flags.Blocked) != NetSegment.Flags.None && lane2.m_laneType == NetInfo.LaneType.Vehicle)
                {
                    item2.m_comparisonValue += 0.1f;
                    result = true;
                }
                item2.m_direction = direction;
                item2.m_lanesUsed = (item.m_lanesUsed | lane2.m_laneType);
                item2.m_laneID = laneID;
                if (lane2.m_laneType == laneType && lane2.m_vehicleType == vehicleType)
                {
                    int firstTarget = (int)instance.m_lanes.m_buffer[(int)((UIntPtr)laneID)].m_firstTarget;
                    int lastTarget = (int)instance.m_lanes.m_buffer[(int)((UIntPtr)laneID)].m_lastTarget;
                    if (currentTargetIndex < firstTarget || currentTargetIndex >= lastTarget)
                    {
                        item2.m_comparisonValue += Mathf.Max(1f, num13 * 3f - 3f) / ((speedLimit + lane2.m_speedLimit) * 0.5f * this.m_maxLength);
                    }
                }

                this.AddBufferItem(item2, item.m_position);
            }

            currentTargetIndex = num11;
            return result;
        }

        private NetLane.Flags GetLaneFlags(ushort segmentId, ushort nodeId)
        {
            NetManager instance = NetManager.instance;
            NetSegment seg = instance.m_segments.m_buffer[segmentId];
            NetLane.Flags flags = NetLane.Flags.None;
            NetInfo.Direction dir = NetInfo.Direction.Forward;
            if (seg.m_startNode == nodeId)
                dir = NetInfo.Direction.Backward;
            ulong currentLane = seg.m_lanes;
            for (int i = 0; i < seg.Info.m_lanes.Length; i++)
            {
                if (((seg.Info.m_lanes[i].m_direction & dir) == dir) && seg.Info.m_lanes[i].m_laneType == NetInfo.LaneType.Vehicle)
                    flags |= (NetLane.Flags)instance.m_lanes.m_buffer[currentLane].m_flags;
                currentLane = instance.m_lanes.m_buffer[currentLane].m_nextLane;
            }
            return flags;
        }


        private float CalculateLaneSpeed(byte startOffset, byte endOffset, NetSegment segment, NetInfo.Lane laneInfo)
        {
            NetInfo.Direction direction = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? laneInfo.m_finalDirection : NetInfo.InvertDirection(laneInfo.m_finalDirection);
            if ((byte)(direction & NetInfo.Direction.Avoid) == 0)
            {
                return laneInfo.m_speedLimit;
            }
            if (endOffset > startOffset && direction == NetInfo.Direction.AvoidForward)
            {
                return laneInfo.m_speedLimit * 0.1f;
            }
            if (endOffset < startOffset && direction == NetInfo.Direction.AvoidBackward)
            {
                return laneInfo.m_speedLimit * 0.1f;
            }
            return laneInfo.m_speedLimit * 0.2f;
        }

        private void ProcessItem3(CustomPathFind.BufferItem item, ushort targetNode, bool targetDisabled, ushort segmentID, ref NetSegment segment, uint lane, byte offset, byte connectOffset)
        {
            if ((segment.m_flags & (NetSegment.Flags.PathFailed | NetSegment.Flags.Flooded)) != NetSegment.Flags.None)
            {
                return;
            }
            NetManager instance = Singleton<NetManager>.instance;
            if (targetDisabled && ((instance.m_nodes.m_buffer[(int)segment.m_startNode].m_flags | instance.m_nodes.m_buffer[(int)segment.m_endNode].m_flags) & NetNode.Flags.Disabled) == NetNode.Flags.None)
            {
                return;
            }
            NetInfo info = segment.Info;
            NetInfo info2 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            int num = info.m_lanes.Length;
            uint num2 = segment.m_lanes;
            float num3 = 1f;
            float num4 = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
            if ((int)item.m_position.m_lane < info2.m_lanes.Length)
            {
                NetInfo.Lane lane2 = info2.m_lanes[(int)item.m_position.m_lane];
                num3 = lane2.m_speedLimit;
                laneType = lane2.m_laneType;
                num4 = this.CalculateLaneSpeed(connectOffset, item.m_position.m_offset, instance.m_segments.m_buffer[(int)item.m_position.m_segment], lane2);
            }
            float averageLength = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_averageLength;
            float num5 = (float)Mathf.Abs((int)(connectOffset - item.m_position.m_offset)) * FOO_FACTOR * averageLength;
            float num6 = item.m_methodDistance + num5;
            float num7 = item.m_comparisonValue + num5 / (num4 * this.m_maxLength);
            Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * FOO_FACTOR);
            int num8 = 0;
            while (num8 < num && num2 != 0u)
            {
                if (lane == num2)
                {
                    NetInfo.Lane lane3 = info.m_lanes[num8];
                    if (lane3.CheckType(this.m_laneTypes, this.m_vehicleTypes))
                    {
                        Vector3 a = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].CalculatePosition((float)offset * FOO_FACTOR);
                        float num9 = Vector3.Distance(a, b);
                        CustomPathFind.BufferItem item2;
                        item2.m_position.m_segment = segmentID;
                        item2.m_position.m_lane = (byte)num8;
                        item2.m_position.m_offset = offset;
                        if (laneType != lane3.m_laneType)
                        {
                            item2.m_methodDistance = 0f;
                        }
                        else
                        {
                            item2.m_methodDistance = num6 + num9;
                        }
                        if (lane3.m_laneType != NetInfo.LaneType.Pedestrian || item2.m_methodDistance < 1000f)
                        {
                            item2.m_comparisonValue = num7 + num9 / ((num3 + lane3.m_speedLimit) * 0.5f * this.m_maxLength);
                            if (lane == this.m_startLaneA)
                            {
								item2.m_comparisonValue += calculateComparisonValue(segment, lane3, m_startOffsetA, item2.m_position.m_offset);
                            }
                            if (lane == this.m_startLaneB)
                            {
								item2.m_comparisonValue += calculateComparisonValue (segment, lane3, m_startOffsetB, item2.m_position.m_offset);
                            }
                            if ((segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
                            {
                                item2.m_direction = NetInfo.InvertDirection(lane3.m_finalDirection);
                            }
                            else
                            {
                                item2.m_direction = lane3.m_finalDirection;
                            }
                            item2.m_laneID = lane;
                            item2.m_lanesUsed = (item.m_lanesUsed | lane3.m_laneType);
                            this.AddBufferItem(item2, item.m_position);
                        }
                    }
                    return;
                }
                num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                num8++;
            }
        }

        private bool ProcessItem4(CustomPathFind.BufferItem item, ushort targetNode, ushort segmentID, ref NetSegment segment, ref int currentTargetIndex, byte connectOffset, bool enableVehicle, bool enablePedestrian)
        {
            
			if (!isSegmentAvailable(segment))
            {
                return false;
			}
            
			NetManager instance = Singleton<NetManager>.instance;
			NetSegment segmentAtItemPosition = instance.m_segments.m_buffer [(int)item.m_position.m_segment];

			NetInfo info = segment.Info;
            NetInfo info2 = segmentAtItemPosition.Info;
            int num = info.m_lanes.Length;
            uint num2 = segment.m_lanes;
            NetInfo.Direction direction = (targetNode != segment.m_startNode) ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
            NetInfo.Direction direction2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? direction : NetInfo.InvertDirection(direction);
            
			if(!isTurnPossible(targetNode, segment, segmentAtItemPosition, info, info2, direction))
				return false;

			bool result = false;

            float num5 = 1f;
            float num6 = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
            VehicleInfo.VehicleType vehicleType = VehicleInfo.VehicleType.None;
            if ((int)item.m_position.m_lane < info2.m_lanes.Length)
            {
                NetInfo.Lane lane = info2.m_lanes[(int)item.m_position.m_lane];
                laneType = lane.m_laneType;
                vehicleType = lane.m_vehicleType;
                num5 = lane.m_speedLimit;
                num6 = this.CalculateLaneSpeed(connectOffset, item.m_position.m_offset, segmentAtItemPosition, lane);
            }
            float num7 = segmentAtItemPosition.m_averageLength;
            if (!this.m_stablePath)
            {
                Randomizer randomizer = new Randomizer(this.m_pathFindIndex << 16 | (uint)item.m_position.m_segment);
                num7 *= (float)(randomizer.Int32(900, 1000 + (int)(segmentAtItemPosition.m_trafficDensity * 10)) + this.m_pathRandomizer.Int32(20u)) * 0.001f;
            }
            if (this.m_isHeavyVehicle && (segmentAtItemPosition.m_flags & NetSegment.Flags.HeavyBan) != NetSegment.Flags.None)
            {
                num7 *= 10f;
            }
            float num8 = (float)Mathf.Abs((int)(connectOffset - item.m_position.m_offset)) * FOO_FACTOR * num7;
            float num9 = item.m_methodDistance + num8;
            float num10 = item.m_comparisonValue + num8 / (num6 * this.m_maxLength);
            Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * FOO_FACTOR);
            int num11 = currentTargetIndex;
            bool flag = (instance.m_nodes.m_buffer[(int)targetNode].m_flags & NetNode.Flags.Transition) != NetNode.Flags.None;
            NetInfo.LaneType laneType2 = this.m_laneTypes;
            if (!enableVehicle)
            {
                laneType2 &= ~NetInfo.LaneType.Vehicle;
            }
            if (!enablePedestrian)
            {
                laneType2 &= ~NetInfo.LaneType.Pedestrian;
            }
            int num12 = 0;
            while (num12 < num && num2 != 0u)
            {
                NetInfo.Lane lane2 = info.m_lanes[num12];
                if ((byte)(lane2.m_finalDirection & direction2) != 0)
                {
                    if (lane2.CheckType(laneType2, this.m_vehicleTypes) && (segmentID != item.m_position.m_segment || num12 != (int)item.m_position.m_lane) && (byte)(lane2.m_finalDirection & direction2) != 0)
                    {
                        Vector3 a;
                        if ((byte)(direction & NetInfo.Direction.Forward) != 0)
                        {
                            a = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_bezier.d;
                        }
                        else
                        {
                            a = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_bezier.a;
                        }
                        float num13 = Vector3.Distance(a, b);
                        if (flag)
                        {
                            num13 *= 2f;
                        }
                        float num14 = num13 / ((num5 + lane2.m_speedLimit) * 0.5f * this.m_maxLength);
                        CustomPathFind.BufferItem item2;
                        item2.m_position.m_segment = segmentID;
                        item2.m_position.m_lane = (byte)num12;
                        item2.m_position.m_offset = (byte)(((byte)(direction & NetInfo.Direction.Forward) == 0) ? 0 : 255);
                        if (laneType != lane2.m_laneType)
                        {
                            item2.m_methodDistance = 0f;
                        }
                        else
                        {
                            item2.m_methodDistance = num9 + num13;
                        }
                        if (lane2.m_laneType != NetInfo.LaneType.Pedestrian || item2.m_methodDistance < 1000f)
                        {
                            item2.m_comparisonValue = num10 + num14;
                            if (num2 == this.m_startLaneA)
                            {
                                float num15 = this.CalculateLaneSpeed(this.m_startOffsetA, item2.m_position.m_offset, segment, lane2);
                                float num16 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetA)) * FOO_FACTOR;
                                item2.m_comparisonValue += num16 * segment.m_averageLength / (num15 * this.m_maxLength);
                            }
                            if (num2 == this.m_startLaneB)
                            {
                                float num17 = this.CalculateLaneSpeed(this.m_startOffsetB, item2.m_position.m_offset, segment, lane2);
                                float num18 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetB)) * FOO_FACTOR;
                                item2.m_comparisonValue += num18 * segment.m_averageLength / (num17 * this.m_maxLength);
                            }
                            if (!this.m_ignoreBlocked && (segment.m_flags & NetSegment.Flags.Blocked) != NetSegment.Flags.None && lane2.m_laneType == NetInfo.LaneType.Vehicle)
                            {
                                item2.m_comparisonValue += 0.1f;
                                result = true;
                            }
                            item2.m_direction = direction;
                            item2.m_lanesUsed = (item.m_lanesUsed | lane2.m_laneType);
                            item2.m_laneID = num2;
                            if (lane2.m_laneType == laneType && lane2.m_vehicleType == vehicleType)
                            {
                                int firstTarget = (int)instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_firstTarget;
                                int lastTarget = (int)instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_lastTarget;
                                if (currentTargetIndex < firstTarget || currentTargetIndex >= lastTarget)
                                {
                                    item2.m_comparisonValue += Mathf.Max(1f, num13 * 3f - 3f) / ((num5 + lane2.m_speedLimit) * 0.5f * this.m_maxLength);
                                }
                            }

                            this.AddBufferItem(item2, item.m_position);
                        }
                    }
                }
                else if (lane2.m_laneType == laneType && lane2.m_vehicleType == vehicleType)
                {
                    num11++;
                }
                num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                num12++;
            }
            currentTargetIndex = num11;
            return result;
        }

        private void ProcessItem5(CustomPathFind.BufferItem item, ushort targetNode, ushort segmentID, ref NetSegment segment, byte connectOffset, int laneIndex, uint lane)
        {
            if ((segment.m_flags & (NetSegment.Flags.PathFailed | NetSegment.Flags.Flooded)) != NetSegment.Flags.None)
            {
                return;
            }
            NetManager instance = Singleton<NetManager>.instance;
            NetInfo info = segment.Info;
            NetInfo info2 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            int num = info.m_lanes.Length;
            float num2;
            byte offset;
            if (segmentID == item.m_position.m_segment)
            {
                Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * FOO_FACTOR);
                Vector3 a = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].CalculatePosition((float)connectOffset * FOO_FACTOR);
                num2 = Vector3.Distance(a, b);
                offset = connectOffset;
            }
            else
            {
                NetInfo.Direction direction = (targetNode != segment.m_startNode) ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
                Vector3 b2 = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * FOO_FACTOR);
                Vector3 a2;
                if ((byte)(direction & NetInfo.Direction.Forward) != 0)
                {
                    a2 = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].m_bezier.d;
                }
                else
                {
                    a2 = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].m_bezier.a;
                }
                num2 = Vector3.Distance(a2, b2);
                offset = (byte)(((byte)(direction & NetInfo.Direction.Forward) == 0) ? 0 : 255);
            }
            float num3 = 1f;
            float num4 = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
            if ((int)item.m_position.m_lane < info2.m_lanes.Length)
            {
                NetInfo.Lane lane2 = info2.m_lanes[(int)item.m_position.m_lane];
                num3 = lane2.m_speedLimit;
                laneType = lane2.m_laneType;
                num4 = this.CalculateLaneSpeed(connectOffset, item.m_position.m_offset, instance.m_segments.m_buffer[(int)item.m_position.m_segment], lane2);
            }
            float averageLength = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_averageLength;
            float num5 = (float)Mathf.Abs((int)(connectOffset - item.m_position.m_offset)) * FOO_FACTOR * averageLength;
            float num6 = item.m_methodDistance + num5;
            float num7 = item.m_comparisonValue + num5 / (num4 * this.m_maxLength);
            if (laneIndex < num)
            {
                NetInfo.Lane lane3 = info.m_lanes[laneIndex];
                CustomPathFind.BufferItem item2;
                item2.m_position.m_segment = segmentID;
                item2.m_position.m_lane = (byte)laneIndex;
                item2.m_position.m_offset = offset;
                if (laneType != lane3.m_laneType)
                {
                    item2.m_methodDistance = 0f;
                }
                else
                {
                    item2.m_methodDistance = num6 + num2;
                }
                if (lane3.m_laneType != NetInfo.LaneType.Pedestrian || item2.m_methodDistance < 1000f)
                {
                    item2.m_comparisonValue = num7 + num2 / ((num3 + lane3.m_speedLimit) * 0.25f * this.m_maxLength);
                    if (lane == this.m_startLaneA)
                    {
                        float num8 = this.CalculateLaneSpeed(this.m_startOffsetA, item2.m_position.m_offset, segment, lane3);
                        float num9 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetA)) * FOO_FACTOR;
                        item2.m_comparisonValue += num9 * segment.m_averageLength / (num8 * this.m_maxLength);
                    }
                    if (lane == this.m_startLaneB)
                    {
                        float num10 = this.CalculateLaneSpeed(this.m_startOffsetB, item2.m_position.m_offset, segment, lane3);
                        float num11 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetB)) * FOO_FACTOR;
                        item2.m_comparisonValue += num11 * segment.m_averageLength / (num10 * this.m_maxLength);
                    }
                    if ((segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
                    {
                        item2.m_direction = NetInfo.InvertDirection(lane3.m_finalDirection);
                    }
                    else
                    {
                        item2.m_direction = lane3.m_finalDirection;
                    }
                    item2.m_laneID = lane;
                    item2.m_lanesUsed = (item.m_lanesUsed | lane3.m_laneType);
                    this.AddBufferItem(item2, item.m_position);
                }
            }
        }

        private void AddBufferItem(CustomPathFind.BufferItem item, PathUnit.Position target)
        {
            uint num = this.m_laneLocation[(int)((UIntPtr)item.m_laneID)];
            uint num2 = num >> 16;
            int num3 = (int)(num & 65535u);
            int num6;
            if (num2 == this.m_pathFindIndex)
            {
                if (item.m_comparisonValue >= this.m_buffer[num3].m_comparisonValue)
                {
                    return;
                }
                int num4 = num3 >> 6;
                int num5 = num3 & -64;
                if (num4 < this.m_bufferMinPos || (num4 == this.m_bufferMinPos && num5 < this.m_bufferMin[num4]))
                {
                    return;
                }
                num6 = Mathf.Max(Mathf.RoundToInt(item.m_comparisonValue * 1024f), this.m_bufferMinPos);
                if (num6 == num4)
                {
                    this.m_buffer[num3] = item;
                    this.m_laneTarget[(int)((UIntPtr)item.m_laneID)] = target;
                    return;
                }
                int num7 = num4 << 6 | this.m_bufferMax[num4]--;
                CustomPathFind.BufferItem bufferItem = this.m_buffer[num7];
                this.m_laneLocation[(int)((UIntPtr)bufferItem.m_laneID)] = num;
                this.m_buffer[num3] = bufferItem;
            }
            else
            {
                num6 = Mathf.Max(Mathf.RoundToInt(item.m_comparisonValue * 1024f), this.m_bufferMinPos);
            }
            if (num6 >= 1024)
            {
                return;
            }
            while (this.m_bufferMax[num6] == 63)
            {
                num6++;
                if (num6 == 1024)
                {
                    return;
                }
            }
            if (num6 > this.m_bufferMaxPos)
            {
                this.m_bufferMaxPos = num6;
            }
            num3 = (num6 << 6 | ++this.m_bufferMax[num6]);
            this.m_buffer[num3] = item;
            this.m_laneLocation[(int)((UIntPtr)item.m_laneID)] = (this.m_pathFindIndex << 16 | (uint)num3);
            this.m_laneTarget[(int)((UIntPtr)item.m_laneID)] = target;
        }
        private void GetLaneDirection(PathUnit.Position pathPos, out NetInfo.Direction direction, out NetInfo.LaneType type)
        {
            NetManager instance = Singleton<NetManager>.instance;
            NetInfo info = instance.m_segments.m_buffer[(int)pathPos.m_segment].Info;
            if (info.m_lanes.Length > (int)pathPos.m_lane)
            {
                direction = info.m_lanes[(int)pathPos.m_lane].m_finalDirection;
                type = info.m_lanes[(int)pathPos.m_lane].m_laneType;
                if ((instance.m_segments.m_buffer[(int)pathPos.m_segment].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
                {
                    direction = NetInfo.InvertDirection(direction);
                }
            }
            else
            {
                direction = NetInfo.Direction.None;
                type = NetInfo.LaneType.None;
            }
        }

        private void PathFindThread()
        {
            while (true)
            {
                while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                {
                }
                try
                {
                    while (this.m_queueFirst == 0u && !this.m_terminated)
                    {
                        Monitor.Wait(this.m_queueLock);
                    }
                    if (this.m_terminated)
                    {
                        break;
                    }
                    this.m_calculating = this.m_queueFirst;
                    this.m_queueFirst = this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_nextPathUnit;
                    if (this.m_queueFirst == 0u)
                    {
                        this.m_queueLast = 0u;
                        this.m_queuedPathFindCount = 0;
                    }
                    else
                    {
                        this.m_queuedPathFindCount--;
                    }
                    this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_nextPathUnit = 0u;
                    this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags = (byte)(((int)this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags & -2) | 2);
                }
                finally
                {
                    Monitor.Exit(this.m_queueLock);
                }
                try
                {
                    this.m_pathfindProfiler.BeginStep();
                    try
                    {
                        this.PathFindImplementation(this.m_calculating, ref this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)]);
                    }
                    finally
                    {
                        this.m_pathfindProfiler.EndStep();
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log("path thread error: " + ex.Message);
                    UIView.ForwardException(ex);
                    CODebugBase<LogChannel>.Error(LogChannel.Core, "Path find error: " + ex.Message + "\n" + ex.StackTrace);
                    PathUnit[] expr_1A0_cp_0 = this.m_pathUnits.m_buffer;
                    UIntPtr expr_1A0_cp_1 = (UIntPtr)this.m_calculating;
                    expr_1A0_cp_0[(int)expr_1A0_cp_1].m_pathFindFlags = (byte)(expr_1A0_cp_0[(int)expr_1A0_cp_1].m_pathFindFlags | 8);
                }
                while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                {
                }
                try
                {
                    this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags = (byte)((int)this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags & -3);
                    Singleton<PathManager>.instance.ReleasePath(this.m_calculating);
                    this.m_calculating = 0u;
                    Monitor.Pulse(this.m_queueLock);
                }
                finally
                {
                    Monitor.Exit(this.m_queueLock);
                }
            }
        }
    }
}
