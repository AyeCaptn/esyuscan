using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class TemporaryScansTests : TestFiles
{
    [Test]
    public void TemporaryScansAreUniqueAndDeletedOnDispose()
    {
        using var second = Session.CreateTemporary();
        var first = Session.CreateTemporary();
        var path = first.DirectoryPath;
        AddPages(first, 2);
        Assert.That(path, Is.Not.EqualTo(second.DirectoryPath));
        first.Dispose();
        first.Dispose();
        Assert.That(Directory.Exists(path), Is.False);
        Assert.That(Directory.Exists(second.DirectoryPath), Is.True);
    }

    [Test]
    public async Task SavingConvertsPagesAndOutputSurvivesTemporaryCleanup()
    {
        var scans = Session.CreateTemporary();
        var path = scans.DirectoryPath;
        try
        {
            AddPages(scans, 2);
            using var scanner = new Scanner(Root);
            await scanner.Export(scans, Path.Combine(Root, "saved.pdf"), ExportFormat.CombinedPdf, CancellationToken.None);
            await scanner.Export(scans, Path.Combine(Root, "images"), ExportFormat.Images, CancellationToken.None);
        }
        finally { scans.Dispose(); }
        Assert.That(Directory.Exists(path), Is.False);
        Assert.That(File.Exists(Path.Combine(Root, "saved.pdf")), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(Root, "images"), "*.png"), Has.Length.EqualTo(2));
    }

    [AvaloniaTest]
    public void WindowStartsReadyAndCleansUpOnClose()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        var path = model.Session!.DirectoryPath;
        window.Show();
        Assert.That(model.Session.IsTemporary, Is.True);
        Assert.That(window.FindControl<Button>("ImportFilesButton"), Is.Not.Null);
        Assert.That(window.FindControl<Button>("OpenSessionButton"), Is.Null);
        window.Close();
        Assert.That(Directory.Exists(path), Is.False);
    }

    [AvaloniaTest]
    public async Task NewWarnsBeforeDiscardAndCancelKeepsPages()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            AddPages(model.Session!, 1);
            model.SyncPages();
            var path = model.Session!.DirectoryPath;
            Click(window.FindControl<Button>("NewSessionButton")!);
            var warning = window.OwnedWindows.Single();
            Click(warning.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "KeepScansButton"));
            await Task.Yield();
            Assert.That(model.Session.DirectoryPath, Is.EqualTo(path));
            Assert.That(model.Pages, Has.Count.EqualTo(1));
            Click(window.FindControl<Button>("NewSessionButton")!);
            warning = window.OwnedWindows.Single();
            Click(warning.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiscardScansButton"));
            for (var i = 0; i < 100 && model.Session.DirectoryPath == path; i++) await Task.Delay(10);
            Assert.That(model.Session.DirectoryPath, Is.Not.EqualTo(path));
            Assert.That(model.Pages, Is.Empty);
            Assert.That(Directory.Exists(path), Is.False);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.CloseForTest(); }
    }

    [AvaloniaTest]
    public void SavedStateTracksEditsAndUndoAndNewNeedsNoWarningAfterSave()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            AddPages(model.Session!, 1);
            model.SyncPages();
            Assert.That(model.HasUnsavedScans, Is.True);
            model.MarkSaved();
            Assert.That(model.HasUnsavedScans, Is.False);
            model.Session!.Rotate(0);
            model.SyncPages();
            Assert.That(model.HasUnsavedScans, Is.True);
            model.UndoEdit();
            Assert.That(model.HasUnsavedScans, Is.False);
            var path = model.Session.DirectoryPath;
            Click(window.FindControl<Button>("NewSessionButton")!);
            Assert.That(window.OwnedWindows, Is.Empty);
            Assert.That(Directory.Exists(path), Is.False);
        }
        finally { window.CloseForTest(); }
    }

    [AvaloniaTest]
    [TestCase("pdf", "PDF · combined")]
    [TestCase("png", "PNG · lossless")]
    [TestCase("jpeg", "JPEG · lossy")]
    [TestCase("tiff", "TIFF · multipage")]
    public void SaveUsesTheChosenFileType(string type, string label)
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            AddPages(model.Session!, 1);
            model.SyncPages();
            model.SelectedSaveFileType = model.SaveFileTypes.Single(choice => choice.Value == type);
            Click(window.FindControl<Button>("CombinedExportButton")!);
            var dialog = (ExportWindow)window.OwnedWindows.Single();
            Assert.That(dialog.FindControl<ComboBox>("ExportFileFormat")!.SelectedItem, Is.EqualTo(label));
            dialog.Close(false);
            Assert.That(model.HasUnsavedScans, Is.True);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.CloseForTest(); }
    }

    [AvaloniaTest]
    public async Task ClosingUnsavedScansRequiresConfirmationAndCancelKeepsFiles()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        var path = model.Session!.DirectoryPath;
        try
        {
            AddPages(model.Session, 1);
            model.SyncPages();
            window.Close();
            var warning = window.OwnedWindows.Single();
            Assert.That(warning.Title, Is.EqualTo("Close with unsaved scans?"));
            Assert.That(window.IsVisible, Is.True);
            Assert.That(Directory.Exists(path), Is.True);
            // Repeated close requests must not stack confirmation dialogs.
            window.Close();
            Assert.That(window.OwnedWindows.Count(), Is.EqualTo(1));
            Click(warning.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "KeepScansButton"));
            await Task.Yield();
            Assert.That(window.IsVisible, Is.True);
            Assert.That(model.HasUnsavedScans, Is.True);
            Assert.That(File.Exists(model.Session.OriginalPath(model.Session.Pages[0])), Is.True);
            window.Close();
            warning = window.OwnedWindows.Single();
            Click(warning.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiscardScansButton"));
            for (var i = 0; i < 100 && window.IsVisible; i++) await Task.Delay(10);
            Assert.That(window.IsVisible, Is.False);
            Assert.That(Directory.Exists(path), Is.False);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.CloseForTest(); }
    }

    [AvaloniaTest]
    public void SavedScansCloseWithoutConfirmation()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        var path = model.Session!.DirectoryPath;
        AddPages(model.Session, 1);
        model.SyncPages();
        model.MarkSaved();
        window.Close();
        Assert.That(window.IsVisible, Is.False);
        Assert.That(window.OwnedWindows, Is.Empty);
        Assert.That(Directory.Exists(path), Is.False);
    }

    [AvaloniaTest]
    public void BusyCloseWaitsForWorkToStopBeforeAskingToDiscard()
    {
        var model = new MainViewModel();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            AddPages(model.Session!, 1);
            model.SyncPages();
            model.Busy = true;
            window.Close();
            Assert.That(window.IsVisible, Is.True);
            Assert.That(window.OwnedWindows, Is.Empty);
            model.Busy = false;
            window.Close();
            Assert.That(window.OwnedWindows.Single().Title, Is.EqualTo("Close with unsaved scans?"));
        }
        finally { model.Busy = false; foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.CloseForTest(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
