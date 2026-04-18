using UnityEngine;

namespace HotReloadKSP.Test;

/// <summary>
/// Toy PartModule used to exercise HotReloadKSP's PartModule reload path.
/// Shows a single string field in the Part Action Window so a reload is
/// immediately visible by editing the default message and reloading.
/// </summary>
public class TestPartModule : PartModule
{
    [KSPField(
        guiActive = true,
        guiActiveEditor = true,
        guiName = "Hot Reload Test",
        groupName = "Hot Reload",
        groupDisplayName = "Hot Reload"
    )]
    public string message = "I'm a test string! (mk2)";

    public override void OnStart(StartState state)
    {
        Debug.Log($"[HotReloadKSP.Test] TestPartModule.OnStart on {part.name}: {message}");
    }
}
