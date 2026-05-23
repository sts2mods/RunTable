using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace RunTable;

[ModInitializer("Initialize")]
public static class RunTableMod
{
    public const string Version = "0.1.0";
    public const string LogPrefix = "[Run Table] ";

    private static Harmony? _harmony;

    public static void Initialize()
    {
        GD.Print($"{LogPrefix}v{Version} initializing...");
        try
        {
            _harmony = new Harmony("austin.badgevault");
            ModHelpers.TryPatchAll(_harmony, typeof(RunTableMod).Assembly, LogPrefix);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix}Init failed: {ex}");
        }
    }
}
