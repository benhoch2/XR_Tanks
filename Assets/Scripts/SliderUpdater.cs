using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class SliderUpdater : MonoBehaviour
{
    public Slider slider;
    public TextMeshProUGUI valueText;

    void Start()
    {
        if (slider == null) return;

        // Initialize slider from GameConfig.numberOfEnemies
        float initial = Mathf.Clamp((float)GameConfig.numberOfEnemies, slider.minValue, slider.maxValue);
        Debug.Log($"SliderUpdater setting initial slider value to {initial}");
        slider.value = initial;

        // Initialize text with current slider value
        UpdateText(slider.value);

        // Subscribe to slider value changes
        slider.onValueChanged.AddListener(UpdateText);
    }

    void UpdateText(float value)
    {
        // Cast to int since slider is whole numbers
        int intValue = Mathf.RoundToInt(value);
        valueText.text = intValue.ToString();
    }
}
