
struct Option<T>
{
    public enum Tag
    {
        Some,
        None
    }
    private readonly T _value;

    public void Deconstruct(out Tag tag, out T value) => 
}