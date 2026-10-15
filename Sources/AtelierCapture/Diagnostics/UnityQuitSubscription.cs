using System.Reflection;
using Il2CppInterop.Runtime;

namespace AtelierCapture;

internal sealed class UnityQuitSubscription : IDisposable
{
    private readonly Action managedCallback;
    private readonly object nativeCallback;
    private readonly MethodInfo remove;
    private int disposed;

    internal UnityQuitSubscription(
        MethodInfo add,
        MethodInfo remove,
        Action managedCallback,
        object nativeCallback
    )
    {
        this.managedCallback = managedCallback;
        this.nativeCallback = nativeCallback;
        this.remove = remove;
        add.Invoke(null, [nativeCallback]);
    }

    internal static UnityQuitSubscription Attach(
        RuntimeCompatibility compatibility,
        Action callback
    )
    {
        var application = Plugin.ResolveType("UnityEngine.Application");
        var parameters = new[] { typeof(Il2CppSystem.Action) };
        var add =
            application.GetMethod(
                "add_quitting",
                BindingFlags.Public | BindingFlags.Static,
                null,
                parameters,
                null
            ) ?? throw new MissingMethodException("UnityEngine.Application.add_quitting");
        var remove =
            application.GetMethod(
                "remove_quitting",
                BindingFlags.Public | BindingFlags.Static,
                null,
                parameters,
                null
            ) ?? throw new MissingMethodException("UnityEngine.Application.remove_quitting");
        compatibility.ValidateMethod(add, 0x91B7E80, "System.Void", ["Il2CppSystem.Action"], true);
        compatibility.ValidateMethod(
            remove,
            0x91B7FD0,
            "System.Void",
            ["Il2CppSystem.Action"],
            true
        );
        var native =
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(callback)
            ?? throw new InvalidOperationException("Unity quit callback conversion failed.");
        return new UnityQuitSubscription(add, remove, callback, native);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        remove.Invoke(null, [nativeCallback]);
        GC.KeepAlive(managedCallback);
        GC.KeepAlive(nativeCallback);
    }
}
