
using BenchmarkDotNet.Engines;
using FastEnum;

namespace System.Linq.Tests
{
    internal static class ConsumerExt
    {
        public static void ConsumeFast<T, TEnumerable, TEnumerator>(this IEnumerable<T, TEnumerator> enumerable, Consumer consumer)
            where TEnumerable : IEnumerable<T, TEnumerator>
            where TEnumerator : IFastEnumerator<T>
        {
            var e = enumerable.GetEnumerator();
            while (e.TryGetNext(out var item))
            {
                consumer.Consume(in item);
            }
        }

        public static void ConsumeFast<T, TEnumerator>(this TEnumerator enumerator, Consumer consumer)
            where TEnumerator : IFastEnumerator<T>
        {
            while (enumerator.TryGetNext(out var item))
            {
                consumer.Consume(in item);
            }
        }

        public static void Consume<TEnum, T, TIter>(this TEnum enumerable, Consumer consumer)
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