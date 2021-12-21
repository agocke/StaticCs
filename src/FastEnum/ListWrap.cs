
using System.Diagnostics.CodeAnalysis;

namespace FastEnum
{
    public class ListWrap<T> : List<T>
    {
        public ListWrap(List<T> list) : base(list) { }
    }

    public readonly struct IListWrap<TList, T> : IEnumerable<IListWrap<TList, T>, T, int>
        where TList : IList<T>
    {
        private readonly TList _list;
        public IListWrap(TList list)
        {
            _list = list;
        }
        public int Start => 0;

        public bool TryGetNext(ref int index, [MaybeNullWhen(false)] out T item)
        {
            if (index >= _list.Count) {
                item = default(T);
                return false;
            }

            item = _list[index++];
            return true;
        }
    }
}