using HotReloadKSP.UI.Screens.Main;
using KSP.UI.Screens.DebugToolbar;
using UnityEngine;

namespace HotReloadKSP.UI;

/// <summary>
/// Registers HotReloadKSP into the Alt+F12 debug menu.
/// Runs once at MainMenu, after DebugScreenSpawner has created the debug screen.
/// </summary>
[KSPAddon(KSPAddon.Startup.MainMenu, once: true)]
internal class DebugMenuScreen : MonoBehaviour
{
    void Start()
    {
        if (!DebugUIManager.Initialize())
        {
            Log.Error("Failed to initialize DebugUIManager, skipping debug menu registration");
            return;
        }

        var mainScreen = DebugUIManager.CreateScreenPrefab<MainScreenContent>(
            "HotReloadKSP_DebugScreen"
        );
        mainScreen.GetComponent<MainScreenContent>().BuildUI();
        AddDebugScreen(null, "HotReloadKSP", "Hot Reload", mainScreen);
    }

    static void AddDebugScreen(string parentName, string name, string text, RectTransform prefab)
    {
        DebugScreenSpawner.Instance.debugScreens.screens.Add(
            new LabelledScreenWrapper()
            {
                parentName = parentName,
                name = name,
                text = text,
                screen = prefab,
            }
        );
    }

    class LabelledScreenWrapper : AddDebugScreens.ScreenWrapper
    {
        public override string ToString() => name;
    }
}
