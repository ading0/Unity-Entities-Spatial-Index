using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

using ASH = Unity.Collections.LowLevel.Unsafe.AtomicSafetyHandle;

namespace SpatialHashing
{

    /* Spatial hashing logic. Has to be assigned by ref for Burst compatibility.
     * Works with Unity DOTS safety checks. */
    public unsafe struct SpatialIndex<T> : IDisposable, IRay where T : unmanaged, ISpatialHashingItem<T>
    {
        public SpatialIndex(Bounds worldBounds, float3 cellSize, Allocator label)
            : this(worldBounds, cellSize, worldBounds.GetCellCount(cellSize).Multiply() * 3, label)
        { }

        public SpatialIndex(Bounds worldBounds, float3 cellSize, int initialSize, Allocator allocator)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            switch (allocator)
            {
                case <= Allocator.None:
                    throw new ArgumentException("Allocator must be Temp, TempJob or Persistent", nameof(allocator));
                case >= Allocator.FirstUserIndex:
                    throw new ArgumentException("Allocator must be Temp, TempJob or Persistent", nameof(allocator));
            }

            if (initialSize < 1)
                throw new ArgumentOutOfRangeException(nameof(initialSize), "InitialSize must be >0");

            DisposeSentinel.Create(out m_Safety, out m_DisposeSentinel, 0, allocator);
#endif

            _allocatorLabel = allocator;
            _data = (SpatialIndexData*) UnsafeUtility.MallocTracked(sizeof(SpatialIndexData), UnsafeUtility.AlignOf<SpatialIndexData>(), allocator, 0);
            _data->WorldBounds = worldBounds;
            _data->WorldBoundsMin = worldBounds.Min;
            _data->CellSize = cellSize;
            _data->CellCount = worldBounds.GetCellCount(cellSize);
            _data->RayCastBound = new Bounds();
            _data->HasHit = false;
            _data->Counter = 0;
            _data->RayOrigin = float3.zero;
            _data->RayDirection = float3.zero;

            _buckets = new NativeParallelMultiHashMap<uint, int>(initialSize, allocator);
            _itemIDToBounds = new NativeParallelHashMap<int, Bounds>(initialSize >> 1, allocator);
            _itemIDToItem = new NativeParallelHashMap<int, T>(initialSize >> 1, allocator);
            _helpMoveHashMapOld = new NativeParallelHashSet<int3>(128, allocator);
            _helpMoveHashMapNew = new NativeParallelHashSet<int3>(128, allocator);

            _voxelRay = new VoxelRay<SpatialIndex<T>>();
            _rayHitValue = 0;
        }

        public void Dispose()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Dispose(ref m_Safety, ref m_DisposeSentinel);
#endif

            UnsafeUtility.FreeTracked(_data, _allocatorLabel);

            _buckets.Dispose();
            _itemIDToBounds.Dispose();
            _itemIDToItem.Dispose();
            _helpMoveHashMapOld.Dispose();
            _helpMoveHashMapNew.Dispose();
        }

        #region I/O

        public void Add(ref T item)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckWriteAndThrow(m_Safety);
#endif

            Bounds bounds = new(item.GetCenter(), item.GetSize());

            bounds.Clamp(_data->WorldBounds);

            int itemID = ++_data->Counter;
            item.SpatialHashingIndex = itemID;
            _itemIDToBounds.TryAdd(itemID, bounds);
            _itemIDToItem.TryAdd(itemID, item);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);
            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;
                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;
                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;
                        AddInternal(hashPosition, itemID);
                    }
                }
            }
        }

        public void AddFast(ref T item)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckWriteAndThrow(m_Safety);
#endif

            int itemID = item.SpatialHashingIndex;
            Bounds bounds = new Bounds(item.GetCenter(), item.GetSize());
            bounds.Clamp(_data->WorldBounds);

            _itemIDToBounds[itemID] = bounds;
            _itemIDToItem[itemID] = item;

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);
            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;
                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;
                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;
                        AddInternal(hashPosition, itemID);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddInternal(int3 position, int itemID)
        {
            _buckets.Add(Hash(position), itemID);
        }

        public void Remove(int itemID)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckWriteAndThrow(m_Safety);
