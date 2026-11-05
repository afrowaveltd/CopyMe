using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Image;

/// <summary>
/// Scriptable stand-in for the Windows source takeover. It lets the automated tests drive every
/// branch of the lifecycle: success, refusal with and without retry, and a failing release.
/// </summary>
public sealed class SimulatedSourceTakeover : ISourceTakeoverService
{
    private readonly IReadOnlyList<SimulatedDisk>? _disks;

    /// <summary>Creates a simulator that also flips the read-only flag of the matching image disk.</summary>
    public SimulatedSourceTakeover(IReadOnlyList<SimulatedDisk>? disks = null) => _disks = disks;

    public bool IsAvailable { get; set; } = true;

    public string? UnavailableReason { get; set; }

    /// <summary>Disk numbers the service was asked to take over, in order.</summary>
    public List<int> TakeOverCalls { get; } = [];

    /// <summary>How many sessions were released. Must equal the number of successful takeovers.</summary>
    public int ReleaseCount { get; private set; }

    /// <summary>When true, the takeover fails with <see cref="Problems"/>.</summary>
    public bool FailTakeOver { get; set; }

    public bool FailTakeOverWithRetryPossible { get; set; } = true;

    /// <summary>When true, the first N attempts fail and the next succeeds.</summary>
    public int FailTakeOverTimes { get; set; }

    public IReadOnlyList<string> Problems { get; set; } = ["svazek nelze uzamknout – je používán"];

    public IReadOnlyList<string> Steps { get; set; } = ["zdrojový disk převzat pro čtení"];

    public IReadOnlyList<string> Warnings { get; set; } = [];

    public bool FailRelease { get; set; }

    public SourceTakeoverReport TakeOver(PhysicalDisk disk, out ISourceTakeoverSession? session)
    {
        session = null;
        TakeOverCalls.Add(disk.Identity.Number);

        int attempt = TakeOverCalls.Count;

        if (FailTakeOver || attempt <= FailTakeOverTimes)
        {
            return new SourceTakeoverReport
            {
                Success = false,
                Steps = Steps,
                Problems = Problems,
                Warnings = Warnings,
                CanRetry = FailTakeOverWithRetryPossible,
            };
        }

        var simulated = _disks?.FirstOrDefault(d => d.Identity.Number == disk.Identity.Number);
        bool wasReadOnly = simulated?.ReadOnly ?? false;

        if (simulated is not null && !wasReadOnly)
        {
            simulated.ReadOnly = true;
        }

        session = new Session(this, simulated, wasReadOnly);
        return new SourceTakeoverReport
        {
            Success = true,
            Steps = Steps,
            Warnings = Warnings,
        };
    }

    private sealed class Session : ISourceTakeoverSession
    {
        private readonly SimulatedSourceTakeover _owner;
        private readonly SimulatedDisk? _disk;
        private readonly bool _wasReadOnly;
        private bool _released;

        public Session(SimulatedSourceTakeover owner, SimulatedDisk? disk, bool wasReadOnly)
        {
            _owner = owner;
            _disk = disk;
            _wasReadOnly = wasReadOnly;
        }

        public SourceReleaseReport Release()
        {
            if (_released) return new SourceReleaseReport { Success = true };
            _released = true;
            _owner.ReleaseCount++;

            // Only undo what the takeover changed: an originally read-only disk stays read-only.
            if (_disk is not null && !_wasReadOnly && !_owner.FailRelease)
            {
                _disk.ReadOnly = false;
            }

            return _owner.FailRelease
                ? new SourceReleaseReport
                {
                    Success = false,
                    Problems = ["read-only příznak se nepodařilo zrušit"],
                }
                : new SourceReleaseReport
                {
                    Success = true,
                    Steps = ["zdrojový disk vrácen do původního stavu"],
                };
        }

        public void Dispose() => Release();
    }
}
