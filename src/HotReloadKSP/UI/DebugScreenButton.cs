using UnityEngine;
using UnityEngine.UI;

namespace HotReloadKSP.UI;

internal abstract class DebugScreenButton : MonoBehaviour
{
    public Button button;

    void Awake()
    {
        SetupValues();
    }

    protected virtual void SetupValues() { }

    protected abstract void OnClick();

    protected virtual void OnEnable()
    {
        button.onClick.AddListener(OnClick);
    }

    protected virtual void OnDisable()
    {
        button.onClick.RemoveListener(OnClick);
    }
}
