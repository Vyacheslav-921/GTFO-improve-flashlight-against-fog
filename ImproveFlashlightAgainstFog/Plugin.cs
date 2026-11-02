using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace ImproveFlashlightAgainstFog;

[BepInPlugin(Plugin.Guid, "Improve Flashlight Against Fog", "1.1.0")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "improveflashlightagainstfog";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> CutFog;
    internal static ConfigEntry<float> CutDensity;

    public override void Load()
    {
        Logger = Log;
        CutFog = Config.Bind(
            "Flashlight",
            "Cut fog in the beam",
            true,
            "While a flashlight is on, carve infection fog along the beam the same way a fog repeller or heavy fog turbine does. How far the carve reaches follows the light's brightness, and it is gone when the light turns off or points away.");
        CutDensity = Config.Bind(
            "Flashlight",
            "Cut density",
            -4f,
            "Density written into the fog-sphere cutter. Negative values remove fog. If the beam makes fog thicker, flip the sign.");

        ClassInjector.RegisterTypeInIl2Cpp<FlashlightFogCut>();
        AddComponent<FlashlightFogCut>();
        Logger.LogInfo("Improve Flashlight Against Fog loaded.");
    }
}
