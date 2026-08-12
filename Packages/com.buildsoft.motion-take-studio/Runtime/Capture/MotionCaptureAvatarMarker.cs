using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    /// <summary>
    /// Inert marker used to hand an exact temporary clone to optional editor
    /// preprocess integrations. No capture logic runs on an uploaded avatar.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class MotionCaptureAvatarMarker : MonoBehaviour
    {
        [SerializeField, HideInInspector] private string sessionId;
        [SerializeField, HideInInspector] private string sourceGlobalObjectId;

        public string SessionId => sessionId;
        public string SourceGlobalObjectId => sourceGlobalObjectId;

        public void Configure(string captureSessionId, string sourceAvatarGlobalObjectId)
        {
            sessionId = captureSessionId ?? string.Empty;
            sourceGlobalObjectId = sourceAvatarGlobalObjectId ?? string.Empty;
        }
    }
}