#endif
            Assert.IsTrue(_itemIDToBounds.TryGetValue(itemID, out Bounds bounds));

            _itemIDToBounds.Remove(itemID);
            _itemIDToItem.Remove(itemID);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);

            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;

                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;

                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;

                        RemoveInternal(hashPosition, itemID);
                    }
                }
            }
        }

        /* Remove method used to move an item */
        public void RemoveFast(int itemID)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckWriteAndThrow(m_Safety);
#endif
            
            Assert.IsTrue(_itemIDToBounds.TryGetValue(itemID, out Bounds bounds));

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);
            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;
                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;
                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;
                        RemoveInternal(hashPosition, itemID);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void RemoveInternal(int3 voxelPosition, int itemID)
        {
            _buckets.Remove(Hash(voxelPosition), itemID);
        }

        public void Move(T item)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckWriteAndThrow(m_Safety);
#endif
            int itemID = item.SpatialHashingIndex;
            bool success = _itemIDToBounds.TryGetValue(itemID, out Bounds oldBounds);
            Assert.IsTrue(success);

            Bounds newBounds = new Bounds(item.GetCenter(), item.GetSize());
            newBounds.Clamp(_data->WorldBounds);
            _itemIDToBounds.Remove(itemID);
            _itemIDToBounds.TryAdd(itemID, newBounds);
            _itemIDToItem.Remove(itemID);
            _itemIDToItem.TryAdd(itemID, item);

            _helpMoveHashMapOld.Clear();
            SetVoxelIndexForBounds(_data, oldBounds, _helpMoveHashMapOld);
            _helpMoveHashMapNew.Clear();
            SetVoxelIndexForBounds(_data, newBounds, _helpMoveHashMapNew);

            foreach (int3 oldVoxelPosition in _helpMoveHashMapOld)
            {
                if (_helpMoveHashMapNew.Contains(oldVoxelPosition) == false)
                    RemoveInternal(oldVoxelPosition, itemID);
            }

            foreach (int3 newVoxelPosition in _helpMoveHashMapOld)
            {
                if (_helpMoveHashMapOld.Contains(newVoxelPosition) == false)
                    AddInternal(newVoxelPosition, itemID);
            }
        }

        public void Resize(T item)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckWriteAndThrow(m_Safety);
