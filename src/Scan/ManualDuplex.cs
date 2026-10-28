namespace Scan;

public enum DuplexPhase { Fronts, Reload, Backs, Review }

// File identities, rather than session indices, keep pairing local to this operation.
public sealed record ManualDuplexState(DuplexPhase Phase, string[] Fronts, string[] Backs,
    bool ReverseBacks = false, int BackRotation = 0);

public sealed partial class Session
{
    public ManualDuplexState? ManualDuplex { get; private set; }
    public bool CanApplyDuplex => ManualDuplex is { Phase: DuplexPhase.Review } state
        && state.Fronts.Length > 0 && state.Fronts.Length == state.Backs.Length;

    public void BeginManualDuplex()
    {
        RequireNoDuplex();
        SetDuplex(new(DuplexPhase.Fronts, [], []));
    }

    public void SetDuplexOptions(bool reverseBacks, int backRotation)
    {
        if (backRotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(backRotation));
        var state = ManualDuplex ?? throw new InvalidOperationException("Start a manual duplex operation first.");
        SetDuplex(state with { ReverseBacks = reverseBacks, BackRotation = backRotation });
    }

    public async Task ScanDuplexPass(bool backs, Func<CancellationToken, Task> scan, CancellationToken token)
    {
        var state = ManualDuplex ?? throw new InvalidOperationException("Start a manual duplex operation first.");
        if (backs ? state.Fronts.Length == 0 : state.Backs.Length != 0)
            throw new InvalidOperationException("Finish the front pass before scanning backs.");
        SetDuplex(state with { Phase = backs ? DuplexPhase.Backs : DuplexPhase.Fronts });
        try
        {
            token.ThrowIfCancellationRequested();
            await scan(token);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            // Even cancellation/failure leaves a durable, recoverable review/reload step.
            SetDuplex(ManualDuplex! with { Phase = backs ? DuplexPhase.Review : DuplexPhase.Reload });
        }
    }

    public IReadOnlyList<SessionPage> PreviewDuplex()
    {
        var state = ManualDuplex ?? throw new InvalidOperationException("No duplex operation to review.");
        var captured = state.Fronts.Concat(state.Backs).ToHashSet();
        var result = pages.Where(page => !captured.Contains(page.FileName)).ToList();
        var byName = pages.Where(page => captured.Contains(page.FileName)).ToDictionary(page => page.FileName);
        var backs = state.ReverseBacks ? state.Backs.Reverse().ToArray() : state.Backs;
        // Unequal counts are still shown in full, but applying them is prohibited.
        for (var i = 0; i < Math.Max(state.Fronts.Length, backs.Length); i++)
        {
            if (i < state.Fronts.Length) result.Add(byName[state.Fronts[i]]);
            if (i < backs.Length)
            {
                var page = byName[backs[i]];
                result.Add(page with { Rotation = (page.Rotation + state.BackRotation) % 360 });
            }
        }
        return result.AsReadOnly();
    }

    public void ApplyManualDuplex()
    {
        if (!CanApplyDuplex) throw new InvalidOperationException("Front and back counts must match and be nonzero. Keep scan order to correct missing sheets manually.");
        var next = PreviewDuplex().ToList();
        // Order, rotation and the end of the workflow commit in one manifest replacement.
        Save(next, null);
        undo.Push(pages);
        redo.Clear();
        pages = next;
        ManualDuplex = null;
    }

    public void KeepDuplexScanOrder() => SetDuplex(null);

    private void AddDuplexPage(string fileName)
    {
        var state = ManualDuplex!;
        if (state.Phase is not (DuplexPhase.Fronts or DuplexPhase.Backs))
            throw new InvalidOperationException("Resume a duplex pass before adding pages.");
        if (pages.Any(page => page.FileName == fileName)) throw new InvalidOperationException("This page has already been captured.");
        var next = pages.Append(new SessionPage(fileName)).ToList();
        var nextState = state.Phase == DuplexPhase.Fronts
            ? state with { Fronts = [.. state.Fronts, fileName] }
            : state with { Backs = [.. state.Backs, fileName] };
        Save(next, nextState);
        undo.Push(pages);
        pages = next;
        ManualDuplex = nextState;
        redo.Clear();
    }

    private void RecoverDuplexPass()
    {
        if (ManualDuplex is not { } state) return;
        if (state.Phase == DuplexPhase.Fronts) SetDuplex(state with { Phase = DuplexPhase.Reload });
        else if (state.Phase == DuplexPhase.Backs) SetDuplex(state with { Phase = DuplexPhase.Review });
    }

    private void SetDuplex(ManualDuplexState? state)
    {
        Save(pages, state);
        ManualDuplex = state;
    }

    private void RequireNoDuplex()
    {
        if (ManualDuplex is not null) throw new InvalidOperationException("Finish duplex review or keep scan order before editing the session.");
        if (Batch is not null) throw new InvalidOperationException("Exit batch keeping all pages before editing the session.");
    }

    private static void ValidateDuplex(SessionManifest manifest)
    {
        if (manifest.ManualDuplex is not { } state) return;
        if (!Enum.IsDefined(state.Phase) || state.Fronts is null || state.Backs is null
            || state.BackRotation is not (0 or 90 or 180 or 270)
            || (state.Phase is DuplexPhase.Fronts or DuplexPhase.Reload && state.Backs.Length > 0)
            || (state.Phase is DuplexPhase.Backs or DuplexPhase.Review && state.Fronts.Length == 0))
            throw new InvalidDataException("The session contains invalid manual duplex recovery information.");
        var captured = state.Fronts.Concat(state.Backs).ToArray();
        var names = manifest.Pages.Select(page => page.FileName).ToArray();
        if (captured.Distinct().Count() != captured.Length || captured.Any(name => names.Count(value => value == name) != 1)
            || !names.TakeLast(captured.Length).SequenceEqual(captured))
            throw new InvalidDataException("Manual duplex pages do not match the saved scan order.");
    }
}
