using UnityEngine;

public class Billboard : MonoBehaviour
{
    private Transform target;

    void Start()
    {
        // Find the CameraRig in the scene by name
        GameObject rig = GameObject.Find("CameraRig");
        if (rig != null)
        {
            // If your camera is a child of the rig, grab it
            Camera cam = rig.GetComponentInChildren<Camera>();
            if (cam != null)
                target = cam.transform;
        }

        // Fallback: just use the main camera if CameraRig not found
        if (target == null && Camera.main != null)
            target = Camera.main.transform;
    }

    void LateUpdate()
    {
        if (target != null)
        {
            transform.LookAt(transform.position + target.forward);
        }
    }
}
