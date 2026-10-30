namespace Scan;

public sealed partial class Session
{
    public void AutoProcessPages(IEnumerable<string> identities, PageCorrection correction, CancellationToken token,
        Action<int, int>? progress = null)
    {
        if (correction is not (PageCorrection.Document or PageCorrection.Photo))
            throw new ArgumentOutOfRangeException(nameof(correction), "Choose Document or Photo cleanup.");
        var selected = ResolvePages(identities);
        var next = pages.ToList();
        var completed = 0;
        for (var i = 0; i < next.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var page = next[i];
            if (!selected.Contains(page.Identity)) continue;
            var angle = PageRenderer.DetectDeskew(this, page, token);
            if (!double.IsFinite(angle) || Math.Abs(angle) > 20)
                throw new InvalidDataException($"Page {i + 1}: deskew must be between -20 and 20 degrees.");
            var edited = page with
            {
                DeskewAngle = angle,
                Adjustments = (page.Adjustments ?? new PageAdjustments()) with { Correction = correction }
            };
            // Verify each result before committing any page; originals are never rewritten.
            using var image = PageRenderer.Render(this, edited, token);
            next[i] = edited;
            progress?.Invoke(++completed, selected.Count);
        }
        token.ThrowIfCancellationRequested();
        if (next.SequenceEqual(pages)) return;
        Change(list => { list.Clear(); list.AddRange(next); });
    }
}
