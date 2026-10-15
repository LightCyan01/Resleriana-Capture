using System.Diagnostics;
using System.Security.Cryptography;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static partial class Network
{
    private static void DescriptorCreated(object __instance, IDisposable? __state)
    {
        if (__state is null)
            return;
        Observe(
            "api_descriptor",
            commits =>
            {
                var token = Objects.Token(__instance);
                var descriptor = ReadDescriptor(__instance);
                Descriptors[token] = descriptor;
                Plugin.Writer.EmitBootstrap(
                    "native_request_descriptor",
                    new
                    {
                        descriptor_id = token.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        operation_id = descriptor.OperationId,
                        native_operation_id = descriptor.RawOperationId,
                        route = descriptor.Route,
                        stage = "native_request_protobuf",
                        body = descriptor.Bytes,
                        bytes = descriptor.Bytes?.Length,
                        sha256 = descriptor.Bytes is null
                            ? null
                            : Convert.ToHexString(SHA256.HashData(descriptor.Bytes)),
                        source = "original_immutable_native_descriptor",
                        capture_status = "observed",
                        correlation_status = "pending_transport_binding",
                    }
                );
            }
        );
    }

    private static void TransportCreated(object __instance, IDisposable? __state)
    {
        if (__state is null)
            return;
        Observe(
            "api_transport",
            commits =>
            {
                var pair = bindings!.ReadTransport(__instance);
                if (pair.Descriptor is null || pair.Request is null)
                {
                    Emit(
                        null,
                        "api_binding_pending",
                        new
                        {
                            owner = "PMLKMPHGENK..ctor",
                            reason = "transport_descriptor_request_not_yet_materialized",
                        }
                    );
                    return;
                }
                BindDescriptor(
                    pair.Descriptor,
                    pair.Request,
                    commits,
                    "initialized_transport_constructor"
                );
            }
        );
    }

    private static void BindDescriptor(
        object descriptorObject,
        object requestObject,
        List<Action> commits,
        string source
    )
    {
        var descriptorToken = Objects.Token(descriptorObject);
        if (!Descriptors.TryGetValue(descriptorToken, out var descriptor))
        {
            descriptor = ReadDescriptor(descriptorObject);
            Descriptors.Add(descriptorToken, descriptor);
        }
        var requestToken = Objects.Token(requestObject);
        try
        {
            if (
                RequestDescriptors.TryGetValue(requestToken, out var known)
                && (
                    known.DescriptorId != descriptor.DescriptorId
                    || known.Route != descriptor.Route
                    || known.RawOperationId != descriptor.RawOperationId
                )
            )
                throw new InvalidDataException(
                    "Native HTTP owner was bound to another original descriptor."
                );
            Ledger.VerifyDescriptor(
                requestToken,
                descriptor.DescriptorId,
                descriptor.Route,
                descriptor.RawOperationId
            );
        }
        catch (InvalidDataException)
        {
            PreserveUnclassified(
                descriptor.Bytes,
                "request",
                "native_request_protobuf",
                "descriptor_request_mismatch"
            );
            Gap(null, "descriptor_request_mismatch", new { source });
            return;
        }
        if (Ledger.Attempts(requestToken).Count > 1)
        {
            PreserveUnclassified(
                descriptor.Bytes,
                "request",
                "native_request_protobuf",
                "descriptor_request_owner_ambiguous"
            );
            Gap(null, "descriptor_request_owner_ambiguous", new { source });
            return;
        }
        RequestDescriptors[requestToken] = descriptor;
        var attempt = Ledger.Unique(requestToken);
        if (attempt is null)
        {
            PreserveUnclassified(
                descriptor.Bytes,
                "request",
                "native_request_protobuf",
                "descriptor_send_identity_not_yet_proven"
            );
            return;
        }
        var context = attempt.Context;
        attempt.Context = context = context with
        {
            DescriptorId = descriptor.DescriptorId,
            LogicalRoute = descriptor.Route,
            NativeOperationId = descriptor.RawOperationId,
        };
        BindContext(requestObject, context, "PMLKMPHGENK.initialized_descriptor_request_fields");
        using var scope = PushContext(context);
        Emit(
            context,
            "api_descriptor_binding",
            new
            {
                request_id = context.RequestId,
                attempt_id = context.AttemptId,
                operation_id = descriptor.OperationId,
                native_operation_id = descriptor.RawOperationId,
                logical_route = descriptor.Route,
                transport_path = context.TransportPath,
                recorded_route = context.Route,
                request_header_id = context.RequestHeaderId,
                source,
                original_descriptor = true,
                descriptor_id = descriptorToken.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
            }
        );
        if (descriptor.Bytes is { } bytes)
            Preserve(attempt, bytes, "request", "native_request_protobuf", commits);
    }

    private static Descriptor ReadDescriptor(object value)
    {
        var bytes = CopyNullable(
            ReadAvailable(() => bindings!.ReadDescriptorBody(value), "descriptor_body_read_failed")
        );
        var route =
            ReadAvailable(
                () => bindings!.ReadDescriptorRoute(value),
                "descriptor_route_read_failed"
            ) ?? "";
        var originalOperation = ReadAvailable(
            () => bindings!.ReadDescriptorId(value),
            "descriptor_operation_read_failed"
        );
        if (route.Length == 0 || originalOperation is null)
        {
            PreserveUnclassified(
                bytes,
                "request",
                "native_request_protobuf",
                "descriptor_identity_unavailable"
            );
            Gap(
                null,
                "descriptor_identity_unavailable",
                new
                {
                    route_available = route.Length != 0,
                    operation_available = originalOperation is not null,
                }
            );
        }
        return new Descriptor(
            RequestLedger.CanonicalOperation(originalOperation),
            route,
            bytes,
            originalOperation,
            Objects.Token(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
    }
}
