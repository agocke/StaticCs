
struct Option<T>
{
    public enum Tag
    {
        Some,
        None
    }
    private readonly T _value;

}