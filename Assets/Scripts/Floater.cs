using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Floater : MonoBehaviour
{
    [Tooltip("Maximum vertical offset from the starting position.")]
    public float amplitude = 0.1f;

    [Tooltip("Cycles per second.")]
    public float frequency = 1f;

    private float startY;
    private float startTime;

    // random phase in radians
    private float phase = 0f;

    // Start is called before the first frame update
    void Start()
    {
        startY = transform.position.y;
        startTime = Time.time;
        // choose a random phase so instances are out of sync
        phase = Random.Range(0f, Mathf.PI * 2f);
    }

    // Update is called once per frame
    void Update()
    {
        float elapsed = Time.time - startTime;
        // add random phase to the sine so each object floats differently
        float offset = Mathf.Sin(elapsed * frequency * 2f * Mathf.PI + phase) * amplitude;
        Vector3 pos = transform.position;
        pos.y = startY + offset;
        transform.position = pos;
    }
}
