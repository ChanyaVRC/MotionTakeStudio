using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    /// <summary>Durable non-destructive edit recipe for one motion take.</summary>
    [CreateAssetMenu(menuName = "BuildSoft/Motion Take Studio/Edit Recipe")]
    public sealed class MotionEditRecipe : ScriptableObject
    {
        public const int CurrentRecipeVersion = 1;

        [SerializeField] private MotionTakeAsset sourceTake;
        [SerializeField] private MotionPoseCorrectionTrack correctionTrack =
            new MotionPoseCorrectionTrack();
        [SerializeField, Min(1)] private int recipeVersion = CurrentRecipeVersion;
        [SerializeField] private string displayName = "Motion Take Recipe";

        public MotionTakeAsset SourceTake => sourceTake;
        public MotionPoseCorrectionTrack CorrectionTrack
        {
            get
            {
                if (correctionTrack == null)
                {
                    correctionTrack = new MotionPoseCorrectionTrack();
                }

                return correctionTrack;
            }
        }

        public int RecipeVersion => recipeVersion;
        public string DisplayName => displayName;

        public void Initialize(MotionTakeAsset take, string recipeDisplayName = null)
        {
            sourceTake = take;
            recipeVersion = CurrentRecipeVersion;
            displayName = string.IsNullOrWhiteSpace(recipeDisplayName)
                ? (take != null ? take.TakeDisplayName + " Recipe" : "Motion Take Recipe")
                : recipeDisplayName.Trim();
            if (correctionTrack == null)
            {
                correctionTrack = new MotionPoseCorrectionTrack();
            }
        }

        private void OnValidate()
        {
            recipeVersion = Mathf.Max(1, recipeVersion);
            if (correctionTrack == null)
            {
                correctionTrack = new MotionPoseCorrectionTrack();
            }
        }
    }
}
