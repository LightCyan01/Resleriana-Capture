using System.Reflection;
using System.Reflection.Emit;

namespace AtelierCapture.Checks;

internal static class NativeConcreteChecks
{
    internal static void Run()
    {
        FiniteDeclarationsRejectNearNamesAndWrongModules();
        ClassAndPointerProofsRejectWrongViews();
        InterfaceViewsKeepFreshConcreteFieldsAndFailures();
        InteropTypeRequiresVerifiedInstalledOrigin();
    }

    private static void FiniteDeclarationsRejectNearNamesAndWrongModules()
    {
        var names = new[]
        {
            ("FCMCKDCGGEB", "Blend.dll", 0xA1),
            ("FHOEJNIEFIL", "Blend.Framework.dll", 0xA1),
            ("KAKLKJIIIHB", "Blend.dll", 0xA1),
            ("PLIHKAHEAEN", "Blend.dll", 0xA1),
            ("BattleUnitMoveControl+JPOMNKKPIFC+COBONNODPIB", "Blend.dll", 0x100082),
        };
        foreach (var (name, module, flags) in names)
        {
            Require(
                NativeObjectType.RequiresConcrete(name, module),
                "A pinned declaration was not prepared."
            );
            Require(
                !NativeObjectType.RequiresConcrete(name + "X", module),
                "A lookalike declaration was prepared."
            );
            Require(
                !NativeObjectType.RequiresConcrete(name, "Other.dll"),
                "A declaration from another module was prepared."
            );
            NativeObjectType.ValidateDeclaration(name, flags);
            Reject(() => NativeObjectType.ValidateDeclaration(name, 1));
        }
        Require(!NativeObjectType.RequiresConcrete(null, null), "A null declaration was accepted.");
        Require(
            NativeObjectType.Concrete(null!) is null,
            "A null reference changed during preparation."
        );
        Require(
            Equals(NativeObjectType.Concrete(17), 17),
            "A CLR scalar was converted to a native object."
        );
        Require(
            NativeFieldPolicy.IsResourceOwner("UserDataManager", "Blend.dll")
                && !NativeFieldPolicy.IsResourceOwner("Other.UserDataManager", "Blend.dll")
                && !NativeFieldPolicy.IsResourceOwner("UserDataManager", "Other.dll")
                && !NativeFieldPolicy.IsResourceOwner(null, null),
            "The resource owner exception widened beyond its exact full name/module."
        );
    }

