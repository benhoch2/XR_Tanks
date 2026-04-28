using UnityEngine;

public class BlinkRenderer : MonoBehaviour
{
    [Tooltip("Seconds between visibility toggles. Lower = faster blink.")]
    public float interval = 0.15f;

    private float _timer;
    private MeshRenderer[] _renderers;

    private void Start()
    {
        _renderers = GetComponentsInChildren<MeshRenderer>(true);
    }

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < interval)
            return;

        _timer = 0f;
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] != null)
                _renderers[i].enabled = !_renderers[i].enabled;
        }
    }
}
