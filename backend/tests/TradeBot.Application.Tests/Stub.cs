using System.Collections;
using System.Reflection;

namespace TradeBot.Application.Tests;

/// <summary>Interface fake: per-method handlers by name, recorded calls, and empty/default results for everything else.</summary>
public class Stub<T> : DispatchProxy where T : class
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = [];
    private readonly List<(string Method, object?[] Args)> _calls = [];

    public T Object => (T)(object)this;

    public static Stub<T> Create() => (Stub<T>)(object)DispatchProxy.Create<T, Stub<T>>();

    public Stub<T> On(string method, Func<object?[], object?> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    public List<object?[]> Calls(string method)
    {
        lock (_calls) return _calls.Where(c => c.Method == method).Select(c => c.Args).ToList();
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var m = method!;
        args ??= [];
        lock (_calls) _calls.Add((m.Name, args));
        var result = _handlers.TryGetValue(m.Name, out var h) ? h(args) : null;
        return Wrap(m.ReturnType, result);
    }

    private static object? Wrap(Type returnType, object? result)
    {
        if (returnType == typeof(void)) return null;
        if (result is Task) return result;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            var value = result ?? Default(inner);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        return result ?? Default(returnType);
    }

    private static object? Default(Type t) =>
        t.IsValueType ? Activator.CreateInstance(t)
        : t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t) && !t.IsInterface ? Activator.CreateInstance(t)
        : null;
}
