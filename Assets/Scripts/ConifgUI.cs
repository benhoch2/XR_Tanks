using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class ConifgUI : MonoBehaviour
{
    // name of the child GameObject to activate
    [SerializeField] private string configCanvasChildName = "ConfigCanvas";

    // distance in meters in front of the camera
    [SerializeField] private float distanceFromCamera = 1.0f;

    // track previous both-pressed state to detect rising edge
    private bool lastBothPressed = false;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        bool leftPressed = false;
        bool rightPressed = false;

        List<InputDevice> leftDevices = new List<InputDevice>();
        List<InputDevice> rightDevices = new List<InputDevice>();

        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftDevices);
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightDevices);

        foreach (var d in leftDevices)
        {
            if (d.TryGetFeatureValue(CommonUsages.primaryButton, out bool val) && val)
            {
                leftPressed = true;
                break;
            }
        }

        foreach (var d in rightDevices)
        {
            if (d.TryGetFeatureValue(CommonUsages.primaryButton, out bool val) && val)
            {
                rightPressed = true;
                break;
            }
        }

        bool bothPressed = leftPressed && rightPressed;

        // rising edge: both pressed now but weren't before
        if (bothPressed && !lastBothPressed)
        {
            ActivateConfigCanvas();
        }

        lastBothPressed = bothPressed;
    }

    private void ActivateConfigCanvas()
    {
        Transform child = transform.Find(configCanvasChildName);
        if (child == null)
        {
            Debug.LogWarning($"Config UI child '{configCanvasChildName}' not found under {name}.");
            return;
        }

        if (!child.gameObject.activeSelf)
            child.gameObject.SetActive(true);

        // position the config UI in front of the camera (only change X and Z, preserve Y)
        Camera cam = FindCamera();
        if (cam == null)
        {
            Debug.LogWarning("No camera found to position ConfigCanvas.");
            return;
        }

        Vector3 desiredWorld = cam.transform.position + cam.transform.forward * distanceFromCamera;
        Vector3 newPos = child.position;
        newPos.x = desiredWorld.x;
        newPos.z = desiredWorld.z;
        // preserve child's current Y
        child.position = newPos;

        // Orient the canvas so it faces the camera: set the child's forward to -camera.forward
        // (this ensures the canvas front faces the camera regardless of its local forward/back)
        child.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
    }

    // simple camera finder: prefer Camera.main, otherwise first Camera in scene
    private Camera FindCamera()
    {
        if (Camera.main != null) return Camera.main;
        Camera any = GameObject.FindObjectOfType<Camera>();
        return any;
    }
}

