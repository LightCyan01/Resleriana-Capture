using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NativeHeaders = Il2CppSystem.Collections.Generic.Dictionary<
    string,
    Il2CppSystem.Collections.Generic.List<string>
>;
using NativeValues = Il2CppSystem.Collections.Generic.List<string>;

namespace AtelierCapture;

internal sealed class NativeApiBindings
{
    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal MethodInfo Send { get; }
    internal MethodInfo Dispose { get; }
    internal MethodInfo State { get; }
    internal MethodInfo EntityBody { get; }
    internal MethodInfo SendHeaders { get; }
    internal MethodInfo WriteHeaderGroup { get; }
    private readonly MethodInfo requestHeaders,
        responseHeaders,
        requestUri,
        originalUri,
        requestMethod,
        requestStreaming,
        requestException,
        requestUploadStream,
        responseStatus,
        responseStreaming;
    internal MethodInfo Callback { get; }
    internal MethodInfo Decoder { get; }
    internal MethodInfo Merge { get; }
    private readonly MethodInfo serialize;
    internal ConstructorInfo Transport { get; }
    internal ConstructorInfo Descriptor { get; }
    private readonly PropertyInfo descriptorId,
        descriptorRoute,
        descriptorBody;
    private readonly PropertyInfo transportDescriptor,
        transportRequest,
        responseRequest;

