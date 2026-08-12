using System;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public static class CaptureConflictDetector
    {
        private static readonly string[] BlockingTypeNames =
        {
            "Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator",
            "BlackStartX.GestureManager.GestureManager"
        };

        public static bool TryFindActiveConflict(out string message)
        {
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour == null || !behaviour.isActiveAndEnabled ||
                    !behaviour.gameObject.scene.IsValid() || !behaviour.gameObject.scene.isLoaded)
                {
                    continue;
                }

                var fullName = behaviour.GetType().FullName;
                for (var index = 0; index < BlockingTypeNames.Length; index++)
                {
                    if (!string.Equals(fullName, BlockingTypeNames[index], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    message = fullName.StartsWith("Lyuma.", StringComparison.Ordinal)
                        ? "Av3Emulator is active and may create competing avatar clones. Disable it before capture."
                        : "Gesture Manager is active and may create or drive a competing avatar clone. Disable it " +
                          "before capture.";
                    return true;
                }
            }

            message = null;
            return false;
        }
    }
}
