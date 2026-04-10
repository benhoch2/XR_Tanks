using UnityEngine;
using UnityEngine.InputSystem;

public class ToggleDebugMesh : MonoBehaviour
{
    public GameObject debugMesh;

    private Keyboard keyboard;
    private bool meshVisible = true;
    private bool wasPressed = false;

    void Start()
    {
        keyboard = Keyboard.current;
    }

    void Toggle()
    {
        if (debugMesh == null) return;

        meshVisible = !meshVisible;

        var renderers = debugMesh.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
            r.enabled = meshVisible;

        debugMesh.SetActive(meshVisible);
    }

    void Update()
    {
        if (debugMesh == null) return;

        // Keyboard toggle (M key)
        if (keyboard != null && keyboard.mKey.wasPressedThisFrame)
        {
            Toggle();
            return;
        }

        // VR toggle: left controller Y button via OVRInput
        bool pressed = OVRInput.Get(OVRInput.Button.Four); // Button.Four = Y on left controller
        if (pressed && !wasPressed)
        {
            Toggle();
        }
        wasPressed = pressed;
    }
}
