using System;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;

using Quat = Unity.Mathematics.quaternion;

namespace SpatialHashing
{
    /* Class representing an axis-aligned bounding box in 3 dimensions */
    [Serializable]
    public struct Bounds : IEquatable<Bounds>
    {
        [SerializeField]
        private float3 _center;
        [SerializeField]
        private float3 _extents;

        public Bounds(float3 center, float3 size)
        {
            _center = center;
            _extents = size * 0.5f;
        }

        /* Properties */

        public float3 Center { readonly get { return _center; } set { _center = value; } }

        public float3 Size { readonly get { return _extents * 2f; } set { _extents = value * 0.5f; } }

        public float3 Extents { readonly get { return _extents; } set { _extents = value; } }

        public float3 Min { readonly get { return Center - Extents; } set { SetMinMax(value, Max); } }

        public float3 Max { readonly get { return Center + Extents; } set { SetMinMax(Min, value); } }

        public void SetMinMax(float3 min, float3 max)
        {
            Extents = (max - min) * 0.5f;
            Center = min + Extents;
        }

        /* Extend to include a point. */
        public void Encapsulate(float3 point)
        {
            SetMinMax(math.min(Min, point), math.max(Max, point));
        }

        /* Extend to include another set of bounds. */
        public void Encapsulate(Bounds bounds)
        {
            Encapsulate(bounds.Center - bounds.Extents);
            Encapsulate(bounds.Center + bounds.Extents);
        }

        /* Shrink to within bounds. */
        public void Clamp(Bounds bounds)
        {
            float3 bMin = bounds.Min;
            float3 bMax = bounds.Max;
            SetMinMax(math.clamp(Min, bMin, bMax), math.clamp(Max, bMin, bMax));
        }

        public void Expand(float3 amount)
        {
            Extents += amount * 0.5f;
        }

        public void Expand(float amount)
        {
            Expand(new float3(amount, amount, amount));
        }

        public readonly bool Intersects(Bounds bounds)
        {
            return math.all(Min <= bounds.Max) && math.all(Max >= bounds.Min);
        }

        public readonly int3 GetCellCount(float3 cellSize)
        {
            float3 min = Min;
            float3 max = Max;

            float3 diff = max - min;
            diff /= cellSize;

            return diff.CeilToInt();
        }

        public bool RayCastOBB(Ray ray, Quat worldRotation)
        {
            return RayCastOBB(ray.origin, ray.direction, worldRotation);
        }

        public bool RayCastOBB(float3 origin, float3 directionNormalized, Quat worldRotation, float length = 1 << 25)
        {
            Quat localRot = math.inverse(worldRotation);
            return RayCastOBBFast(origin, directionNormalized, localRot, length);
        }

        public bool RayCastOBBFast(float3 origin, float3 directionNormalized, Quat localRotation, float length = 1 << 25)
        {
            origin = math.mul(localRotation, origin - _center) + _center;
            directionNormalized = math.mul(localRotation, directionNormalized);

            return GetEnterPositionAABB(origin, directionNormalized, length);
        }

        public bool RayCastOBB(float3 origin, float3 directionNormalized, Quat worldRotation, out float3 enterPoint, float length = 1 << 25)
        {
            Quat localRotation = math.inverse(worldRotation);
            origin = math.mul(localRotation, origin - _center) + _center;
            directionNormalized = math.mul(localRotation, directionNormalized);

            bool res = GetEnterPositionAABB(origin, directionNormalized, length, out enterPoint);
            enterPoint = math.mul(worldRotation, enterPoint);

            return res;
        }

        public bool GetEnterPositionAABB(Ray ray, float length, out float3 enterPoint)
        {
            return GetEnterPositionAABB(ray.origin, ray.direction, length, out enterPoint);
        }