#endif

            Move(item);
        }

        public void Clear()
        {
            _itemIDToBounds.Clear();
            _buckets.Clear();
            _itemIDToBounds.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetVoxelIndexForBounds(SpatialIndexData* data, Bounds bounds, NativeParallelHashSet<int3> collection)
        {
            CalculateStartEndIterationInternal(data, bounds, out int3 start, out int3 end);

            int3 position = new int3(0F);

            for (int x = start.x; x < end.x; ++x)
            {
                position.x = x;

                for (int y = start.y; y < end.y; ++y)
                {
                    position.y = y;

                    for (int z = start.z; z < end.z; ++z)
                    {
                        position.z = z;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        bool success = collection.Add(position);

                        if (success == false)
                        {
                            throw new Exception("You are trying to add a position already in a bound collection.");
                        }
#else
                        collection.Add(position);
#endif
                    }
                }
            }
        }

        public readonly void CalculateStartEndIteration(Bounds bounds, out int3 start, out int3 end)
        {
            CalculateStartEndIterationInternal(_data, bounds, out start, out end);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateStartEndIterationInternal(SpatialIndexData* data, Bounds bounds, out int3 start, out int3 end)
        {
            start = ((bounds.Min - data->WorldBoundsMin) / data->CellSize).FloorToInt();
            end = ((bounds.Max - data->WorldBoundsMin) / data->CellSize).CeilToInt();
        }

        public T GetObject(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif

            if (_itemIDToItem.TryGetValue(index, out T value))
                return value;

            Assert.IsTrue(false);

            return default;
        }

        public void PrepareFreePlace(int count)
        {
            if (_buckets.Capacity - _buckets.Count() < count)
                _buckets.Capacity = math.ceilpow2(_buckets.Count() + count);

            if (_itemIDToBounds.Capacity - _itemIDToBounds.Count() < count)
                _itemIDToBounds.Capacity = math.ceilpow2(_itemIDToBounds.Count() + count);

            if (_itemIDToItem.Capacity - _itemIDToItem.Count() < count)
                _itemIDToItem.Capacity = math.ceilpow2(_itemIDToItem.Count() + count);
        }

        #endregion

        #region Querying Methods

        public void Query(int3 chunkIndex, NativeList<T> resultList)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif

            NativeParallelHashMap<int, byte> hashMapUnic = new NativeParallelHashMap<int, byte>(64, Allocator.Temp);
            uint hash = Hash(chunkIndex);

            if (_buckets.TryGetFirstValue(hash, out int item, out NativeParallelMultiHashMapIterator<uint> it) == false)
                return;

            do
            {
                if (hashMapUnic.TryAdd(item, 0))
                    resultList.Add(_itemIDToItem[item]);
            } while (_buckets.TryGetNextValue(out item, ref it));
        }

        /* Query items within bounds. */
        public void Query(Bounds bounds, NativeList<T> resultList)
        {
            Assert.IsTrue(resultList.IsCreated);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif
            bounds.Clamp(_data->WorldBounds);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            NativeHashSet<int> hashMapUnic = new NativeHashSet<int>(64, Allocator.Temp);
            int3 hashPosition = new int3(0F);

            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;

                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;

                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;

                        uint hash = Hash(hashPosition);

                        if (_buckets.TryGetFirstValue(hash, out int item, out NativeParallelMultiHashMapIterator<uint> it))
                        {
                            do
                                hashMapUnic.Add(item);
                            while (_buckets.TryGetNextValue(out item, ref it));
                        }
                    }
                }
            }

            ExtractValueFromHashMap(hashMapUnic, bounds, resultList);
        }

        public void Query(Bounds bounds, NativeList<int3> voxelIndexes)
        {
            Assert.IsTrue(voxelIndexes.IsCreated);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif
            bounds.Clamp(_data->WorldBounds);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);

            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;

                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;

                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;

                        voxelIndexes.Add(hashPosition);
                    }
                }
            }
        }

        public void Query(Bounds obbBounds, quaternion rotation, NativeList<T> resultList)
        {
            Assert.IsTrue(resultList.IsCreated);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif

            Bounds bounds = TransformBounds(in obbBounds, in rotation);
            bounds.Clamp(_data->WorldBounds);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            NativeHashSet<int> hashMapUnic = new NativeHashSet<int>(64, Allocator.Temp);

            int3 hashPosition = new int3(0F);

            //add offset for simplify logic and allowed pruning
            obbBounds.Size += _data->CellSize * 1F;

            quaternion inverseRotation = math.inverse(rotation);
            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;
                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;
                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;

                        float3 pos = GetPositionVoxel(hashPosition, true);

                        if (obbBounds.RayCastOBBFast(pos - new float3(_data->CellSize.x * 0.5F, 0F, 0F), Right, inverseRotation, _data->CellSize.x) ||
                            obbBounds.RayCastOBBFast(pos - new float3(0F, _data->CellSize.y * 0.5F, 0F), Up, inverseRotation, _data->CellSize.y) ||
                            obbBounds.RayCastOBBFast(pos - new float3(0F, 0F, _data->CellSize.z * 0.5F), Forward, inverseRotation, _data->CellSize.z))
                        {
                            uint hash = Hash(hashPosition);

                            if (_buckets.TryGetFirstValue(hash, out int item, out NativeParallelMultiHashMapIterator<uint> it))
                            {
                                do
                                    hashMapUnic.Add(item);
                                while (_buckets.TryGetNextValue(out item, ref it));
                            }
                        }
                    }
                }
            }

            ExtractValueFromHashMap(hashMapUnic, bounds, resultList);
        }

        public void Query(Bounds obbBounds, quaternion rotation, NativeList<int3> voxelIndexes)
        {
            Assert.IsTrue(voxelIndexes.IsCreated);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif

            Bounds bounds = TransformBounds(in obbBounds, in rotation);
            bounds.Clamp(_data->WorldBounds);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);

            //add offset for simplify logic and allowed pruning
            obbBounds.Size += _data->CellSize * 1F;

            quaternion inverseRotation = math.inverse(rotation);

            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;

                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;

                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;

                        float3 pos = GetPositionVoxel(hashPosition, true);

                        if (obbBounds.RayCastOBBFast(pos - new float3(_data->CellSize.x * 0.5F, 0F, 0F), Right, inverseRotation, _data->CellSize.x) ||
                            obbBounds.RayCastOBBFast(pos - new float3(0F, _data->CellSize.y * 0.5F, 0F), Up, inverseRotation, _data->CellSize.y) ||
                            obbBounds.RayCastOBBFast(pos - new float3(0F, 0F, _data->CellSize.z * 0.5F), Forward, inverseRotation, _data->CellSize.z))
                            voxelIndexes.Add(hashPosition);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Bounds TransformBounds(in Bounds boundTarget, in quaternion rotation)
        {
            Bounds b = new Bounds(boundTarget.Center, math.abs(math.mul(rotation, boundTarget.Size)));
            return b;
        }

        private void ExtractValueFromHashMap(NativeHashSet<int> hashMapUnic, Bounds bounds, NativeList<T> resultList)
        {
            using NativeHashSet<int>.Enumerator iterator = hashMapUnic.GetEnumerator();
            while (iterator.MoveNext())
            {
                int itemID = iterator.Current;

                _itemIDToBounds.TryGetValue(itemID, out Bounds b);

                if (bounds.Intersects(b) == false)
                    continue;

                resultList.Add(_itemIDToItem[itemID]);
            }
        }

        public bool RayCast(Ray ray, ref T item, float length = 99999F)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif

            _data->HasHit = false;

            _data->RayOrigin = ray.origin;
            _data->RayDirection = ray.direction;

            _voxelRay.RayCast(ref this, _data->RayOrigin, _data->RayDirection, length);

            if (_data->HasHit == false)
                return false;

            item = _itemIDToItem[_rayHitValue];
            return true;
        }

        public int QueryCount(int3 chunkIndex)
        {
            uint hash = Hash(chunkIndex);

            int counter = 0;

            if (_buckets.TryGetFirstValue(hash, out _, out NativeParallelMultiHashMapIterator<uint> it))
            {
                ++counter;
                while (_buckets.TryGetNextValue(out _, ref it))
                    ++counter;
            }

            return counter;
        }

        #endregion

        #region Hashing

        public static uint Hash(int3 cellIndex)
        {
            return math.hash(cellIndex);
        }

        private int GetCellCountFromSize(float3 size)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif

            float3 deltaSize = size / _data->CellSize;

            return (int) math.ceil(deltaSize.x) * (int) math.ceil(deltaSize.y) * (int) math.ceil(deltaSize.z);
        }

        public float3 GetPositionVoxel(int3 index, bool center)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif
            float3 pos = index * _data->CellSize + _data->WorldBoundsMin;

            if (center)
                pos += _data->CellSize * 0.5F;

            return pos;
        }

        public int3 GetIndexVoxel(float3 position)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif
            position -= _data->WorldBoundsMin;

            position /= _data->CellSize;

            return position.FloorToInt();
        }

        #endregion

        #region Implementation of IRay

        public bool OnTraversingVoxel(int3 voxelIndex)
        {
            if (math.any(voxelIndex > _data->CellCount)) //if voxel still in world
                return true;

            uint hash = Hash(voxelIndex);

            if (_buckets.TryGetFirstValue(hash, out int itemID, out NativeParallelMultiHashMapIterator<uint> it))
                do
                {
                    _itemIDToBounds.TryGetValue(itemID, out Bounds b);

                    if (b.GetEnterPositionAABB(_data->RayOrigin, _data->RayDirection, 1 << 25, out _) == false)
                        continue;

                    _data->HasHit = true;
                    _rayHitValue = itemID;

                    return true;
                } while (_buckets.TryGetNextValue(out itemID, ref it));

            return false;
        }

        public void GetIndexiesVoxel(T item, NativeList<int3> results)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ASH.CheckReadAndThrow(m_Safety);
