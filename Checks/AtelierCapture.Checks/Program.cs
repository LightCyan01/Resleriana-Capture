using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AtelierCapture.Checks;

internal static class Program
{
    private static readonly string[] Groups =
    [
        "StorageChecks",
        "ArchiveChecks",
        "NativeContractChecks",
        "NetworkChecks",
        "BuildChecks",
        "DiagnosticChecks",
        "JsonCaptureChecks",
        "UpdateChecks",
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--update-package-check")
        {
            UpdateChecks.RunPackage(args[1], args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--update-crash-child")
        {
            UpdateChecks.RunCrashChild(args[1], args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--update-lock-child")
        {
            UpdateChecks.RunLockChild(args[1]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--update-game-child")
        {
            UpdateChecks.RunGameChild(args[1], args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--storage-crash-child")
        {
            StorageChecks.RunCrashChild(args[1], args[2]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--zip64-check")
        {
            StorageChecks.RunZip64();
            return 0;
        }
        if (args.Length == 3 && args[0] == "--warning-pool-child")
        {
            WarningAggregationChecks.RunPoolAcknowledgementChild(args[1], args[2]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--native-race-child")
        {
            if (!uint.TryParse(args[1], out var race) || race is < 1 or > 3)
                return 2;
            BuildChecks.RunNativeRace(race);
            return 0;
        }

        var selected = args.Length == 0 ? Groups : args;
        var results = new List<object>();
        foreach (var name in selected)
        {
            if (!Groups.Contains(name, StringComparer.Ordinal) && name != "UpdateHostChecks")
            {
                Console.Error.WriteLine($"Unknown check group: {name}");
                return 2;
            }

            var timer = Stopwatch.StartNew();
            try
            {
                var type =
                    Assembly
                        .GetExecutingAssembly()
                        .GetTypes()
                        .SingleOrDefault(candidate => candidate.Name == name)
                    ?? throw new InvalidOperationException(
                        $"Required check group is absent: {name}"
                    );
                var run =
                    type.GetMethod(
                        "Run",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    )
                    ?? throw new InvalidOperationException(
                        $"Required Run method is absent: {name}"
                    );
                run.Invoke(null, null);
                results.Add(
                    new
                    {
                        group = name,
                        passed = true,
                        elapsed_ms = timer.ElapsedMilliseconds,
                    }
                );
            }
            catch (Exception error)
            {
                var cause = error.GetBaseException();
                results.Add(
                    new
                    {
                        group = name,
                        passed = false,
                        elapsed_ms = timer.ElapsedMilliseconds,
                        error = cause.ToString(),
                    }
                );
                Console.WriteLine(
                    JsonSerializer.Serialize(
                        new { runtime = RuntimeInformation.FrameworkDescription, results }
                    )
                );
                return 1;
            }
        }

        Console.WriteLine(
            JsonSerializer.Serialize(
                new { runtime = RuntimeInformation.FrameworkDescription, results }
            )
        );
        return 0;
    }
}