    private static void ClassAndPointerProofsRejectWrongViews()
    {
        NativeObjectType.ValidateResolvedClass("concrete.owner", 1, 2, 2, true, true, 1);
        NativeObjectType.ValidateWrappedReference("concrete.owner", 3, 3, 2, 2);
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("wrong.wrapper", 1, 2, 4, true, true, 1)
        );
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("unassignable.owner", 1, 2, 2, false, true, 1)
        );
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("open.generic<T>", 1, 2, 2, true, false, 1)
        );
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("missing.declaration", 0, 2, 2, true, true, 1)
        );
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("missing.actual", 1, 0, 0, true, true, 1)
        );
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("abstract.owner", 1, 2, 2, true, true, 0x80)
        );
        Reject(() =>
            NativeObjectType.ValidateResolvedClass("interface.owner", 1, 2, 2, true, true, 0x20)
        );
        Reject(() => NativeObjectType.ValidateWrappedReference("null.pointer", 0, 0, 2, 2));
        Reject(() => NativeObjectType.ValidateWrappedReference("changed.pointer", 3, 4, 2, 2));
        Reject(() => NativeObjectType.ValidateWrappedReference("changed.class", 3, 3, 2, 4));
    }

    private static void InterfaceViewsKeepFreshConcreteFieldsAndFailures()
    {
        var owner = new Model { Value = 17 };
        var first = new View("FCMCKDCGGEB", 3, 2, 2, owner);
        var alias = new View("KAKLKJIIIHB", 3, 2, 2, owner);
        DetachedSnapshot Graph() =>
            new(value =>
            {
                if (value is object?[] values)
                    return new("synthetic.values", values, [], () => values);
                var view = (View)value;
                Require(
                    NativeObjectType.RequiresConcrete(view.Declared, "Blend.dll"),
                    "An unqualified declaration reached materialization."
                );
                NativeObjectType.ValidateResolvedClass(
                    "synthetic.concrete.owner",
                    1,
                    view.ActualClass,
                    view.RegisteredClass,
                    true,
                    true,
                    1
                );
                NativeObjectType.ValidateWrappedReference(
                    "synthetic.concrete.owner",
                    view.Pointer,
                    view.Pointer,
                    view.ActualClass,
                    view.ActualClass
                );
                return new(
                    "synthetic.concrete.owner",
                    (view.Pointer, view.ActualClass),
                    [
                        new("required", () => view.Model.Value),
                        new("required_list", () => view.Model.Values),
                    ]
                );
            });
        var before = Graph();
        before.Read(first);
        before.Read(alias);
        Require(
            before.Complete
                && before.Nodes.Count == 2
                && Equals(before.Nodes[0].fields["required"], 17),
            "Two declared views lost or duplicated their concrete stored owner."
        );
        owner.Value = 29;
        owner.Values = [0, null, 31];
        var after = Graph();
        after.Read(alias);
        Require(
            after.Complete
                && Equals(after.Nodes[0].fields["required"], 29)
                && after.Nodes[0].fields["required_list"]!
                    .GetType()
                    .GetProperty("reference")!
                    .GetValue(after.Nodes[0].fields["required_list"])
                    is 2,
            "A later observation reused stale concrete state."
        );
        Require(
            after.Nodes[1].items!.SequenceEqual(new object?[] { 0, null, 31 }),
            "Concrete preparation lost null/default/full collection fields."
        );
        var failed = Graph();
        failed.Read(first);
        failed.Read(new View("FCMCKDCGGEB", 4, 2, 5, owner));
        Require(
            !failed.Complete
                && failed.Nodes.Count == 2
                && failed.Failures.Count == 1
                && failed
                    .Failures[0]
                    .Details!.Contains("synthetic.concrete.owner", StringComparison.Ordinal)
                && Equals(failed.Nodes[0].fields["required"], 29),
            "An invalid concrete view suppressed its actual class failure or other available fields."
        );
    }

    private static void InteropTypeRequiresVerifiedInstalledOrigin()
    {
        var compatibility = new RuntimeCompatibility();
        Reject(() => compatibility.ValidateInteropType(typeof(NativeConcreteChecks)));
        typeof(RuntimeCompatibility)
            .GetProperty("ApiVerified", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(compatibility, true);
        Reject(() => compatibility.ValidateInteropType(typeof(NativeConcreteChecks)));
        typeof(RuntimeCompatibility)
            .GetField("interopDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(
                compatibility,
                Path.GetDirectoryName(typeof(NativeConcreteChecks).Assembly.Location)!
                    + Path.DirectorySeparatorChar
            );
        compatibility.ValidateInteropType(typeof(NativeConcreteChecks));
        Reject(() => compatibility.ValidateInteropType(typeof(string)));
        var dynamic = AssemblyBuilder
            .DefineDynamicAssembly(
                new AssemblyName("UnknownInteropOrigin"),
                AssemblyBuilderAccess.Run
            )
            .DefineDynamicModule("Blend.dll")
            .DefineType("UserDataManager")
            .CreateType()!;
        Reject(() => compatibility.ValidateInteropType(dynamic));
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException("An unqualified native concrete contract was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private sealed class Model
    {
        internal int Value;
        internal object?[] Values = [];
    }

    private sealed record View(
        string Declared,
        nint Pointer,
        nint ActualClass,
        nint RegisteredClass,
        Model Model
    );
}
