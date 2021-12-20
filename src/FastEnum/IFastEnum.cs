
using System.Diagnostics.CodeAnalysis;

namespace FastEnum
{
    public static class LinqExtensions
    {
        public static WhereEnumerable<T, TEnumerable, TEnumerator> Where<T, TEnumerable, TEnumerator>(this TEnumerable e, Func<T, bool> pred)
            where TEnumerator : IFastEnumerator<T>
            where TEnumerable : IEnumerable<T, TEnumerator>
            => new WhereEnumerable<T, TEnumerable, TEnumerator>(e, pred);

        public static SelectEnumerable<T, U, TEnumerable, TEnumerator> Select<T, U, TEnumerable, TEnumerator>(this TEnumerable e, Func<T, U> pred)
            where TEnumerable : IEnumerable<T, TEnumerator>
            where TEnumerator : IFastEnumerator<T>
            => new SelectEnumerable<T, U, TEnumerable, TEnumerator>(e, pred);

        public static IEnumerable<TEnum, T, TIter>.WhereImpl Where<TEnum, T, TIter>(this TEnum e, Func<T, bool> pred)
            where TEnum : IEnumerable<TEnum, T, TIter>
            => new IEnumerable<TEnum, T, TIter>.WhereImpl(e, pred);

        public static IEnumerable<TEnum, T, TIter>.SelectImpl<U> Select<TEnum, T, TIter, U>(this TEnum e, Func<T, U> map)
            where TEnum : IEnumerable<TEnum, T, TIter>
            => new IEnumerable<TEnum, T, TIter>.SelectImpl<U>(e, map);
    }

    public readonly struct WhereEnumerable<T, TEnumerable, TEnumerator> : IEnumerable<T, WhereEnumerator<T, TEnumerator>>
        where TEnumerable : IEnumerable<T, TEnumerator>
        where TEnumerator : IFastEnumerator<T>
    {
        private readonly TEnumerable _e;
        private readonly Func<T, bool> _pred;
        public WhereEnumerable(TEnumerable e, Func<T, bool> pred)
        {
            _e = e;
            _pred = pred;
        }

        public WhereEnumerator<T, TEnumerator> GetEnumerator() => new WhereEnumerator<T, TEnumerator>(_e.GetEnumerator(), _pred);
    }

    public struct WhereEnumerator<T, TEnumerator> : IFastEnumerator<T>
        where TEnumerator : IFastEnumerator<T>
    {
        private readonly Func<T, bool> _pred;
        private TEnumerator _e;

        public WhereEnumerator(TEnumerator e, Func<T, bool> pred)
        {
            _e = e;
            _pred = pred;
        }

        public bool TryGetNext([MaybeNullWhen(false)] out T item)
        {
            while (_e.TryGetNext(out item))
            {
                if (_pred(item))
                {
                    return true;
                }
            }
            item = default;
            return false;
        }
    }

    public readonly struct SelectEnumerable<T, U, TEnumerable, TEnumerator> : IEnumerable<U, SelectEnumerator<T, U, TEnumerable, TEnumerator>>
        where TEnumerable : IEnumerable<T, TEnumerator>
        where TEnumerator : IFastEnumerator<T>
    {
        private readonly TEnumerable _e;
        private readonly Func<T, U> _map;
        public SelectEnumerable(TEnumerable e, Func<T, U> map)
        {
            _e = e;
            _map = map;
        }

        public SelectEnumerator<T, U, TEnumerable, TEnumerator> GetEnumerator()
            => new SelectEnumerator<T, U, TEnumerable, TEnumerator>(_e.GetEnumerator(), _map);
    }

    public struct SelectEnumerator<T, U, TEnumerable, TEnumerator> : IFastEnumerator<U>
        where TEnumerator : IFastEnumerator<T>
    {
        private readonly Func<T, U> _map;
        private TEnumerator _e;
        public SelectEnumerator(TEnumerator e, Func<T, U> map)
        {
            _map = map;
            _e = e;
        }

        public bool TryGetNext([MaybeNullWhen(false)] out U item)
        {
            T? prev;
            if (_e.TryGetNext(out prev))
            {
                item = _map(prev);
                return true;
            }
            item = default;
            return false;
        }
    }

    public interface IEnumerable<T, TEnumerator>
        where TEnumerator : IFastEnumerator<T>
    {
        TEnumerator GetEnumerator();
    }

    public interface IFastEnumerator<T>
    {
        bool TryGetNext([MaybeNullWhen(false)] out T item);
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