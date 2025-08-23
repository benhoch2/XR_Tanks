using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Target : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    bool IsBall(GameObject obj)
	{
		if (obj == null) return false;
		// Prefer tag check (set the Ball object's tag to "Ball" if possible)
		if (obj.CompareTag("Ball")) return true;
		// Fallback to name check in case tag isn't set
		string n = obj.name ?? "";
		return n == "Ball" || n.Contains("Ball");
	}

	private void OnCollisionEnter(Collision collision)
	{
		if (IsBall(collision.gameObject))
		{
			Destroy(collision.gameObject);
			Destroy(gameObject);
		}
	}

	private void OnTriggerEnter(Collider other)
	{
		if (IsBall(other.gameObject))
		{
			Destroy(other.gameObject);
			Destroy(gameObject);
		}
	}
}