    internal NativeApiBindings()
    {
        if (!Plugin.Compatibility.ApiVerified)
            throw new InvalidDataException("Native API compatibility is unverified.");
        var http = NativeContract.ResolveType("BestHTTP.HTTPRequest");
        var response = NativeContract.ResolveType("BestHTTP.HTTPResponse");
        var transport = NativeContract.ResolveType("PMLKMPHGENK");
        var descriptor = NativeContract.ResolveType("IAIMAAJBIOO");
        var message = NativeContract.ResolveType("Google.Protobuf.IMessage");
        var crypto = NativeContract.ResolveType("MJEDHIOLBGP");
        Send = Method(http, "Send", http, false, 0x9A67A0);
        Dispose = Method(http, "Dispose", typeof(void), false, 0x9A6D50);
        State = Method(
            http,
            "set_State",
            typeof(void),
            false,
            0x9A13D0,
            NativeContract.ResolveType("BestHTTP.HTTPRequestStates")
        );
        EntityBody = Method(
            http,
            "GetEntityBody",
            typeof(Il2CppStructArray<byte>),
            false,
            0x9A55D0
        );
        requestHeaders = Method(http, "get_Headers", typeof(NativeHeaders), false, 0x9A1F60);
        responseHeaders = Method(response, "get_Headers", typeof(NativeHeaders), false, 0x8C90C0);
        requestMethod = Method(
            http,
            "get_MethodType",
            NativeContract.ResolveType("BestHTTP.HTTPMethods"),
            false,
            0x9A09D0
        );
        requestStreaming = Method(http, "get_UseStreaming", typeof(bool), false, 0x9A1F00);
        requestException = Method(
            http,
            "get_Exception",
            typeof(Il2CppSystem.Exception),
            false,
            0x8B36D0
        );
        requestUploadStream = Method(
            http,
            "get_UploadStream",
            typeof(Il2CppSystem.IO.Stream),
            false,
            0x89E290
        );
        responseStatus = Method(response, "get_StatusCode", typeof(int), false, 0x931050);
        responseStreaming = Method(response, "get_IsStreamed", typeof(bool), false, 0x8A7820);
        responseRequest = Property(response, "baseRequest", http);
        var uri = NativeContract.ResolveType("Il2CppSystem.Uri");
        requestUri = Method(http, "get_Uri", uri, false, 0x8A56A0);
        originalUri = Method(uri, "get_OriginalString", typeof(string), false, 0x8A15EE0);
        SendHeaders = Method(
            http,
            "SendHeaders",
            typeof(void),
            false,
            0x9A5260,
            typeof(Il2CppSystem.IO.Stream)
        );
        var headerOwner =
            http.GetNestedTypes(Flags)
                .SingleOrDefault(type =>
                    type.Name is "<>c__DisplayClass243_0" or "__c__DisplayClass243_0"
                )
            ?? throw new MissingMemberException(
                "Exact native SendHeaders closure owner is unavailable."
            );
        var headerMethod =
            headerOwner
                .GetMethods(Flags)
                .SingleOrDefault(method =>
                    method.Name is "<SendHeaders>b__0" or "_SendHeaders_b__0"
                )
            ?? throw new MissingMemberException(
                "Exact native SendHeaders callback is unavailable."
            );
        WriteHeaderGroup = Method(
            headerOwner,
            headerMethod.Name,
            typeof(void),
            false,
            0x9A72B0,
            typeof(string),
            typeof(NativeValues)
        );
        var callback =
            transport.GetNestedType("INPLGDJFDNN", Flags)
            ?? throw new MissingMemberException("Native API callback owner is unavailable.");
        Callback = Method(callback, "LCPDHIHNGEP", typeof(void), false, 0x4BBE0B0, http, response);
        Decoder = Method(
            transport,
            "AMIKMFABIFK",
            typeof(Il2CppStructArray<byte>),
            true,
            0x4BBB3D0,
            response,
            crypto
        );
        Merge = Method(
            NativeContract.ResolveType("Google.Protobuf.MessageExtensions"),
            "MergeFrom",
            typeof(void),
            true,
            0x7E93AA0,
            message,
            typeof(Il2CppStructArray<byte>),
            typeof(bool),
            NativeContract.ResolveType("Google.Protobuf.ExtensionRegistry")
        );
        serialize = Method(
            NativeContract.ResolveType("Google.Protobuf.MessageExtensions"),
            "ToByteArray",
            typeof(Il2CppStructArray<byte>),
            true,
            0x7E92E00,
            message
        );
        Descriptor =
            descriptor.GetConstructor(
                Flags,
                null,
                new[] { typeof(string), typeof(bool), message },
                null
            )
            ?? throw new MissingMethodException("Native immutable API descriptor is unavailable.");
        Plugin.Compatibility.ValidateConstructor(
            Descriptor,
            0x8B3D60,
            Descriptor
                .GetParameters()
                .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
                .ToArray()
        );
        descriptorId = Property(descriptor, "_GIHPCONIAHO_k__BackingField", typeof(string));
        descriptorRoute = Property(descriptor, "_AAAPBOIJFLF_k__BackingField", typeof(string));
        descriptorBody = Property(
            descriptor,
            "_IFCBEHFOCJA_k__BackingField",
            typeof(Il2CppStructArray<byte>)
        );
        transportDescriptor = Property(transport, "HPKFLOPHIEI", descriptor);
        transportRequest = Property(transport, "FHFCKPIAGNN", http);
        Transport =
            transport.GetConstructor(
                Flags,
                null,
                new[] { descriptor, typeof(string), crypto },
                null
            )
            ?? throw new MissingMethodException("Native API transport constructor is unavailable.");
        Plugin.Compatibility.ValidateConstructor(
            Transport,
            0x4BBB9D0,
            Transport
                .GetParameters()
                .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
                .ToArray()
        );
    }

    internal string? ReadDescriptorId(object value) => (string?)descriptorId.GetValue(value);

    internal byte[] ReadMessageBinary(object message)
    {
        var nativeBytes =
            serialize.Invoke(null, [message]) as Il2CppStructArray<byte>
            ?? throw new InvalidDataException("Native message serialization returned no bytes.");
        try
        {
            return nativeBytes.AsSpan().ToArray();
        }
        finally
        {
            GC.KeepAlive(nativeBytes);
            GC.KeepAlive(message);
        }
    }

    internal string? ReadDescriptorRoute(object value) => (string?)descriptorRoute.GetValue(value);

    internal Il2CppStructArray<byte>? ReadDescriptorBody(object value) =>
        (Il2CppStructArray<byte>?)descriptorBody.GetValue(value);

    internal NativeHeaders? ReadHeaders(object owner, bool response) =>
        (NativeHeaders?)(response ? responseHeaders : requestHeaders).Invoke(owner, null);

