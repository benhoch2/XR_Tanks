using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class ConifgUI : MonoBehaviour
{
    // Global flag to let gameplay scripts know when the config menu is open
    public static bool IsMenuActive { get; private set; }
    // Canvas to activate (assign in Inspector)
    [SerializeField] private Transform configCanvas;

    [SerializeField] private float menuDistance = 0.5f;
    // Y-button (left controller secondaryButton) hold settings
    [SerializeField] private float yHoldSeconds = 0.5f;
    private float yHoldTimer = 0f;
    private bool yPrevPressed = false;
    private bool holdTriggered = false;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        bool yPressed = false;

        // Check left-hand controller Y button (secondaryButton)
        List<InputDevice> leftDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftDevices);
        foreach (var d in leftDevices)
        {
            if (d.TryGetFeatureValue(CommonUsages.secondaryButton, out bool val) && val)
            {
                yPressed = true;
                break;
            }
        }

        bool menuIsActive = (configCanvas != null) && configCanvas.gameObject.activeSelf;

        if (!menuIsActive)
        {
            // Hold-to-open behavior
            if (yPressed)
            {
                yHoldTimer += Time.deltaTime;
                if (!holdTriggered && yHoldTimer >= yHoldSeconds)
                {
                    if (configCanvas != null && !configCanvas.gameObject.activeSelf)
                    {
                        ActivateConfigCanvas();
                    }
                    holdTriggered = true; // prevent retrigger while held
                }
            }
            else
            {
                // reset when released
                yHoldTimer = 0f;
                holdTriggered = false;
            }
        }
        else
        {
            // Press-to-close behavior (rising edge)
            if (yPressed && !yPrevPressed)
            {
                configCanvas.gameObject.SetActive(false);
                // reset timers so next open requires a full hold again
                yHoldTimer = 0f;
                holdTriggered = false;
            }
        }

        // Keep global flag in sync with the assigned canvas' active state
        IsMenuActive = (configCanvas != null) && configCanvas.gameObject.activeSelf;

        // remember previous button state
        yPrevPressed = yPressed;
    }

    private void ActivateConfigCanvas()
    {
        Transform child = configCanvas;
        if (child == null)
        {
            Debug.LogWarning($"Config UI reference not assigned on {name}.");
            return;
        }

        if (!child.gameObject.activeSelf)
            child.gameObject.SetActive(true);

        child.position = GetSpawnLocation();

        child.LookAt(Camera.main.transform);
        child.Rotate(0, 180, 0); // face the camera

    }

    // simple camera finder: prefer Camera.main, otherwise first Camera in scene
    private Vector3 GetSpawnLocation()
    {
        Transform camTransform = null;
        if (Camera.main != null)
        {
            camTransform = Camera.main.transform;
        }
        else
        {
            var cam = FindObjectOfType<Camera>();
            if (cam != null)
            {
                Debug.LogWarning("No main camera tagged; using first camera found in scene.");
                camTransform = cam.transform;
            }
        }

        if (camTransform == null)
        {
            Debug.LogWarning("No camera found; returning current position as fallback.");
            return transform.position;
        }

        // 1m away from the camera in the XZ plane, keeping the camera's Y value
        Vector3 forwardXZ = new Vector3(camTransform.forward.x, 0f, camTransform.forward.z);
        if (forwardXZ.sqrMagnitude < 1e-6f)
        {
            forwardXZ = Vector3.forward;
        }
        forwardXZ.Normalize();

        Vector3 pos = camTransform.position + forwardXZ * menuDistance;
        pos.y = camTransform.position.y; // keep Y
        return pos;
    }
}