#endif
            Bounds bounds = new Bounds(item.GetCenter(), item.GetSize());

            bounds.Clamp(_data->WorldBounds);

            CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

            int3 hashPosition = new int3(0F);

            for (int x = start.x; x < end.x; ++x)
            {
                hashPosition.x = x;

                for (int y = start.y; y < end.y; ++y)
                {
                    hashPosition.y = y;

                    for (int z = start.z; z < end.z; ++z)
                    {
                        hashPosition.z = z;

                        results.Add(hashPosition);
                    }
                }
            }
        }

        #endregion

        public Concurrent ToConcurrent()
        {
            return new Concurrent
            {
                _data = _data,
                _itemIDToBounds = _itemIDToBounds.AsParallelWriter(),
                _itemIDToItem = _itemIDToItem.AsParallelWriter(),
                _buckets = _buckets.AsParallelWriter(),

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_Safety = m_Safety
#endif
            };
        }

        #region Variables

        private static readonly float3 Forward = new float3(0F, 0F, 1F);
        private static readonly float3 Up = new float3(0F, 1F, 0F);
        private static readonly float3 Right = new float3(1F, 0F, 0F);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        // ReSharper disable InconsistentNaming
        private ASH m_Safety;
        [NativeSetClassTypeToNullOnSchedule]
        private DisposeSentinel m_DisposeSentinel;
        // ReSharper restore InconsistentNaming