        /* 
         * Find the intersection of a line segment (specified by origin, direction, length) and an AABB
         */
        public readonly bool GetEnterPositionAABB(float3 origin, float3 direction, float length, out float3 enterPoint)
        {
            enterPoint = new float3();
            float3 start = origin + direction * length;

            float low = 0F;
            float high = 1F;

            if (ClipLine(0, origin, start, ref low, ref high) == false || ClipLine(1, origin, start, ref low, ref high) == false || ClipLine(2, origin, start, ref low, ref high) == false)
                return false;

            // The formula for I: http://youtu.be/USjbg5QXk3g?t=6m24s
            float3 b = start - origin;
            enterPoint = origin + b * low;

            return true;
        }

        /*
         * Find the intersection of a line segment (specified by origin, direction, length) and an AABB
         * Won't return the collision point.
         */
        public readonly bool GetEnterPositionAABB(float3 origin, float3 direction, float length)
        {
            return GetEnterPositionAABB(origin, direction, length, out _);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool ClipLine(int d, float3 v0, float3 v1, ref float low, ref float high)
        {
            // f_low and f_high are the prior results from clipping, and are updated with each call.

            // f_dim_low and f_dim_high are the results we're calculating for the current dimension.
            float dimensionLow = (Min[d] - v0[d]) / (v1[d] - v0[d]);
            float dimensionHigh = (Max[d] - v0[d]) / (v1[d] - v0[d]);

            // Ensure low < high
            if (dimensionHigh < dimensionLow)
            {
                float tmp = dimensionHigh;
                dimensionHigh = dimensionLow;
                dimensionLow = tmp;
            }

            // If this dimension's high is less than the low we got then we definitely missed.
            if (dimensionHigh < low)
                return false;

            // Likewise if the low is less than the high.
            if (dimensionLow > high)
                return false;

            // Add the clip from this dimension to the previous results
            low = math.max(dimensionLow, low);
            high = math.min(dimensionHigh, high);

            if (low > high)
                return false;

            return true;
        }

        /*
         * Return the ray's exit position fro these bounds; the ray must originate within the bounds.
         */
        public readonly bool GetExitPosition(Ray ray, float length, out float3 exitPoint)
        {
            exitPoint = new float3();

            float3 minBounds = Min;
            float3 maxBounds = Max;

            float3 rayProjectionLength = ray.direction * length;

            float3 minProjection = (minBounds - (float3) ray.origin) / rayProjectionLength;
            float3 maxProjection = (maxBounds - (float3) ray.origin) / rayProjectionLength;
            float3 temp = math.min(minProjection, maxProjection);
            maxProjection = math.max(minProjection, maxProjection);
            minProjection = temp;

            if (minProjection.x > maxProjection.y || minProjection.y > maxProjection.x)
                return false;

            float tMin = math.max(minProjection.x, minProjection.y); //Get Greatest Min
            float tMax = math.min(maxProjection.x, maxProjection.y); //Get Smallest Max

            if (tMin > maxProjection.z || minProjection.z > tMax)
                return false;

            tMax = math.min(maxProjection.z, tMax);

            exitPoint = ray.origin + ray.direction * length * tMax;
            return true;
        }

        public override readonly string ToString()
        {
            return $"Center: {_center}, Extents: {_extents}";
        }

        public override readonly int GetHashCode()
        {
            return Center.GetHashCode() ^ (Extents.GetHashCode() << 2);
        }

        public override readonly bool Equals(object other)
        {
            if (other is not Bounds)
                return false;
            return Equals((Bounds) other);
        }

        public readonly bool Equals(Bounds other)
        {
            return Center.Equals(other.Center) && Extents.Equals(other.Extents);
        }

        public static bool operator ==(Bounds lhs, Bounds rhs)
        {
            return math.all(lhs.Center == rhs.Center) & math.all(lhs.Extents == rhs.Extents);
        }

        public static bool operator !=(Bounds lhs, Bounds rhs)
        {
            return !(lhs == rhs);
        }

    }


}