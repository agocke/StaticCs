
using System.Diagnostics.CodeAnalysis;

namespace FastEnum
{
    public record ListWrap<T>(List<T> Value) : IEnumerable<ListWrap<T>, T, int>
    {
        public static int Start => 0;

        public bool TryGetNext(ref int index, [MaybeNullWhen(false)] out T item)
        {
            if (index >= Value.Count) { 
                item = default(T);
                return false;
            }
            
            item = Value[index++];
            return true;
        }
    }
}