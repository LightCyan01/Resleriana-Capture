using System.Reflection;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace AtelierCapture.Updater;

[PatcherPluginInfo("AtelierCapture.Updater", "Capture updater", "1.0.0")]
public sealed class UpdatePatcher : BasePatcher
{
    public override void Initialize()
    {
        var enabled = Config
            .Bind(
                "Updates",
                "Enabled",
                true,
                "Download compatible stable capture releases and install after the game exits."
            )
            .Value;
        var assembly = Assembly.LoadFrom(
            Path.Combine(Paths.GameRootPath, "dotnet", "AtelierCapture.Update.dll")
        );
        var initialize = assembly
            .GetType("AtelierCapture.Update.UpdateHost", true)!
            .GetMethod("BeforePluginLoad", BindingFlags.Public | BindingFlags.Static)!;
        initialize.Invoke(
            null,
            [Paths.GameRootPath, enabled, (Action<string>)(message => Log.LogInfo(message))]
        );
    }
}
