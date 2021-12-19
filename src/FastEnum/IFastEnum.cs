
using System.Diagnostics.CodeAnalysis;

namespace FastEnum
{
    public static class LinqExtensions
    {
        public static IEnumerable<TEnum, T, TIter>.WhereImpl Where<TEnum, T, TIter>(this TEnum e, Func<T, bool> pred)
            where TEnum : IEnumerable<TEnum, T, TIter>
            => new IEnumerable<TEnum, T, TIter>.WhereImpl(e, pred);

        public static IEnumerable<TEnum, T, TIter>.SelectImpl<U> Select<TEnum, T, TIter, U>(this TEnum e, Func<T, U> map)
            where TEnum : IEnumerable<TEnum, T, TIter>
            => new IEnumerable<TEnum, T, TIter>.SelectImpl<U>(e, map);
    }
    public interface IEnumerable<TEnum, T, TIterator>
        where TEnum : IEnumerable<TEnum, T, TIterator>
    {
        abstract static TIterator Start { get; }
        bool TryGetNext(ref TIterator iter, [MaybeNullWhen(false)] out T item);

        public struct WhereImpl : IEnumerable<WhereImpl, T, TIterator>
        {
            private TEnum _e;
            private readonly Func<T, bool> _pred;

            public WhereImpl(TEnum e, Func<T, bool> pred)
            {
                _e = e;
                _pred = pred;
            }

            public static TIterator Start => TEnum.Start;

            public bool TryGetNext(ref TIterator iter, [MaybeNullWhen(false)] out T item)
            {
                while (_e.TryGetNext(ref iter, out item))
                {
                    if (_pred(item))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public struct SelectImpl<U> : IEnumerable<SelectImpl<U>, U, TIterator>
        {
            private TEnum _e;
            private readonly Func<T, U> _map;

            public static TIterator Start => TEnum.Start;

            public SelectImpl(TEnum e, Func<T, U> map)
            {
                _e = e;
                _map = map;
            }

            public bool TryGetNext(ref TIterator iter, [MaybeNullWhen(false)] out U item)
            {
                T? input;
                if (_e.TryGetNext(ref iter, out input))
                {
                    item = _map(input);
                    return true;
                }
                item = default;
                return false;
            }
        }
    }
}