using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace SpatialHashing
{
    /* Various math utility functions; nothing nontrivial here */
    public static class MathHelpExtension
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 CeilToInt(this float2 x)
        {
            return new int2(math.ceil(x));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 CeilToInt(this float3 x)
        {
            return new int3(math.ceil(x));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 FloorToInt(this float2 x)
        {
            return new int2(math.floor(x));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 FloorToInt(this float3 x)
        {
            return new int3(math.floor(x));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Sum(this int2 x)
        {
            return math.csum(x);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Sum(this int3 x)
        {
            return math.csum(x);
        }

        /* Elementwise multiply*/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Multiply(this float2 x)
        {
            return x.x * x.y;
        }

        /* Elementwise multiply*/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Multiply(this float3 x)
        {
            return x.x * x.y * x.z;
        }

        /* Elementwise multiply*/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Multiply(this int2 x)
        {
            return x.x * x.y;
        }

        /* Elementwise multiply*/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Multiply(this int3 x)
        {
            return x.x * x.y * x.z;
        }
    }
}