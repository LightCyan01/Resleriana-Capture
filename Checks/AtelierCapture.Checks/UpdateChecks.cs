using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtelierCapture.Update;

namespace AtelierCapture.Checks;

internal static class UpdateChecks
{
    private static int assertions;
    private static string product = "";
    private static string native = "";
    private static string workspace = "";

    internal static void Run()
    {
        workspace = FindRoot();
        product = Path.Combine(workspace, "work/build/stage/BepInEx/plugins/AtelierCapture.dll");
        native = Path.Combine(
            workspace,
            "work/build/stage/BepInEx/plugins/AtelierCapture.Native.dll"
        );
        using (var area = new Area())
        {
            PackageValidation(area);
            TransactionCases(area);
            LockingAndGate(area);
            ReleaseCases().GetAwaiter().GetResult();
            HelperLifecycle(area);
        }
        Console.WriteLine($"UPDATE CHECKS PASSED: {assertions} assertions");
    }

    internal static void RunHost()
    {
        workspace = FindRoot();
        product = Path.Combine(workspace, "work/build/stage/BepInEx/plugins/AtelierCapture.dll");
        native = Path.Combine(
            workspace,
            "work/build/stage/BepInEx/plugins/AtelierCapture.Native.dll"
        );
        using (var area = new Area())
            HelperLifecycle(area);
        Console.WriteLine($"UPDATE HOST CHECKS PASSED: {assertions} assertions");
    }

