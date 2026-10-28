using System.Text.Json;

namespace Scan;

public record PdfPageSource(string FileName, int PageIndex);
public record SessionPage(string FileName, int Rotation = 0, double DeskewAngle = 0, PageCrop? Crop = null, PdfPageSource? PdfSource = null, PageAdjustments? Adjustments = null)
{
    // Usually the original filename is already unique. Shared-original page instances
    // receive an optional ID; old readers safely ignore this navigation-only field.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity => Id ?? FileName;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PreservePdf => PdfSource is not null && DeskewAngle == 0 && Crop is null && Adjustments?.IsIdentity != false;
}
public record SessionManifest(int Version, string Name, List<SessionPage> Pages)
{
    public ManualDuplexState? ManualDuplex { get; init; }
    public BatchState? Batch { get; init; }
}

public sealed partial class Session : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileStream lease;
    private readonly Stack<List<SessionPage>> undo = new();
    private readonly Stack<List<SessionPage>> redo = new();
    private List<SessionPage> pages;

    private Session(string directory, SessionManifest manifest)
    {
        DirectoryPath = Path.GetFullPath(directory);
        lease = new FileStream(Path.Combine(DirectoryPath, ".esyuscan.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Name = manifest.Name;
        pages = manifest.Pages;
        ManualDuplex = manifest.ManualDuplex;
        Batch = manifest.Batch;
    }

    public string DirectoryPath { get; }
    public string Name { get; }
    public IReadOnlyList<SessionPage> Pages => pages.AsReadOnly();
    internal object HistoryState => pages;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public static Session Create(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Give the session a name.", nameof(name));
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Choose a new, empty session folder.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(directory, "originals"));
        var session = new Session(directory, new SessionManifest(1, name.Trim(), []));
        try { session.Save(); }
        catch { session.Dispose(); throw; }
        return session;
    }

    public static Session Open(string directory)
    {
        var manifest = JsonSerializer.Deserialize<SessionManifest>(
            File.ReadAllText(Path.Combine(directory, "session.json")))
            ?? throw new InvalidDataException("This is not an esyuscan session.");
        if (manifest.Version is not (1 or 2 or 3) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Pages is null)
            throw new InvalidDataException("This session format is not supported.");
        foreach (var page in manifest.Pages)
        {
            if (page is null || !IsFileName(page.FileName)
                || page.Rotation is not (0 or 90 or 180 or 270)
                || !double.IsFinite(page.DeskewAngle) || Math.Abs(page.DeskewAngle) > 20)
                throw new InvalidDataException("The session contains an invalid page.");
            page.Adjustments?.Validate();
            if (page.Adjustments is not null && manifest.Version < 3)
                throw new InvalidDataException("Page adjustments require session format 3.");
            if (!File.Exists(Path.Combine(directory, "originals", page.FileName)))
                throw new FileNotFoundException($"Session page is missing: {page.FileName}");
            if (page.PdfSource is { } source)
            {
                if (manifest.Version < 2 || !IsFileName(source.FileName) || source.PageIndex < 0
                    || !source.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The session contains an invalid PDF source.");
                if (!File.Exists(Path.Combine(directory, "originals", source.FileName)))
                    throw new FileNotFoundException($"Session PDF source is missing: {source.FileName}");
            }
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Pages.Count; i++)
        {
            var page = manifest.Pages[i];
            if (string.IsNullOrWhiteSpace(page.Identity) || !identities.Add(page.Identity))
            {
                do { page = page with { Id = Guid.NewGuid().ToString("N") }; } while (!identities.Add(page.Identity));
                manifest.Pages[i] = page;
            }
        }
        ValidateDuplex(manifest);
        ValidateBatch(manifest);
        var session = new Session(directory, manifest);
        try
        {
            session.RecoverDuplexPass();
            session.RecoverBatch();
            foreach (var page in manifest.Pages) PageRenderer.ValidateCrop(session, page);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public string OriginalPath(SessionPage page) => Path.Combine(DirectoryPath, "originals", page.FileName);
    public string NextOriginalPath() => Path.Combine(DirectoryPath, "originals", $"{Guid.NewGuid():N}.png");
    public string NextPdfSourcePath() => Path.Combine(DirectoryPath, "originals", $"{Guid.NewGuid():N}.pdf");
    public string PdfSourcePath(PdfPageSource source) => Path.Combine(DirectoryPath, "originals", source.FileName);

    public void Add(string originalPath, PdfPageSource? pdfSource = null)
    {
        var fileName = Path.GetFileName(originalPath);
        if (Path.GetFullPath(originalPath) != Path.GetFullPath(Path.Combine(DirectoryPath, "originals", fileName))
            || !File.Exists(originalPath))
            throw new ArgumentException("The page must be saved in this session's originals folder.");
        if (pdfSource is not null && (!IsFileName(pdfSource.FileName) || pdfSource.PageIndex < 0
            || !pdfSource.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(PdfSourcePath(pdfSource))))
            throw new ArgumentException("The PDF source must be saved in this session's originals folder.");
        if (Batch is not null)
        {
            if (pdfSource is not null) throw new InvalidOperationException("Finish batch review before importing PDFs.");
            AddBatchPage(fileName);
        }
        else if (ManualDuplex is not null)
        {
            if (pdfSource is not null) throw new InvalidOperationException("Finish manual duplex review before importing PDFs.");
            AddDuplexPage(fileName);
        }
        else
        {
            var page = new SessionPage(fileName, PdfSource: pdfSource);
            while (pages.Any(existing => existing.Identity == page.Identity)) page = page with { Id = Guid.NewGuid().ToString("N") };
            Change(list => list.Add(page));
        }
    }

    public void Rotate(int index) => Change(list => list[index] = list[index] with { Rotation = (list[index].Rotation + 90) % 360 });

    public void SetDeskew(int index, double angle)
    {
        if (!double.IsFinite(angle) || Math.Abs(angle) > 20)
            throw new ArgumentOutOfRangeException(nameof(angle), "Deskew must be between -20 and 20 degrees.");
        if (pages[index].DeskewAngle == angle) return;
        Change(list => list[index] = list[index] with { DeskewAngle = angle });
    }

    public void SetCrop(int index, PageCrop? crop)
    {
        var next = pages[index] with { Crop = crop };
        PageRenderer.ValidateCrop(this, next);
        if (pages[index] == next) return;
        Change(list => list[index] = next);
    }

    public void SetAdjustments(int index, PageAdjustments? adjustments) => SetEdits(index, pages[index] with { Adjustments = adjustments });

    public void ResetEdits(int index) => SetEdits(index, pages[index] with { Rotation = 0, DeskewAngle = 0, Crop = null, Adjustments = null });

    // Commit an editor draft as one atomic, undoable action. Recovery sources cannot be replaced.
    public void SetEdits(int index, SessionPage edited)
    {
        if (edited.FileName != pages[index].FileName || edited.PdfSource != pages[index].PdfSource)
            throw new ArgumentException("Page edits cannot replace original sources.", nameof(edited));
        if (edited.Rotation is not (0 or 90 or 180 or 270) || !double.IsFinite(edited.DeskewAngle) || Math.Abs(edited.DeskewAngle) > 20)
            throw new InvalidDataException("The page contains an invalid rotation or deskew angle.");
        edited.Adjustments?.Validate();
        if (edited.Adjustments?.IsIdentity == true) edited = edited with { Adjustments = null };
        PageRenderer.ValidateCrop(this, edited);
        if (pages[index] == edited) return;
        Change(list => list[index] = edited);
    }

    public void Move(int index, int destination)
    {
        if (destination < 0 || destination >= pages.Count || index == destination) return;
        Change(list =>
        {
            var page = list[index];
            list.RemoveAt(index);
            list.Insert(destination, page);
        });
    }

    public void Remove(int index) => Change(list => list.RemoveAt(index));

    // Destination is an insertion boundary in the original document: 0 through Count.
    // Noncontiguous selections become one block in their existing document order.
    public void MovePages(IEnumerable<string> identities, int destination)
    {
        RequireNoDuplex();
        if (destination < 0 || destination > pages.Count) throw new ArgumentOutOfRangeException(nameof(destination));
        var selected = ResolvePages(identities);
        if (selected.Count == 0) return;
        var block = pages.Where(page => selected.Contains(page.Identity)).ToList();
        var insertion = destination - pages.Take(destination).Count(page => selected.Contains(page.Identity));
        var next = pages.Where(page => !selected.Contains(page.Identity)).ToList();
        next.InsertRange(insertion, block);
        if (next.SequenceEqual(pages)) return;
        Change(list => { list.Clear(); list.AddRange(next); });
    }

    public void RotatePages(IEnumerable<string> identities)
    {
        var selected = ResolvePages(identities);
        if (selected.Count == 0) return;
        Change(list =>
        {
            for (var i = 0; i < list.Count; i++)
                if (selected.Contains(list[i].Identity)) list[i] = list[i] with { Rotation = (list[i].Rotation + 90) % 360 };
        });
    }

    public void RemovePages(IEnumerable<string> identities)
    {
        var selected = ResolvePages(identities);
        if (selected.Count > 0) Change(list => list.RemoveAll(page => selected.Contains(page.Identity)));
    }

    private HashSet<string> ResolvePages(IEnumerable<string> identities)
    {
        RequireNoDuplex();
        var selected = identities.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(id => !pages.Any(page => page.Identity == id)))
            throw new ArgumentException("The selection contains a page outside this session.", nameof(identities));
        return selected;
    }

    public void Undo()
    {
        RequireNoDuplex();
        if (undo.Count == 0) return;
        var previous = undo.Pop();
        try { Save(previous); }
        catch { undo.Push(previous); throw; }
        redo.Push(pages);
        pages = previous;
    }

    public void Redo()
    {
        RequireNoDuplex();
        if (redo.Count == 0) return;
        var next = redo.Pop();
        try { Save(next); }
        catch { redo.Push(next); throw; }
        undo.Push(pages);
        pages = next;
    }

    private void Change(Action<List<SessionPage>> change)
    {
        RequireNoDuplex();
        var next = pages.ToList();
        change(next);
        Save(next);
        undo.Push(pages);
        redo.Clear();
        pages = next;
    }

    public void Save() => Save(pages);

    private void Save(List<SessionPage> next) => Save(next, ManualDuplex);

    private void Save(List<SessionPage> next, ManualDuplexState? duplex) => Save(next, duplex, Batch);

    private void Save(List<SessionPage> next, ManualDuplexState? duplex, BatchState? batch)
    {
        var target = Path.Combine(DirectoryPath, "session.json");
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, new SessionManifest(batch is not null || next.Any(page => page.Adjustments is not null) ? 3 : next.Any(page => page.Crop is not null || page.DeskewAngle != 0 || page.PdfSource is not null) ? 2 : 1, Name, next) { ManualDuplex = duplex, Batch = batch }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => lease.Dispose();

    private static bool IsFileName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..") && name == Path.GetFileName(name);
}
