using System;

/// <summary>
/// Transport boundary for Interviewer Mode. The initial Unity implementation
/// stays independent from a particular realtime SDK; a LiveKit adapter can
/// implement this interface after the backend exposes an authenticated token
/// endpoint.
/// </summary>
public interface IInterviewerRealtimeService
{
    bool IsAvailable { get; }
    bool IsConnected { get; }
    string ConnectionState { get; }

    void Join(
        string roomCode,
        string displayName,
        InterviewerParticipantRole role,
        Action<bool, string> completed
    );

    void Leave();
    void SetMicrophoneEnabled(bool enabled);
    void SetCameraEnabled(bool enabled);
    void SetScreenShareEnabled(bool enabled, Action<bool, string> completed);
    void PublishWhiteboardSnapshot(byte[] pngBytes);
    void PublishDatasetManifest(string manifestJson);
}

public enum InterviewerParticipantRole
{
    Candidate,
    Interviewer
}

/// <summary>
/// Default bridge used until a realtime SDK and server-issued room tokens are
/// configured. It returns an actionable status instead of pretending that a
/// remote participant is connected.
/// </summary>
public sealed class UnavailableInterviewerRealtimeService : IInterviewerRealtimeService
{
    public bool IsAvailable
    {
        get { return false; }
    }

    public bool IsConnected
    {
        get { return false; }
    }

    public string ConnectionState
    {
        get { return "Local preview"; }
    }

    public void Join(
        string roomCode,
        string displayName,
        InterviewerParticipantRole role,
        Action<bool, string> completed
    )
    {
        if (completed != null)
        {
            completed(
                false,
                "Realtime service is not configured. Add a LiveKit adapter and an " +
                "authenticated backend token endpoint to enable remote interviews."
            );
        }
    }

    public void Leave()
    {
    }

    public void SetMicrophoneEnabled(bool enabled)
    {
    }

    public void SetCameraEnabled(bool enabled)
    {
    }

    public void SetScreenShareEnabled(bool enabled, Action<bool, string> completed)
    {
        if (completed != null)
        {
            completed(
                false,
                "Screen sharing requires an active realtime room."
            );
        }
    }

    public void PublishWhiteboardSnapshot(byte[] pngBytes)
    {
    }

    public void PublishDatasetManifest(string manifestJson)
    {
    }
}
