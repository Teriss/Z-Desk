using ZDesk.Models;

namespace ZDesk.Services;

public sealed record DisplayLayoutChangeResult(string Signature, List<GroupDefinition>? Profile);

/// <summary>Serializes display-profile transitions and rejects stale async results.</summary>
public sealed class DisplayLayoutChangeService
{
    private long _version;
    public bool IsProcessing { get; private set; }
    public bool HasPendingChange { get; private set; }

    // Called on the controller's Dispatcher, like ProcessAsync and its continuations.
    public void NotifyChanged()
    {
        ++_version;
        HasPendingChange = true;
    }

    public async Task<DisplayLayoutChangeResult?> ProcessAsync(
        string previousSignature,
        IEnumerable<GroupDefinition> groups,
        bool autoSwitch,
        Func<string> getSignature,
        Func<string, IEnumerable<GroupDefinition>, Task> save,
        Func<string, Task<List<GroupDefinition>?>> load,
        Func<bool> isActive)
    {
        if (IsProcessing || !HasPendingChange || !isActive()) return null;
        IsProcessing = true;
        HasPendingChange = false;
        var version = _version;
        try
        {
            var signature = getSignature();
            List<GroupDefinition>? profile = null;
            if (autoSwitch && signature != previousSignature)
            {
                // Capture before awaiting I/O; events can mutate the live model.
                await save(previousSignature, SnapshotService.CloneGroups(groups));
                if (version != _version) return null;
                if (!isActive()) { HasPendingChange = true; return null; }
                profile = await load(signature);
            }
            if (version != _version) return null;
            if (!isActive()) { HasPendingChange = true; return null; }
            if (getSignature() != signature)
            {
                NotifyChanged();
                return null;
            }
            return new DisplayLayoutChangeResult(signature, profile);
        }
        finally { IsProcessing = false; }
    }
}
