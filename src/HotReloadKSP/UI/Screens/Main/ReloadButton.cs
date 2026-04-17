namespace HotReloadKSP.UI.Screens.Main;

internal class ReloadButton : DebugScreenButton
{
    public MainScreenContent screen;

    protected override void OnClick() => screen.ReloadSelected();
}