    internal ResponseEntityFraming ReadResponseFraming(object response, HeaderSnapshot headers)
    {
        var request =
            responseRequest.GetValue(response)
            ?? throw new InvalidDataException("Original response owner is unavailable.");
        try
        {
            return new ResponseEntityFraming(
                requestMethod.Invoke(request, null)?.ToString(),
                (int)responseStatus.Invoke(response, null)!,
                (bool)requestStreaming.Invoke(request, null)!,
                (bool)responseStreaming.Invoke(response, null)!,
                requestException.Invoke(request, null) is not null,
                headers
            );
        }
        finally
        {
            GC.KeepAlive(request);
            GC.KeepAlive(response);
        }
    }

    internal RequestEntityFraming ReadRequestEntityFraming(
        object request,
        RequestLedger.Attempt attempt
    )
    {
        try
        {
            return new RequestEntityFraming(
                requestMethod.Invoke(request, null)?.ToString(),
                requestUploadStream.Invoke(request, null) is not null,
                attempt.RequestEntityReturnedNull,
                attempt.SerializedRequestHeaders
            );
        }
        finally
        {
            GC.KeepAlive(request);
        }
    }

    internal string ReadOriginalUri(object request)
    {
        var uri =
            requestUri.Invoke(request, null)
            ?? throw new InvalidDataException("Original native request URI is unavailable.");
        try
        {
            return (string?)originalUri.Invoke(uri, null)
                ?? throw new InvalidDataException("Original native URI string is unavailable.");
        }
        finally
        {
            GC.KeepAlive(uri);
        }
    }

    internal (object? Descriptor, object? Request) ReadTransport(object value) =>
        (transportDescriptor.GetValue(value), transportRequest.GetValue(value));

    internal static MethodInfo Method(
        Type owner,
        string name,
        Type result,
        bool isStatic,
        long rva,
        params Type[] parameters
    )
    {
        var method =
            owner.GetMethod(name, Flags, null, parameters, null)
            ?? throw new MissingMethodException(owner.FullName, name);
        Plugin.Compatibility.ValidateMethod(
            method,
            rva,
            RuntimeCompatibility.SignatureType(result),
            parameters.Select(RuntimeCompatibility.SignatureType).ToArray(),
            isStatic
        );
        return method;
    }

    private static PropertyInfo Property(Type owner, string name, Type type)
    {
        var property = owner.GetProperty(name, Flags);
        if (
            property is null
            || property.GetMethod is null
            || property.GetMethod.IsStatic
            || property.PropertyType != type
        )
            throw new MissingMemberException("Native API descriptor field contract changed.");
        return property;
    }

    internal void Install()
    {
        Plugin.ObserveNative(Send, typeof(Network), "Send", null, "SendFailed");
        Plugin.ObserveNative(Dispose, typeof(Network), "Completed", null, "EndNativeCallback");
        Plugin.ObserveNative(
            State,
            typeof(Network),
            "BeginNativeCallback",
            "StateChanged",
            "EndNativeCallback"
        );
        Plugin.ObserveNative(
            EntityBody,
            typeof(Network),
            "BeginNativeCallback",
            "EntityMaterialized",
            "EndNativeCallback"
        );
        Plugin.ObserveNative(
            SendHeaders,
            typeof(Network),
            "BeginHeaderSend",
            null,
            "EndHeaderSend"
        );
        Plugin.ObserveNative(
            WriteHeaderGroup,
            typeof(Network),
            "HeaderGroupWriting",
            null,
            "EndNativeCallback"
        );
        Plugin.ObserveNative(Callback, typeof(Network), "BeginCallback", null, "EndCallback");
        Plugin.ObserveNative(
            Descriptor,
            typeof(Network),
            "BeginNativeCallback",
            "DescriptorCreated",
            "EndNativeCallback"
        );
        Plugin.ObserveNative(
            Transport,
            typeof(Network),
            "BeginNativeCallback",
            "TransportCreated",
            "EndNativeCallback"
        );
        Plugin.ObserveNative(Decoder, typeof(Network), "Decoding", "Decoded", "EndDecoding");
        Plugin.ObserveNative(Merge, typeof(Network), "Parsing", "Parsed", "EndParsing");
    }
}
