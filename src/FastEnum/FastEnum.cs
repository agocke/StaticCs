
namespace FastEnum
{
    public static class FastEnum
    {
        public static FastRange Range(int start, int count) => new FastRange(start, count);

        public readonly struct FastRange : IEnumerable<FastRange, int, int>
        {
            private readonly int _start;
            private readonly int _count;
            public FastRange(int start, int count)
            {
                _start = start;
                _count = count;
            }

            int IEnumerable<FastRange, int, int>.Start => 0;

            bool IEnumerable<FastRange, int, int>.TryGetNext(ref int index, out int item)
            {
                if (index >= _count)
                {
                    item = -1;
                    return false;
                }
                item = _start + index++;
                return true;
            }
        }
    }
}