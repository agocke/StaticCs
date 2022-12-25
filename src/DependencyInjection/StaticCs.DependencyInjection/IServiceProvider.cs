
namespace StaticCs.DependencyInjection;

public interface IServiceProvider<T>
{
    static abstract T GetService();
}

public static class ServiceProvider
{
    public static T GetService<P, T>() where P : IServiceProvider<T>
        => P.GetService();
}
