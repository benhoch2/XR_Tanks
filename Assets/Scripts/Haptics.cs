using System.Collections;
using UnityEngine;

/// <summary>
/// Tiny helper around OVRInput.SetControllerVibration that schedules an
/// automatic stop after a fixed duration. Pass any active MonoBehaviour as
/// the runner — it owns the StartCoroutine call. Uses unscaled time so the
/// pulse still completes when Time.timeScale is 0 (e.g. with the Immersive
/// Debugger menu open).
/// </summary>
public static class Haptics
{
    public static void Pulse(MonoBehaviour runner, OVRInput.Controller controller, float frequency, float amplitude, float durationSeconds)
    {
        if (runner == null || !runner.isActiveAndEnabled)
            return;
        if (durationSeconds <= 0f)
            return;

        OVRInput.SetControllerVibration(frequency, amplitude, controller);
        runner.StartCoroutine(StopAfter(controller, durationSeconds));
    }

    private static IEnumerator StopAfter(OVRInput.Controller controller, float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        OVRInput.SetControllerVibration(0f, 0f, controller);
    }
}
