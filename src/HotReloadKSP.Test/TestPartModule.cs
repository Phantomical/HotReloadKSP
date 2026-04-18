using UnityEngine;

namespace HotReloadKSP.Test;

/// <summary>
/// Toy PartModule used to exercise HotReloadKSP's PartModule reload path.
/// Shows a single string field in the Part Action Window so a reload is
/// immediately visible by editing the default message and reloading.
/// </summary>
public class TestPartModule : PartModule
{
    const string GroupName = "Hot-Reload";
    const string GroupDisplayName = "Hot Reload!";

    [KSPField(
        guiActive = true,
        guiActiveEditor = true,
        guiName = "Test",
        groupName = GroupName,
        groupDisplayName = GroupDisplayName
    )]
    public string message = "I'm a test string!";

    [KSPField(
        isPersistant = true,
        guiActive = true,
        guiName = "A button!",
        groupName = GroupName,
        groupDisplayName = GroupDisplayName
    )]
    [UI_Toggle(enabledText = "yes", disabledText = "no")]
    public bool button = false;
}
