using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using Oculus.Platform;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.SceneManagement; // <-- added

public class ConfigManager : MonoBehaviour
{
    public GameConfig config;
    
    [SerializeField] private GameObject configCanvas;
    [SerializeField] private FindSpawnPositions tankSpawner;
    [SerializeField] private FindSpawnPositions crateSpawner;

    [SerializeField] private SliderPanelCfg numberOfEnemiesCFG;
    [SerializeField] private SliderPanelCfg numberOfCratesCFG;

    // Start is called before the first frame update
    void Awake()
    {
        Debug.Log("ConfigManager started");
        if (tankSpawner != null)
        {
            tankSpawner.SpawnAmount = GameConfig.numberOfEnemies;
        }

        if (crateSpawner != null)
        {
            crateSpawner.SpawnAmount = GameConfig.numberOfCrates;
        }
    }

    void Start()
    {
        if (numberOfEnemiesCFG != null)
            numberOfEnemiesCFG.SetValue(GameConfig.numberOfEnemies);

    
        if (numberOfCratesCFG != null)
            numberOfCratesCFG.SetValue(GameConfig.numberOfCrates);
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
        GameConfig.numberOfEnemies = numberOfEnemiesCFG.GetValue();
        GameConfig.numberOfCrates = numberOfCratesCFG.GetValue();
    }
}
