using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// Shared switches of the fake processor. The control endpoints flip them.
/// </summary>
internal static class BranchProcessorSwitches
{
    private static volatile bool _hold;
    private static volatile bool _pause;

    /// <summary>
    /// When true, a branch named <c>feature/hold</c> stays in the Processing state until the switch is turned off.
    /// </summary>
    public static bool Hold
    {
        get => _hold;
        set => _hold = value;
    }

    /// <summary>
    /// When true, the dispatcher starts no new branch job, so a queued task stays Pending.
    /// </summary>
    public static bool Pause
    {
        get => _pause;
        set => _pause = value;
    }
}

/// <summary>
/// Replaces wiki generation, so no clone, no model call and no network access happens. The branch name decides
/// the result: <c>feature/hold</c> waits on the hold switch, <c>feature/fail</c> fails, every other branch completes.
/// </summary>
internal sealed class FakeBranchProcessor : IRepositoryBranchProcessor
{
    public async Task<string?> ProcessBranchAsync(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? generationTaskId,
        bool forceFullGeneration,
        CancellationToken cancellationToken = default)
    {
        if (branch.BranchName == "feature/fail")
        {
            throw new InvalidOperationException("Simulated generation failure.");
        }

        if (branch.BranchName == "feature/hold")
        {
            while (BranchProcessorSwitches.Hold)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
        else
        {
            await Task.Delay(150, cancellationToken);
        }

        return new string('a', 40);
    }
}
