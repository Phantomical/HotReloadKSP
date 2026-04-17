using UnityEngine;
using UnityEngine.UI;

namespace HotReloadKSP.UI;

internal abstract class DebugScreenButton : MonoBehaviour
{
    public Button button;

    void Awake()
    {
        button.onClick.AddListener(OnClick);
        SetupValues();
    }

    protected virtual void SetupValues() { }

    protected abstract void OnClick();
}
