namespace LaciSynchroni.PlayerData.Factories;

/// <summary>
/// Controls how strictly animation files are validated against skeleton bone data.
/// </summary>
public enum AnimationValidationMode
{
    /// <summary>
    /// No validation - animations are sent/received without checking bone compatibility.
    /// Use this if you experience issues with false positives.
    /// </summary>
    Unsafe = 0,

    /// <summary>
    /// Standard validation - checks that animation bone indices exist in the skeleton.
    /// Recommended for most users.
    /// </summary>
    Safe = 1,

    /// <summary>
    /// Strict validation - requires exact bone index matching with no tolerance.
    /// Use this if you want maximum protection against incompatible animations.
    /// </summary>
    Safest = 2,
}