#endif

        private Allocator _allocatorLabel;

        [NativeDisableUnsafePtrRestriction]
        private SpatialIndexData* _data;
        private NativeParallelMultiHashMap<uint, int> _buckets;        //4
        private NativeParallelHashMap<int, Bounds> _itemIDToBounds; //4
        private NativeParallelHashMap<int, T> _itemIDToItem;   //4

        private NativeParallelHashSet<int3> _helpMoveHashMapOld;
        private NativeParallelHashSet<int3> _helpMoveHashMapNew;
        private VoxelRay<SpatialIndex<T>> _voxelRay;
        private int _rayHitValue;

        #endregion

        #region Proprieties

        public readonly bool IsCreated => _data != null;

        public int ItemCount
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ASH.CheckReadAndThrow(m_Safety);
#endif
                return _itemIDToBounds.Count();
            }
        }

        public readonly int BucketItemCount
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ASH.CheckReadAndThrow(m_Safety);
#endif
                return _buckets.Count();
            }
        }

        public float3 CellSize
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ASH.CheckReadAndThrow(m_Safety);
#endif
                return _data->CellSize;
            }
        }

        public Bounds WorldBounds
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ASH.CheckReadAndThrow(m_Safety);
#endif
                return _data->WorldBounds;
            }
        }

        public int3 CellCount
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ASH.CheckReadAndThrow(m_Safety);
#endif
                return _data->CellCount;
            }
        }

#if UNITY_EDITOR
        public readonly NativeParallelMultiHashMap<uint, int> DebugBuckets => _buckets;
        public readonly NativeParallelHashMap<int, T> DebugIDToItem => _itemIDToItem;
        public Bounds DebugRayCastBounds => _data->RayCastBound;
        public readonly VoxelRay<SpatialIndex<T>> DebugVoxelRay => _voxelRay;
#endif

        #endregion

        public struct Concurrent
        {
            public bool TryAdd(ref T item)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ASH.CheckWriteAndThrow(m_Safety);
#endif

                Bounds bounds = new Bounds(item.GetCenter(), item.GetSize());

                bounds.Clamp(_data->WorldBounds);

                int itemID = Interlocked.Increment(ref _data->Counter);
                item.SpatialHashingIndex = itemID;

                if (_itemIDToBounds.TryAdd(itemID, bounds) == false || _itemIDToItem.TryAdd(itemID, item) == false)
                    return false;

                CalculateStartEndIterationInternal(_data, bounds, out int3 start, out int3 end);

                int3 hashPosition = new int3(0F);

                for (int x = start.x; x < end.x; ++x)
                {
                    hashPosition.x = x;

                    for (int y = start.y; y < end.y; ++y)
                    {
                        hashPosition.y = y;

                        for (int z = start.z; z < end.z; ++z)
                        {
                            hashPosition.z = z;

                            uint hash = Hash(hashPosition);
                            _buckets.Add(hash, itemID);
                        }
                    }
                }

                return true;
            }

            #region Variables

            [NativeDisableUnsafePtrRestriction]
            internal SpatialIndexData* _data;

            internal NativeParallelMultiHashMap<uint, int>.ParallelWriter _buckets;        //4
            internal NativeParallelHashMap<int, Bounds>.ParallelWriter _itemIDToBounds; //4
            internal NativeParallelHashMap<int, T>.ParallelWriter _itemIDToItem;   //4

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            internal ASH m_Safety;
#endif

            [NativeSetThreadIndex]
            internal int _threadIndex;

            #endregion
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpatialIndexData
    {
        public Bounds WorldBounds;
        public float3 WorldBoundsMin;

        public Bounds RayCastBound;
        public float3 CellSize;    

        public float3 RayOrigin;   
        public float3 RayDirection;
        public int3 CellCount;   

        public int Counter;
        public bool HasHit;
    }

    public interface ISpatialHashingItem<T> : IEquatable<T>
    {
        float3 GetCenter();

        float3 GetSize();

        int SpatialHashingIndex { get; set; }
    }
}