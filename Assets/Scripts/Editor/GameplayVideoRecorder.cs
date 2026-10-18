using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

/// <summary>
/// Editor-only: records the main camera (the player's CenterEyeAnchor) to an MP4 during Play
/// mode, for sharing gameplay clips. Driven from code (e.g. the MCP) together with
/// <see cref="XRTestInput"/>: <c>Start(path)</c> after entering Play mode, <c>Stop()</c> before
/// leaving it. With no headset the view is the mono editor camera, not the stereo headset view.
/// </summary>
public static class GameplayVideoRecorder
{
    private static RecorderController _controller;

    /// <param name="outputFileWithoutExtension">Absolute path; ".mp4" is appended.</param>
    /// <param name="fieldOfView">Narrower than the headset's 90° so the flat video isn't fish-eyed.</param>
    public static void Start(string outputFileWithoutExtension, int width = 1280, int height = 720, float fieldOfView = 65f)
    {
        Stop();

        Camera camera = Camera.main;
        if (camera != null)
            camera.fieldOfView = fieldOfView;

        var controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "Gameplay";
        movie.Enabled = true;
        movie.EncoderSettings = new CoreEncoderSettings
        {
            Codec = CoreEncoderSettings.OutputCodec.MP4,
            EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High,
        };
        movie.ImageInputSettings = new CameraInputSettings
        {
            Source = ImageSource.MainCamera,
            OutputWidth = width,
            OutputHeight = height,
            CaptureUI = false,
        };
        movie.AudioInputSettings.PreserveAudio = false;
        movie.OutputFile = outputFileWithoutExtension;

        controllerSettings.AddRecorderSettings(movie);
        controllerSettings.SetRecordModeToManual();
        controllerSettings.FrameRate = 30f;

        _controller = new RecorderController(controllerSettings);
        _controller.PrepareRecording();
        _controller.StartRecording();
    }

    public static bool IsRecording => _controller != null && _controller.IsRecording();

    public static void Stop()
    {
        if (_controller != null && _controller.IsRecording())
            _controller.StopRecording();
        _controller = null;
    }
}
