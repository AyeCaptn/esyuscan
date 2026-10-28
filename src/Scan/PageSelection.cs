namespace Scan;

public sealed record PageSelectionSnapshot(string[] Selected, string? Focus, string? Anchor, int FocusIndex);

/// <summary>Window-local navigation state. SessionPage.Identity survives edits, reordering, and shared originals.</summary>
public sealed class PageSelection
{
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private string[] order = [];
    private string? anchor;
    public IReadOnlySet<string> Selected => selected;
    public string? Focus { get; private set; }

    public PageSelectionSnapshot Snapshot() => new(selected.ToArray(), Focus, anchor, Array.IndexOf(order, Focus));

    public void Reconcile(IEnumerable<string> identities, PageSelectionSnapshot? restore = null)
    {
        var previous = restore ?? Snapshot();
        order = identities.ToArray();
        selected.Clear();
        selected.UnionWith(previous.Selected.Where(order.Contains));
        Focus = order.Contains(previous.Focus) ? previous.Focus : order.ElementAtOrDefault(Math.Clamp(previous.FocusIndex, 0, Math.Max(0, order.Length - 1)));
        anchor = order.Contains(previous.Anchor) ? previous.Anchor : Focus;
        // A deletion/import into an empty document recovers a useful nearest page.
        if (selected.Count == 0 && Focus is not null && (previous.Selected.Length > 0 || previous.Focus is null)) selected.Add(Focus);
    }

    public void Select(int index, bool toggle = false, bool range = false, bool focusOnly = false)
    {
        if (index < 0 || index >= order.Length) return;
        Focus = order[index];
        if (focusOnly) return;
        if (range)
        {
            var start = Array.IndexOf(order, anchor);
            if (start < 0) start = index;
            if (!toggle) selected.Clear();
            selected.UnionWith(order.Skip(Math.Min(start, index)).Take(Math.Abs(start - index) + 1));
        }
        else
        {
            anchor = Focus;
            if (toggle) { if (!selected.Remove(Focus)) selected.Add(Focus); }
            else { selected.Clear(); selected.Add(Focus); }
        }
    }

    public void SelectAll() => selected.UnionWith(order);
}
