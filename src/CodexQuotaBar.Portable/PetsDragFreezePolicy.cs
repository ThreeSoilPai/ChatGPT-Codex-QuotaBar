namespace CodexQuotaBar;

internal static class PetsDragFreezePolicy
{
    internal static bool ShouldFreeze(
        bool freezeAlreadyActive,
        bool primaryMouseButtonDown,
        bool interactionStartedOnPets) =>
        primaryMouseButtonDown
        && (freezeAlreadyActive || interactionStartedOnPets);

    internal static bool ShouldDeferVisualUpdate(bool freezeActive) => freezeActive;
}
