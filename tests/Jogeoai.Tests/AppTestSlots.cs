using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>
/// Pending-slot filter for the App SQL cases that drive the year-2000 calendar by hand. Creating or reconciling an App plan
/// at the real clock may insert an instant slot (11-27-app-first-cycle-now); those cases look at the regular 09:11 slots only.
/// </summary>
internal static class AppTestSlots
{
    /// <summary>
    /// The pending slots without the App plan's instant slots. The key alone does not tell them apart (main and custom plans
    /// have other minutes too): an App plan slot freezes App targets only.
    /// </summary>
    public static IReadOnlyList<PendingCollectionSlot> Regular(this IEnumerable<PendingCollectionSlot> slots)
        => slots.Where(slot => !IsAppInstant(slot)).ToArray();

    /// <summary>An instant slot of an App plan: its key is off 09:11 and its frozen selection holds App targets only.</summary>
    public static bool IsAppInstant(PendingCollectionSlot slot)
        => AppCycleRules.IsInstantSlotKey(slot.LocalSlotKey) && slot.Selection.Targets.Count > 0
            && slot.Selection.Targets.All(target => MonitoringProviders.IsApp(target.Provider));
}
