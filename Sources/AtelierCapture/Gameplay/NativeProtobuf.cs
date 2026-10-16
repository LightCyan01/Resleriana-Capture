using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static class NativeProtobuf
{
    private static MethodInfo? serialize;

    // GetInterfaces copies an array per call; snapshots ask this for every native node.
    private static readonly ConcurrentDictionary<Type, bool> Messages = new();

    internal static bool IsMessage(Type type) => Messages.GetOrAdd(type, ComputeIsMessage);

    private static bool ComputeIsMessage(Type type) =>
        !type.IsEnum
        && (
            type.FullName == "Google.Protobuf.IMessage"
            || type.GetInterfaces().Any(t => t.FullName == "Google.Protobuf.IMessage")
            || type.FullName?.StartsWith("Blend.ApiSchema.Api.", StringComparison.Ordinal) == true
            || type.FullName?.StartsWith("Blend.ApiSchema.Model.", StringComparison.Ordinal) == true
            || type.FullName?.StartsWith(
                "Google.Protobuf.WellKnownTypes.",
                StringComparison.Ordinal
            ) == true
        );

    internal static bool IsMessageCollection(Type type) =>
        type.IsConstructedGenericType
        && type.GetGenericTypeDefinition().FullName == "Google.Protobuf.Collections.RepeatedField`1"
        && IsMessage(type.GenericTypeArguments[0]);

    internal static byte[] Copy(object message)
    {
        serialize ??= NativeContract.Bind(
            NativeContract.ResolveType("Google.Protobuf.MessageExtensions"),
            "ToByteArray",
            0x7E92E00,
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1<System.Byte>",
            ["Google.Protobuf.IMessage"],
            true
        );
        var parameter = serialize.GetParameters()[0].ParameterType;
        var argument = parameter.IsInstanceOfType(message)
            ? message
            : NativeObjectType.WrapPointer(parameter, ((Il2CppObjectBase)message).Pointer);
        Il2CppStructArray<byte>? result = null;
        try
        {
            result =
                serialize.Invoke(null, [argument]) as Il2CppStructArray<byte>
                ?? throw new InvalidDataException(
                    "Native protobuf serialization returned no bytes."
                );
            return result.AsSpan().ToArray();
        }
        finally
        {
            GC.KeepAlive(result);
            GC.KeepAlive(argument);
            GC.KeepAlive(message);
        }
    }
}
