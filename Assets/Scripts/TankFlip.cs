using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class TankFlip : MonoBehaviour
{
    // How long both X and Y must be held to trigger (seconds)
    public float holdDuration = 3f;

    // Upward velocity applied after reset
    public float upVelocity = 2f;

    private InputDevice leftDevice;
    private float holdTimer = 0f;
    private bool triggered = false;
    private Rigidbody rb;

    // Start is called before the first frame update
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        TryInitializeLeftDevice();
    }

    void TryInitializeLeftDevice()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller, devices);
        if (devices.Count > 0)
        {
            leftDevice = devices[0];
        }
    }

    // Update is called once per frame
    void Update()
    {
        // ensure we have a valid device reference
        if (!leftDevice.isValid)
        {
            TryInitializeLeftDevice();
        }

        if (!leftDevice.isValid)
        {
            // no left controller available this frame
            return;
        }

        bool xPressed = false;
        bool yPressed = false;

        leftDevice.TryGetFeatureValue(CommonUsages.primaryButton, out xPressed);   // typically X on left
        leftDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out yPressed); // typically Y on left

        if (xPressed && yPressed)
        {
            holdTimer += Time.deltaTime;
            if (!triggered && holdTimer >= holdDuration)
            {
                triggered = true;
                PerformResetAndBounce();
            }
        }
        else
        {
            holdTimer = 0f;
            triggered = false;
        }
    }

    private void PerformResetAndBounce()
    {
        // Reset rotation to zero
        transform.rotation = Quaternion.identity;
        transform.localRotation = Quaternion.identity;

        // Ensure we have Rigidbody reference
        if (rb == null) rb = GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.velocity = Vector3.up * upVelocity;
        }
        else
        {
            Debug.LogWarning("TankFlip: Rigidbody not found. Cannot apply velocity. Applying small position bump as fallback.", this);
            transform.position += Vector3.up * 0.1f;
        }
    }
}
