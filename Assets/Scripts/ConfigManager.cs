using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement; // <-- added

public class ConfigManager : MonoBehaviour
{

    [SerializeField] private GameObject configCanvas;

    // Start is called before the first frame update
    void Start()
    {
        Debug.Log("ConfigManager started");
    }

    // Update is called once per frame
    void Update()
    {

    }

    // Public method to reload the currently active scene
    public void ReloadScene()
    {
        Debug.Log("Reloading scene...");
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }

    public void CancelButton()
    {
        Debug.Log("Cancel button pressed");
        configCanvas.SetActive(false);
    }

    public void SaveButton()
    {
        Debug.Log("Save button pressed");

    }   
}