    internal static void RunPackage(string root, string archive)
    {
        var workspaceRoot = FindRoot();
        if (
            !Path.GetFullPath(root)
                .StartsWith(
                    Path.Combine(workspaceRoot, "work", "checks") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
        )
            throw new InvalidOperationException(
                "Package checks may only modify disposable work/checks directories."
            );
        var files = new UpdateFiles(root);
        files.Initialize();
        using (var package = new UpdatePackage(archive))
            Check(
                package.CheckInstalled(files) < UpdateFiles.Version(package.Manifest.Version),
                "exact published package matches the installed base"
            );
        File.Copy(archive, files.Ready, true);
        var transaction = new UpdateTransaction(files);
        using (transaction.Enter())
        using (transaction.GameLease(true))
            Check(transaction.ApplyReady(), "exact packaged release applies completely");
        using var candidate = new UpdatePackage(archive);
        Check(ReadVersion(files) == candidate.Manifest.Version, "exact packaged version installed");
        Check(
            candidate.Manifest.Files.All(s => UpdateFiles.Matches(files.Resolve(s.Path), s)),
            "all three published payload hashes match"
        );
        Check(!File.Exists(files.Journal), "exact package transaction finalized");
        Console.WriteLine("EXACT UPDATE PACKAGE CHECKS PASSED");
    }

    private static void PackageValidation(Area area)
    {
        var root = area.New();
        var files = Fixture(root);
        using (var package = new UpdatePackage(files.Ready))
        {
            Check(package.Manifest.Version == "1.4.0", "candidate version");
            Check(
                package.CheckInstalled(files) == new Version(1, 3, 9),
                "compatible installed base"
            );
        }
        var valid = File.ReadAllBytes(files.Ready);
        foreach (
            var name in new[]
            {
                "../escape.dll",
                "BepInEx/plugins/Captures/Official/a.zip",
                "BepInEx/core/BepInEx.Core.dll",
                "BepInEx/plugins/AtelierCapture.dll",
                "BepInEx\\plugins\\AtelierCapture.dll",
                "dotnet/coreclr.dll",
            }
        )
        {
            File.WriteAllBytes(files.Ready, valid);
            using (var zip = ZipFile.Open(files.Ready, ZipArchiveMode.Update))
                zip.CreateEntry(name);
            Throws(
                () =>
                {
                    using var ignored = new UpdatePackage(files.Ready);
                },
                "unexpected/duplicate/traversal ZIP"
            );
        }
        File.WriteAllBytes(files.Ready, valid);
        Mutate(files.Ready, "update.json", j => j["version"] = "1.4.1");
        Throws(
            () =>
            {
                using var ignored = new UpdatePackage(files.Ready);
            },
            "manifest/certificate mismatch"
        );
        File.WriteAllBytes(files.Ready, valid);
        Mutate(files.Ready, "update.json", j => j["recovery_contract"] = "incompatible");
        Throws(
            () =>
            {
                using var ignored = new UpdatePackage(files.Ready);
            },
            "changed recovery contract"
        );
        File.WriteAllBytes(files.Ready, valid);
        Mutate(
            files.Ready,
            "update.json",
            j =>
            {
                var size = j["files"]![0]!["size"]!.GetValue<long>();
                j["files"]![0]!["size"] = size - 1;
            }
        );
        var forged = File.ReadAllBytes(files.Ready);
        var forgedSize = false;
        for (var index = 0; index + 46 < forged.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(forged.AsSpan(index)) != 0x02014B50)
                continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(forged.AsSpan(index + 28));
            if (index + 46 + nameLength > forged.Length)
                continue;
            if (Encoding.UTF8.GetString(forged, index + 46, nameLength) != UpdateFiles.Targets[0])
                continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(forged.AsSpan(index + 24));
            BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(index + 24), size - 1);
            forgedSize = true;
            break;
        }
        Check(forgedSize, "dishonest central ZIP length fixture is active");
        File.WriteAllBytes(files.Ready, forged);
        try
        {
            using var ignored = new UpdatePackage(files.Ready);
            Check(false, "dishonest expanded size rejected");
        }
        catch (InvalidDataException)
        {
            Check(true, "dishonest expanded bytes rejected by package boundary");
        }
        Throws(
            () => UpdateFiles.Hash(new MemoryStream(new byte[3]), 2),
            "hash cannot consume more than declared size"
        );
        using (var bounded = new MemoryStream())
            Throws(
                () => UpdateFiles.CopyExact(new MemoryStream(new byte[3]), bounded, 2),
                "extract cannot write more than declared size"
            );
        File.WriteAllBytes(files.Ready, valid);
        using (var zip = ZipFile.Open(files.Ready, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(UpdateFiles.Targets[0])!;
            entry.ExternalAttributes = (0xA1FF << 16);
        }
        Throws(
            () =>
            {
                using var ignored = new UpdatePackage(files.Ready);
            },
            "ZIP link"
        );
        File.WriteAllBytes(files.Ready, valid);
        using (var zip = ZipFile.Open(files.Ready, ZipArchiveMode.Update))
        {
            zip.GetEntry(UpdateFiles.Targets[0])!.Delete();
            using var stream = zip.CreateEntry(UpdateFiles.Targets[0]).Open();
            stream.Write("corrupt"u8);
        }
        Throws(
            () =>
            {
                using var ignored = new UpdatePackage(files.Ready);
            },
            "corrupt file identity"
        );
        File.WriteAllBytes(files.Ready, valid);
        var certificatePath = files.Resolve(UpdateFiles.Certificate);
        var certificate = File.ReadAllBytes(certificatePath);
        File.WriteAllText(files.Resolve("dotnet/base.dll"), "tampered");
        using (var package = new UpdatePackage(files.Ready))
            Throws(() => package.CheckInstalled(files), "changed installed base");
        File.WriteAllText(files.Resolve("dotnet/base.dll"), "base");
        foreach (var field in new[] { "runtime_version", "recovery_contract" })
        {
            var json = JsonNode.Parse(certificate)!.AsObject();
            json[field] = "changed";
            File.WriteAllText(certificatePath, json.ToJsonString());
            using var package = new UpdatePackage(files.Ready);
            Throws(() => package.CheckInstalled(files), "incompatible base contract");
        }
        File.WriteAllBytes(certificatePath, certificate);
        using (var zip = ZipFile.Open(files.Ready, ZipArchiveMode.Update))
        {
            zip.GetEntry(UpdateFiles.Targets[0])!.Delete();
            using var stream = zip.CreateEntry(UpdateFiles.Targets[0], CompressionLevel.Fastest)
                .Open();
            var zeros = new byte[1024 * 1024];
            for (var index = 0; index < 65; index++)
                stream.Write(zeros);
        }
        Throws(
            () =>
            {
                using var ignored = new UpdatePackage(files.Ready);
            },
            "compressed oversized entry"
        );
    }

