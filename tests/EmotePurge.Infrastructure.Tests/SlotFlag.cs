using EmotePurge.Infrastructure.Services;

namespace EmotePurge.Infrastructure.Tests;

/// <summary>
/// Explicit slot-reconcile flag state for tests that construct a flag consumer. A constructor without
/// the options argument means "off" in production code; tests state the flag instead of relying on that.
/// A fresh instance per access, so no test can mutate another's options.
/// </summary>
internal static class SlotFlag
{
    public static SlotReconcileOptions On => new() { Enabled = true };

    public static SlotReconcileOptions Off => new() { Enabled = false };
}
