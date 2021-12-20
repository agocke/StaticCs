
using BenchmarkDotNet.Engines;
using FastEnum;

namespace System.Linq.Tests
{
    internal static class ConsumerExt
    {
        public static void Consume<TEnum, T, TIter>(this IEnumerable<TEnum, T, TIter> enumerable, Consumer consumer)
            where TEnum : IEnumerable<TEnum, T, TIter>
        {
            var iter = enumerable.Start;
            while (enumerable.TryGetNext(ref iter, out var item))
            {
                consumer.Consume(in item);
            }
        }
    }
}