    private static void TransactionCases(Area area)
    {
        foreach (
            var stop in new[]
            {
                "journal",
                "before_replace_0",
                "after_replace_0",
                "after_replace_1",
                "after_replace_2",
                "verified",
            }
        )
        {
            var root = area.New();
            var files = Fixture(root);
            var before = Targets(files);
            using (var transactionLock = new UpdateTransaction(files).Enter())
            using (var exclusive = new UpdateTransaction(files).GameLease(true))
            {
                using var child = StartChild("--update-crash-child", root, stop);
                Check(child.WaitForExit(15000), "crash child terminates");
                Check(child.ExitCode == 73, "crash injection exercised " + stop);
                var transaction = new UpdateTransaction(files);
                transaction.Recover();
            }
            var after = Targets(files);
            var journalWasComplete = stop is "after_replace_2" or "verified";
            if (journalWasComplete)
                Check(ReadVersion(files) == "1.4.0", "fully replaced version retained");
            else
                Check(
                    before.SequenceEqual(after),
                    "interrupted transaction restores exact originals"
                );
            Check(!File.Exists(files.Journal), "recovered transaction cleared");
            Check(
                File.ReadAllText(files.Resolve("BepInEx/plugins/Captures/Official/sentinel.zip"))
                    == "capture-preserved",
                "capture bytes unchanged"
            );
            Check(
                File.ReadAllText(files.Resolve("BepInEx/config/user.cfg")) == "config-preserved",
                "user configuration unchanged"
            );
        }
        {
            var files = Fixture(area.New());
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Check(transaction.ApplyReady(), "successfully applies update");
            Check(
                ReadVersion(files) == "1.4.0" && !File.Exists(files.Ready),
                "new complete set installed"
            );
        }
        {
            var files = Fixture(area.New());
            var before = Targets(files);
            var transaction = new UpdateTransaction(
                files,
                stage =>
                {
                    if (stage == "after_replace_0")
                        throw new IOException("simulated locked target");
                }
            );
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Throws(() => transaction.ApplyReady(), "I/O failure is reported");
            Check(
                before.SequenceEqual(Targets(files)) && !File.Exists(files.Journal),
                "I/O failure rolls back"
            );
        }
        {
            var files = Fixture(area.New());
            var target = files.Resolve(UpdateFiles.Targets[1]);
            var before = Targets(files);
            using (
                var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)
            )
            {
                var transaction = new UpdateTransaction(files);
                using (transaction.Enter())
                using (transaction.GameLease(true))
                    Throws(
                        () => transaction.ApplyReady(),
                        "real destination lock fails replacement"
                    );
            }
            Check(
                before.SequenceEqual(Targets(files)),
                "real destination lock preserves original set"
            );
        }
        {
            var files = Fixture(area.New(), "1.4.0");
            var before = Targets(files);
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Check(!transaction.ApplyReady(), "same-version stage never overwrites");
            Check(before.SequenceEqual(Targets(files)), "same-version set untouched");
        }
        {
            var files = Fixture(area.New(), "1.5.0");
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Check(!transaction.ApplyReady(), "stale stage never downgrades");
        }
        {
            var files = Fixture(area.New());
            using (var child = StartChild("--update-crash-child", files.Root, "after_replace_0"))
            {
                Check(child.WaitForExit(15000) && child.ExitCode == 73, "unknown identity setup");
            }
            File.WriteAllText(files.Resolve(UpdateFiles.Targets[1]), "external-change");
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Throws(transaction.Recover, "external change blocks rollback");
            Check(File.Exists(files.Journal), "unknown state retained for recovery");
            Check(
                File.ReadAllText(files.Resolve(UpdateFiles.Targets[1])) == "external-change",
                "external identity not overwritten"
            );
        }
        {
            var files = Fixture(area.New());
            using (var child = StartChild("--update-crash-child", files.Root, "after_replace_1"))
            {
                Check(child.WaitForExit(15000) && child.ExitCode == 73, "rollback crash setup");
            }
            using (var child = StartChild("--update-crash-child", files.Root, "rollback_0"))
            {
                Check(
                    child.WaitForExit(15000) && child.ExitCode == 73,
                    "rollback itself interrupted"
                );
            }
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                transaction.Recover();
            Check(
                ReadVersion(files) == "1.3.9" && !File.Exists(files.Journal),
                "cold rollback resumes exactly"
            );
        }
        {
            var files = Fixture(area.New());
            var config = files.Resolve("BepInEx/config/AtelierCapture.Updater.cfg");
            File.WriteAllText(config, "[Updates]\nEnabled = false\n");
            var before = Targets(files);
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Check(!transaction.ApplyReady(), "disabled switch prevents install");
            Check(before.SequenceEqual(Targets(files)), "disabled set unchanged");
            File.WriteAllText(config, "[Updates]\nEnabled = true\nEnabled = false\n");
            Check(!files.Enabled(), "duplicate config is not an enable instruction");
            foreach (
                var partial in new[]
                {
                    "",
                    "[Updates]\n",
                    "[Updates]\nEnabled =",
                    "[Other]\nEnabled = true",
                }
            )
            {
                File.WriteAllText(config, partial);
                Check(!files.Enabled(), "existing partial config cannot enable updates");
            }
        }
        {
            var files = Fixture(area.New());
            using (var child = StartChild("--update-crash-child", files.Root, "after_replace_0"))
                Check(
                    child.WaitForExit(15000) && child.ExitCode == 73,
                    "disabled recovery interrupted setup"
                );
            File.WriteAllText(
                files.Resolve("BepInEx/config/AtelierCapture.Updater.cfg"),
                "[Updates]\nEnabled = false\n"
            );
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                transaction.Recover();
            Check(
                ReadVersion(files) == "1.3.9" && !File.Exists(files.Journal),
                "disabled updates still recover old set"
            );
        }
        {
            var root = area.New();
            var files = Fixture(root);
            File.WriteAllText(files.Resolve("dotnet/base.dll"), "changed base");
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Check(!transaction.TryApplyReady(_ => { }), "incompatible ready is deferred");
            Check(
                !File.Exists(files.Ready)
                    && File.Exists(files.Resolve("BepInEx/capture-update/failed.zip")),
                "rejected ready cannot starve future checks"
            );
            files = Fixture(root);
            transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            using (transaction.GameLease(true))
                Check(transaction.ApplyReady(), "later compatible payload still installs");
        }
    }

    private static void LockingAndGate(Area area)
    {
        var files = Fixture(area.New());
        var transaction = new UpdateTransaction(files);
        using (transaction.Enter())
        {
            using var first = transaction.GameLease(false);
            using var second = transaction.GameLease(false);
            Throws(
                () => transaction.GameLease(true).Dispose(),
                "multiple active games prohibit update"
            );
        }
        using (transaction.Enter())
        using (transaction.GameLease(true))
            Check(true, "last game exit releases exclusive gate");
        using (var child = StartChild("--update-lock-child", files.Root, "hold"))
        {
            WaitFile(files.Resolve("BepInEx/capture-update/child-ready"), child);
            Throws(() => transaction.Enter(20).Dispose(), "startup lock wait is bounded");
            child.Kill();
            Check(child.WaitForExit(5000), "forced process exit");
        }
        using (transaction.Enter())
        using (transaction.GameLease(true))
            Check(true, "abandoned mutex/process lease recover");
        AppContext.SetData(UpdateFiles.GateKey, null);
        Throws(
            () => RuntimeCompatibility.RequireUpdateGate(files.Root),
            "absent initializer blocks capture admission"
        );
        AppContext.SetData(UpdateFiles.GateKey, files.Root);
        RuntimeCompatibility.RequireUpdateGate(files.Root);
        File.WriteAllText(files.Journal, "malformed");
        Throws(
            () => RuntimeCompatibility.RequireUpdateGate(files.Root),
            "unresolved transaction blocks capture admission"
        );
        Throws(transaction.Recover, "malformed journal cannot mutate target");
        File.Delete(files.Journal);
        AppContext.SetData(UpdateFiles.GateKey, null);
        var outside = area.New();
        var linked = Path.Combine(files.State, "linked");
        using (
            var junction = Process.Start(
                new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c mklink /J \"{linked}\" \"{outside}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                }
            )!
        )
            Check(
                junction.WaitForExit(5000) && junction.ExitCode == 0,
                "disposable junction created without symlink privilege"
            );
        Throws(() => files.Resolve("BepInEx/capture-update/linked/foreign"), "state link rejected");
        Directory.Delete(linked);
    }

    private static async Task ReleaseCases()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData("zip"u8));
        var json = new JsonObject
        {
            ["tag_name"] = "v1.4.0",
            ["draft"] = false,
            ["prerelease"] = false,
            ["published_at"] = "2026-10-16T00:00:00Z",
            ["assets"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Resleriana-Capture-v1.4.0-update-win-x64.zip",
                    ["state"] = "uploaded",
                    ["size"] = 3,
                    ["digest"] = "sha256:" + hash,
                    ["browser_download_url"] =
                        "https://github.com/LightCyan01/Resleriana-Capture/releases/download/v1.4.0/Resleriana-Capture-v1.4.0-update-win-x64.zip",
                }
            ),
        };
        ReleaseAsset? Parse(JsonObject value, Version? current = null)
        {
            using var doc = JsonDocument.Parse(value.ToJsonString());
            return ReleaseDownload.Parse(doc.RootElement, current ?? new Version(1, 3, 9));
        }
        Check(Parse(json) is not null, "new stable release selected");
        Check(Parse(json, new Version(1, 4, 0)) is null, "same release ignored");
        Check(Parse(json, new Version(1, 5, 0)) is null, "old release ignored");
        foreach (var flag in new[] { "draft", "prerelease" })
        {
            var changed = json.DeepClone().AsObject();
            changed[flag] = true;
            Check(Parse(changed) is null, "nonstable ignored");
        }
        foreach (var field in new[] { "digest", "browser_download_url", "state" })
        {
            var changed = json.DeepClone().AsObject();
            changed["assets"]![0]![field] = "invalid";
            Throws(() => Parse(changed), "invalid published asset");
        }
        foreach (
            var url in new[]
            {
                "http://github.com/a",
                "https://github.com.evil/a",
                "https://example.com/a",
                "https://user@github.com/a",
                "https://github.com:8443/a",
            }
        )
            Check(!ReleaseDownload.Allowed(new Uri(url)), "untrusted network URL rejected");
        using (
            var client = new HttpClient(
                new FakeHttp(_ =>
                    new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString()) }
                )
            )
        )
            Check(
                await new ReleaseDownload(client).Latest(
                    new Version(1, 3, 9),
                    CancellationToken.None
                )
                    is not null,
                "actual release HTTP parser"
            );
        using (var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.NotFound))))
            Check(
                await new ReleaseDownload(client).Latest(
                    new Version(1, 3, 9),
                    CancellationToken.None
                )
                    is null,
                "no release is harmless"
            );
        foreach (
            var code in new[]
            {
                HttpStatusCode.Forbidden,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
            }
        )
        {
            using var client = new HttpClient(new FakeHttp(_ => new(code)));
            await ThrowsAsync(
                () =>
                    new ReleaseDownload(client).Latest(
                        new Version(1, 3, 9),
                        CancellationToken.None
                    ),
                "HTTP failure preserves install"
            );
        }
        using (
            var client = new HttpClient(
                new FakeHttp(_ =>
                    new(HttpStatusCode.Found)
                    {
                        Headers = { Location = new Uri("https://example.com/payload") },
                    }
                )
            )
        )
            await ThrowsAsync(
                () =>
                    new ReleaseDownload(client).Latest(
                        new Version(1, 3, 9),
                        CancellationToken.None
                    ),
                "malicious redirect rejected"
            );
        using (
            var client = new HttpClient(
                new FakeHttp(_ =>
                    new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(new string('x', UpdateFiles.MetadataLimit + 1)),
                    }
                )
            )
        )
            await ThrowsAsync(
                () =>
                    new ReleaseDownload(client).Latest(
                        new Version(1, 3, 9),
                        CancellationToken.None
                    ),
                "oversized API response rejected"
            );
        using var output = new MemoryStream();
        await ThrowsAsync(
            () =>
                ReleaseDownload.CopyBounded(
                    new MemoryStream(new byte[11]),
                    output,
                    10,
                    CancellationToken.None
                ),
            "body size guard"
        );
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ThrowsAsync(
            () =>
                ReleaseDownload.CopyBounded(
                    new MemoryStream(new byte[1]),
                    output,
                    10,
                    canceled.Token
                ),
            "cancellation observed"
        );
        using var downloadArea = new Area();
        var downloadPath = Path.Combine(downloadArea.New(), "download.partial");
        var asset = Parse(json)!;
        using (
            var client = new HttpClient(
                new FakeHttp(_ =>
                    new(HttpStatusCode.OK) { Content = new ByteArrayContent("zip"u8.ToArray()) }
                )
            )
        )
        {
            await new ReleaseDownload(client).Download(asset, downloadPath, CancellationToken.None);
            Check(File.ReadAllText(downloadPath) == "zip", "complete published-digest download");
        }
        foreach (var body in new[] { "z", "bad", "toolong" })
        {
            using var client = new HttpClient(
                new FakeHttp(_ =>
                    new(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
                    }
                )
            );
            await ThrowsAsync(
                () =>
                    new ReleaseDownload(client).Download(
                        asset,
                        downloadPath,
                        CancellationToken.None
                    ),
                "partial/corrupt/oversized download rejected"
            );
        }
        using (
            var client = new HttpClient(
                new FakeHttp(_ => throw new HttpRequestException("offline"))
            )
        )
            await ThrowsAsync(
                () =>
                    new ReleaseDownload(client).Latest(
                        new Version(1, 3, 9),
                        CancellationToken.None
                    ),
                "offline check fails without installer mutation"
            );
    }

    private static void HelperLifecycle(Area area)
    {
        var root = area.New();
        var files = Fixture(root);
        // Run the real helper and a managed stand-in process; never launch the installed game.
        foreach (
            var source in Directory.EnumerateFiles(
                Path.Combine(workspace, "work/build/stage/dotnet")
            )
        )
        {
            File.Copy(source, Path.Combine(root, "dotnet", Path.GetFileName(source)), true);
            File.Copy(source, Path.Combine(root, Path.GetFileName(source)), true);
        }
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            if (Path.GetExtension(source) is ".dll" or ".json")
                File.Copy(source, Path.Combine(root, Path.GetFileName(source)), true);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AtelierCapture.Checks.exe"),
            Path.Combine(root, "AtelierResleriana.exe")
        );
        var configuration = JsonNode.Parse(
            File.ReadAllText(Path.Combine(root, "AtelierCapture.Checks.runtimeconfig.json"))
        )!;
        var options = configuration["runtimeOptions"]!.AsObject();
        options.Remove("framework");
        options.Remove("frameworks");
        options["includedFrameworks"] = new JsonArray(
            new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.13" }
        );
        File.WriteAllText(
            Path.Combine(root, "AtelierCapture.Checks.runtimeconfig.json"),
            configuration.ToJsonString()
        );
        var dependenciesPath = Path.Combine(root, "AtelierCapture.Checks.deps.json");
        var dependencies = JsonNode.Parse(File.ReadAllText(dependenciesPath))!;
        var helperDependencies = JsonNode.Parse(
            File.ReadAllText(Path.Combine(root, "AtelierCapture.Update.deps.json"))
        )!;
        var runtimeTarget = helperDependencies["runtimeTarget"]!["name"]!.GetValue<string>();
        var target = dependencies["targets"]!.AsObject().First().Value!.DeepClone().AsObject();
        const string runtimePack = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13";
        target[runtimePack] = helperDependencies["targets"]![runtimeTarget]![
            runtimePack
        ]!.DeepClone();
        dependencies["targets"] = new JsonObject { [runtimeTarget] = target };
        dependencies["runtimeTarget"]!["name"] = runtimeTarget;
        dependencies["libraries"]![runtimePack] = helperDependencies["libraries"]![
            runtimePack
        ]!.DeepClone();
        File.WriteAllText(dependenciesPath, dependencies.ToJsonString());
        foreach (var mode in new[] { "normal", "forced", "helper-killed", "two-games" })
        {
            files = Fixture(root);
            var signalName = "Local\\CaptureUpdateFixture" + Guid.NewGuid().ToString("N");
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, signalName);
            var ready = files.Resolve("BepInEx/capture-update/fixture-ready");
            if (File.Exists(ready))
                File.Delete(ready);
            var start = new ProcessStartInfo(Path.Combine(root, "AtelierResleriana.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "--update-game-child", root, signalName })
                start.ArgumentList.Add(arg);
            start.Environment["DOTNET_ROOT"] = Path.Combine(root, "nonexistent-dotnet");
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            using var child = Process.Start(start)!;
            Process? helper = null;
            Process? secondChild = null;
            EventWaitHandle? secondSignal = null;
            try
            {
                var readyTimer = Stopwatch.StartNew();
                while (
                    !File.Exists(ready) && !child.HasExited && readyTimer.ElapsedMilliseconds < 5000
                )
                    Thread.Sleep(20);
                if (child.HasExited)
                    throw new InvalidOperationException(
                        "Stand-in exited " + child.ExitCode + ": " + child.StandardError.ReadToEnd()
                    );
                Check(File.Exists(ready), "stand-in reached startup gate");
                var helperStart = new ProcessStartInfo(
                    files.Resolve("dotnet/AtelierCapture.Update.exe")
                )
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (
                    var arg in new[]
                    {
                        "--watch",
                        child.Id.ToString(),
                        child.StartTime.ToUniversalTime().Ticks.ToString(),
                    }
                )
                    helperStart.ArgumentList.Add(arg);
                helperStart.Environment["DOTNET_ROOT"] = Path.Combine(root, "nonexistent-dotnet");
                helperStart.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                helper = Process.Start(helperStart)!;
                var watch = Stopwatch.StartNew();
                var helperLock = files.Resolve("BepInEx/capture-update/helper.lock");
                while (
                    !File.Exists(helperLock)
                    && !helper.HasExited
                    && watch.ElapsedMilliseconds < 5000
                )
                    Thread.Sleep(20);
                Check(
                    !helper.HasExited && File.Exists(helperLock),
                    "external helper reached parent wait"
                );
                Check(ReadVersion(files) == "1.3.9", "active game retains old recorder");
                if (mode == "helper-killed")
                {
                    helper.Kill();
                    Check(helper.WaitForExit(5000), "forced helper exit releases ownership");
                    helper.Dispose();
                    helper = Process.Start(helperStart)!;
                }
                if (mode == "two-games")
                {
                    var secondName = "Local\\CaptureUpdateFixture" + Guid.NewGuid().ToString("N");
                    secondSignal = new EventWaitHandle(
                        false,
                        EventResetMode.ManualReset,
                        secondName
                    );
                    File.Delete(ready);
                    start.ArgumentList[2] = secondName;
                    secondChild = Process.Start(start)!;
                    WaitFile(ready, secondChild);
                }
                if (mode == "forced")
                    child.Kill();
                else
                    signal.Set();
                Check(child.WaitForExit(5000), "stand-in game exits");
                Check(
                    helper.WaitForExit(10000) && helper.ExitCode == 0,
                    "actual helper exits cleanly after " + mode
                );
                if (secondChild is not null)
                {
                    Check(
                        ReadVersion(files) == "1.3.9" && File.Exists(files.Ready),
                        "second active game prevents replacement"
                    );
                    secondSignal!.Set();
                    Check(secondChild.WaitForExit(5000), "second game exits");
                    File.Delete(ready);
                    start.ArgumentList[2] = signalName;
                    signal.Reset();
                    File.WriteAllText(files.Resolve("BepInEx/capture-update/startup-apply"), "yes");
                    using var relaunch = Process.Start(start)!;
                    try
                    {
                        WaitFile(ready, relaunch);
                        Check(
                            ReadVersion(files) == "1.4.0",
                            "next startup installs pending update before capture admission"
                        );
                        signal.Set();
                        Check(relaunch.WaitForExit(5000), "relaunch exits");
                    }
                    finally
                    {
                        if (!relaunch.HasExited)
                        {
                            relaunch.Kill();
                            relaunch.WaitForExit(5000);
                        }
                    }
                }
                Check(
                    ReadVersion(files) == "1.4.0" && !File.Exists(files.Journal),
                    "external helper leaves verified complete set"
                );
                Check(
                    File.ReadAllText(
                        files.Resolve("BepInEx/plugins/Captures/Official/sentinel.zip")
                    ) == "capture-preserved",
                    "helper preserves recording"
                );
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    child.WaitForExit(5000);
                }
                if (secondChild is not null)
                {
                    if (!secondChild.HasExited)
                    {
                        secondChild.Kill();
                        secondChild.WaitForExit(5000);
                    }
                    secondChild.Dispose();
                }
                secondSignal?.Dispose();
                if (helper is not null)
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill();
                        helper.WaitForExit(5000);
                    }
                    helper.Dispose();
                }
            }
        }
        foreach (var process in Process.GetProcessesByName("AtelierCapture.Update"))
        {
            using (process)
            {
                try
                {
                    if (
                        !string.Equals(
                            process.MainModule?.FileName,
                            files.Resolve("dotnet/AtelierCapture.Update.exe"),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;
                    var exited = process.WaitForExit(5000);
                    if (!exited)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    Check(
                        exited,
                        "startup-launched disposable helper finishes before fixture cleanup"
                    );
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }

    internal static void RunGameChild(string root, string signalName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var files = new UpdateFiles(root);
        files.Write(
            files.Resolve("BepInEx/capture-update/last-check.json"),
            new { checked_at = DateTimeOffset.UtcNow }
        );
        UpdateHost.BeforePluginLoad(
            root,
            File.Exists(files.Resolve("BepInEx/capture-update/startup-apply")),
            _ => { }
        );
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        File.WriteAllText(files.Resolve("BepInEx/capture-update/fixture-ready"), "ready");
        using var signal = EventWaitHandle.OpenExisting(signalName);
        if (!signal.WaitOne(30000))
            Environment.Exit(74);
    }

    internal static void RunCrashChild(string root, string phase)
    {
        var files = new UpdateFiles(root);
        var transaction = new UpdateTransaction(
            files,
            stage =>
            {
                if (stage == phase)
                    Environment.Exit(73);
            }
        );
        if (phase.StartsWith("rollback_", StringComparison.Ordinal))
            transaction.Recover();
        else
            transaction.ApplyReady();
    }

    internal static void RunLockChild(string root)
    {
        var files = new UpdateFiles(root);
        var transaction = new UpdateTransaction(files);
        using (transaction.Enter())
        using (transaction.GameLease(false))
        {
            File.WriteAllText(files.Resolve("BepInEx/capture-update/child-ready"), "ready");
            Thread.Sleep(30000);
        }
    }

    private static UpdateFiles Fixture(string root, string version = "1.3.9")
    {
        var files = new UpdateFiles(root);
        files.Initialize();
        foreach (
            var path in new[]
            {
                "BepInEx/plugins",
                "dotnet",
                "BepInEx/config",
                "BepInEx/plugins/Captures/Official",
            }
        )
            Directory.CreateDirectory(files.Resolve(path));
        File.WriteAllText(files.Resolve(UpdateFiles.Targets[0]), "old-capture");
        File.WriteAllText(files.Resolve(UpdateFiles.Targets[1]), "old-native");
        File.WriteAllText(files.Resolve("dotnet/base.dll"), "base");
        File.WriteAllText(
            files.Resolve("BepInEx/plugins/Captures/Official/sentinel.zip"),
            "capture-preserved"
        );
        File.WriteAllText(files.Resolve("BepInEx/config/user.cfg"), "config-preserved");
        object Certificate(string v, FileStamp[] records) =>
            new
            {
                schema = 1,
                runtime_major = 10,
                runtime_version = "10.0.13",
                capture_version = v,
                recovery_contract = UpdateFiles.RecoveryContract,
                supported_client = new { game_version = "2.16.0" },
                configuration_contract = new object[0],
                runtime_files = records,
            };
        var before = UpdateFiles
            .Targets.Take(2)
            .Select(p => UpdateFiles.Stamp(p, files.Resolve(p)))
            .Append(UpdateFiles.Stamp("dotnet/base.dll", files.Resolve("dotnet/base.dll")))
            .ToArray();
        files.Write(files.Resolve(UpdateFiles.Certificate), Certificate(version, before));
        var after = new[]
        {
            UpdateFiles.Stamp(UpdateFiles.Targets[0], product),
            UpdateFiles.Stamp(UpdateFiles.Targets[1], native),
            before[2],
        };
        var certificate = JsonSerializer.SerializeToUtf8Bytes(
            Certificate("1.4.0", after),
            UpdateFiles.Json
        );
        var manifest = new UpdateManifest(
            1,
            "1.4.0",
            UpdateFiles.RecoveryContract,
            [
                after[0],
                after[1],
                new(
                    UpdateFiles.Certificate,
                    certificate.Length,
                    Convert.ToHexStringLower(SHA256.HashData(certificate))
                ),
            ]
        );
        if (File.Exists(files.Ready))
            File.Delete(files.Ready);
        using var zip = ZipFile.Open(files.Ready, ZipArchiveMode.Create);
        Write(zip, "update.json", JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateFiles.Json));
        Write(zip, UpdateFiles.Certificate, certificate);
        Write(zip, UpdateFiles.Targets[0], File.ReadAllBytes(product));
        Write(zip, UpdateFiles.Targets[1], File.ReadAllBytes(native));
        return files;
    }

    private static FileStamp[] Targets(UpdateFiles files) =>
        UpdateFiles.Targets.Select(p => UpdateFiles.Stamp(p, files.Resolve(p))).ToArray();

    private static string ReadVersion(UpdateFiles files)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllBytes(files.Resolve(UpdateFiles.Certificate))
        );
        return doc.RootElement.GetProperty("capture_version").GetString()!;
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        output.Write(bytes);
    }

    private static void Mutate(string path, string name, Action<JsonObject> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(name)!;
        JsonObject json;
        using (var stream = entry.Open())
            json = JsonNode.Parse(stream)!.AsObject();
        change(json);
        entry.Delete();
        Write(zip, name, Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static void WaitFile(string path, Process child)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && !child.HasExited && timer.ElapsedMilliseconds < 5000)
            Thread.Sleep(20);
        Check(File.Exists(path), "child reached lock boundary");
    }

    private static void Check(bool pass, string message)
    {
        assertions++;
        if (!pass)
            throw new InvalidOperationException("Update check failed: " + message);
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            Check(true, message);
            return;
        }
        Check(false, message);
    }

    private static async Task ThrowsAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            Check(true, message);
            return;
        }
        Check(false, message);
    }

    private static string FindRoot()
    {
        for (
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            dir is not null;
            dir = dir.Parent
        )
            if (File.Exists(Path.Combine(dir.FullName, "scripts/build.py")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Capture workspace not found.");
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(response(request));
    }

    private sealed class Area : IDisposable
    {
        private readonly string root = Path.Combine(
            workspace,
            "work/checks/updater-" + Guid.NewGuid().ToString("N")
        );
        private int number;

        internal string New()
        {
            var path = Path.Combine(root, (++number).ToString());
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}

internal static class UpdateHostChecks
{
    internal static void Run() => UpdateChecks.RunHost();
}
