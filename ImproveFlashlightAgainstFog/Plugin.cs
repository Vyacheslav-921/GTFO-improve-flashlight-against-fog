using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace ImproveFlashlightAgainstFog;

[BepInPlugin(Plugin.Guid, "Improve Flashlight Against Fog", "1.4.4")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "improveflashlightagainstfog";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> CutFog;
    internal static ConfigEntry<float> CutDensity;
    internal static ConfigEntry<float> CutLength;
    internal static ConfigEntry<float> CutRadius;
    internal static ConfigEntry<float> CutDelay;
    internal static ConfigEntry<float> CutHold;
    internal static ConfigEntry<float> CutReturn;

    public override void Load()
    {
        Logger = Log;
        CutFog = Config.Bind(
            "Flashlight",
            "Cut fog in the beam",
            true,
            "While a flashlight is on, carve infection fog along the beam the same way a fog repeller or heavy fog turbine does. How far the carve reaches follows the light's brightness and how far that light throws, so a long range flashlight clears farther than a weapon light. Anti-fog delay, Anti-fog full clear, and Anti-fog return period decide when a lit spot clears, how long it stays fully clear, and how long fog takes to fill back in.");
        CutDensity = Config.Bind(
            "Flashlight",
            "Cut density",
            -4f,
            "How hard the beam clears fog. Negative values remove it. -4 is a full clear in normal infection fog; use a larger negative in heavier fog. If the beam makes fog thicker, flip the sign.");
        CutLength = Config.Bind(
            "Flashlight",
            "Cut length modifier",
            1f,
            new ConfigDescription(
                "Multiplies how far the flashlight clears fog. 1 keeps the distance from the light's brightness and throw. Walls still stop the carve.",
                new AcceptableValueRange<float>(0.1f, 5f)));
        CutRadius = Config.Bind(
            "Flashlight",
            "Cut radius modifier",
            1f,
            new ConfigDescription(
                "Multiplies how wide the cleared tunnel is. 1 is the normal width. This does not change how far the carve reaches.",
                new AcceptableValueRange<float>(0.1f, 5f)));
        CutDelay = Config.Bind(
            "Flashlight",
            "Anti-fog delay",
            0.4f,
            new ConfigDescription(
                "Seconds a lit spot takes to reach a full clear. The hole grows in that spot while the light is on it, and it does not slide with the beam. 0 uses the full size immediately.",
                new AcceptableValueRange<float>(0f, 15f)));
        float previous = 1.25f;
        if (Config.TryGetEntry("Flashlight", "Anti-fog persist period", out ConfigEntry<float> legacy))
        {
            previous = legacy.Value;
            Config.Remove(legacy.Definition);
        }

        CutHold = Config.Bind(
            "Flashlight",
            "Anti-fog full clear",
            previous,
            new ConfigDescription(
                "Seconds a spot stays fully clear after the flashlight leaves it. 0 starts the return as soon as the light moves off.",
                new AcceptableValueRange<float>(0f, 15f)));
        CutReturn = Config.Bind(
            "Flashlight",
            "Anti-fog return period",
            previous,
            new ConfigDescription(
                "Seconds fog takes to fill a spot back in after the full clear ends. 0 fills that spot back at once.",
                new AcceptableValueRange<float>(0f, 15f)));

        ClassInjector.RegisterTypeInIl2Cpp<FlashlightFogCut>();
        AddComponent<FlashlightFogCut>();
        Logger.LogInfo("Improve Flashlight Against Fog loaded.");
    }
}
