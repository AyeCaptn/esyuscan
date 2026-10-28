using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class PageNavigationTests : TestFiles
{
    [Test]
    public void SelectionUsesAnchorForForwardBackwardAndAdditiveRanges()
    {
        var selection = new PageSelection();
        selection.Reconcile(["a", "b", "c", "d", "e"]);
        selection.Select(2);
        selection.Select(4, range: true);
        Assert.That(selection.Selected, Is.EquivalentTo(new[] { "c", "d", "e" }));
        selection.Select(0, range: true);
        Assert.That(selection.Selected, Is.EquivalentTo(new[] { "a", "b", "c" }));
        selection.Select(4, toggle: true, range: true);
        Assert.That(selection.Selected.Count, Is.EqualTo(5));
        selection.Select(4, toggle: true);
        Assert.That(selection.Focus, Is.EqualTo("e"));
        Assert.That(selection.Selected, Does.Not.Contain("e"));
        selection.Select(1, focusOnly: true);
        Assert.That(selection.Selected.Count, Is.EqualTo(4));
        selection.SelectAll();
        Assert.That(selection.Selected.Count, Is.EqualTo(5));
    }

    [Test]
    public void SelectionReconcilesByIdentityAndRecoversNearestAfterDeletion()
    {
        var selection = new PageSelection();
        selection.Reconcile(["a", "b", "c", "d"]);
        selection.Select(1);
        selection.Select(3, toggle: true);
        var snapshot = selection.Snapshot();
        selection.Reconcile(["d", "a", "b", "c", "new"]);
        Assert.That(selection.Selected, Is.EquivalentTo(new[] { "b", "d" }));
        Assert.That(selection.Focus, Is.EqualTo("d"));
        selection.Reconcile(["a", "c"]);
        Assert.That(selection.Selected, Is.EquivalentTo(new[] { "a" }));
        selection.Reconcile(["a", "b", "c", "d"], snapshot);
        Assert.That(selection.Selected, Is.EquivalentTo(new[] { "b", "d" }));
        Assert.That(selection.Focus, Is.EqualTo("d"));
    }

    [TestCase(-1)]
    [TestCase(5)]
    public void ReorderRejectsInvalidBoundariesWithoutChangingSavedState(int boundary)
    {
        using var session = CreateSession();
        AddPages(session, 4);
        var before = session.Pages.ToArray();
        var saved = File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.MovePages([before[1].FileName], boundary));
        Assert.That(session.Pages, Is.EqualTo(before));
        Assert.That(File.ReadAllBytes(Path.Combine(session.DirectoryPath, "session.json")), Is.EqualTo(saved));
    }

    [TestCase(0, new[] { 1, 3, 0, 2, 4 })]
    [TestCase(5, new[] { 0, 2, 4, 1, 3 })]
    [TestCase(2, new[] { 0, 1, 3, 2, 4 })]
    public void ReorderKeepsDocumentOrderAndIsOneUndoableEdit(int boundary, int[] expected)
    {
        using var session = CreateSession();
        AddPages(session, 5);
        var before = session.Pages.ToArray();
        session.MovePages([before[3].FileName, before[1].FileName], boundary);
        Assert.That(session.Pages, Is.EqualTo(expected.Select(i => before[i])));
        session.Undo();
        Assert.That(session.Pages, Is.EqualTo(before));
        session.Redo();
        Assert.That(session.Pages, Is.EqualTo(expected.Select(i => before[i])));
    }

    [Test]
    public void DroppingWithinContiguousBlockIsNoOpAndDoesNotClearRedo()
    {
        using var session = CreateSession();
        AddPages(session, 4);
        session.Rotate(0);
        session.Undo();
        var pages = session.Pages.ToArray();
        session.MovePages([pages[1].FileName, pages[2].FileName], 2);
        Assert.That(session.Pages, Is.EqualTo(pages));
        Assert.That(session.CanRedo, Is.True);
    }

    [Test]
    public void FailedBlockSaveDoesNotChangePagesOrUndoHistory()
    {
        using var session = CreateSession();
        AddPages(session, 3);
        var before = session.Pages.ToArray();
        var manifest = Path.Combine(session.DirectoryPath, "session.json");
        File.Move(manifest, manifest + ".backup");
        Directory.CreateDirectory(manifest);
        Assert.Throws<IOException>(() => session.MovePages([before[0].FileName], 3));
        Assert.That(session.Pages, Is.EqualTo(before));
        Directory.Delete(manifest);
        File.Move(manifest + ".backup", manifest);
        session.Undo();
        Assert.That(session.Pages.Count, Is.EqualTo(2), "Failed save must not add a history step.");
    }

    [Test]
    public void ZoomFitsViewportAndClampsManualSteps()
    {
        var zoom = new PreviewZoom();
        zoom.SetPage(1000, 2000);
        zoom.SetViewport(800, 600);
        Assert.That((zoom.Width, zoom.Height), Is.EqualTo((300, 600)));
        for (var i = 0; i < 80; i++) zoom.Step(1);
        Assert.That(zoom.Scale, Is.EqualTo(PreviewZoom.Maximum));
        for (var i = 0; i < 80; i++) zoom.Step(-1);
        Assert.That(zoom.Scale, Is.EqualTo(PreviewZoom.Minimum));
        zoom.SetViewport(200, 400);
        Assert.That(zoom.Scale, Is.EqualTo(PreviewZoom.Minimum));
        zoom.Fit();
        Assert.That(zoom.Scale, Is.EqualTo(0.2));
        zoom.SetPage(2000, 1000);
        Assert.That(zoom.Scale, Is.EqualTo(0.1));
    }

    [Test]
    public void ZoomOutFromTinyFitNeverEnlargesThePage()
    {
        var zoom = new PreviewZoom();
        zoom.SetPage(2000, 4000);
        zoom.SetViewport(80, 80);
        var fit = zoom.Scale;
        zoom.Step(-1);
        Assert.That(zoom.Scale, Is.EqualTo(fit));
        Assert.That(zoom.IsFit, Is.True);
        zoom.Step(1);
        Assert.That(zoom.Scale, Is.EqualTo(PreviewZoom.Minimum));
        Assert.That(zoom.IsFit, Is.False);
        zoom.SetPage(1, 1);
        zoom.Fit();
        Assert.That(zoom.Scale, Is.EqualTo(PreviewZoom.Maximum), "Fit must respect the upper zoom limit.");
    }

    [AvaloniaTest]
    public void SharedOriginalPagesHaveIndependentStableSelectionAndHistory()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        session.Add(session.OriginalPath(session.Pages[0]));
        var identities = session.Pages.Select(page => page.Identity).ToArray();
        Assert.That(identities.Distinct().Count(), Is.EqualTo(2));
        Assert.That(session.Pages.Select(page => page.FileName).Distinct().Count(), Is.EqualTo(1));
        model.SetSession(session);
        try
        {
            model.SelectPage(1);
            model.RotateSelection();
            Assert.That(session.Pages.Select(page => page.Rotation), Is.EqualTo(new[] { 0, 90 }));
            Assert.That(model.Selection.Selected.Single(), Is.EqualTo(identities[1]));
            model.UndoEdit();
            Assert.That(model.Selection.Focus, Is.EqualTo(identities[1]));
            model.RedoEdit();
        }
        finally { model.Dispose(); }
        using var reopened = Session.Open(Path.Combine(Root, "session"));
        Assert.That(reopened.Pages.Select(page => page.Identity), Is.EqualTo(identities));
        Assert.That(reopened.Pages.Select(page => page.Rotation), Is.EqualTo(new[] { 0, 90 }));
    }

    [Test]
    public void LegacySharedOriginalSessionLoadsAndSavesDistinctPageIdentities()
    {
        string original;
        using (var session = CreateSession())
        {
            AddPages(session, 1);
            original = session.Pages[0].FileName;
        }
        var folder = Path.Combine(Root, "session");
        var manifest = new SessionManifest(1, "Legacy shared original", [new(original), new(original)]);
        File.WriteAllText(Path.Combine(folder, "session.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
        string[] identities;
        using (var session = Session.Open(folder))
        {
            identities = session.Pages.Select(page => page.Identity).ToArray();
            Assert.That(identities.Distinct().Count(), Is.EqualTo(2));
            session.MovePages([identities[1]], 0);
        }
        using var reopened = Session.Open(folder);
        Assert.That(reopened.Pages.Select(page => page.Identity), Is.EqualTo(identities.Reverse()));
    }

    [AvaloniaTest]
    public void MultiEditsAndHistoryRestoreSelectionFocusAndAnchor()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 5);
        model.SetSession(session);
        model.SelectPage(1);
        model.SelectPage(3, toggle: true);
        var selected = model.Selection.Selected.ToArray();
        var focus = model.Selection.Focus;
        model.RotateSelection();
        Assert.That(session.Pages.Select(page => page.Rotation), Is.EqualTo(new[] { 0, 90, 0, 90, 0 }));
        Assert.That(model.Selection.Selected, Is.EquivalentTo(selected));
        model.RemoveSelection();
        Assert.That(session.Pages.Count, Is.EqualTo(3));
        model.UndoEdit();
        Assert.That(model.Selection.Selected, Is.EquivalentTo(selected));
        Assert.That(model.Selection.Focus, Is.EqualTo(focus));
        model.SelectPage(4, range: true);
        Assert.That(model.Selection.Selected.Count, Is.EqualTo(2), "Anchor before removal should be restored.");
        model.UndoEdit();
        Assert.That(session.Pages.All(page => page.Rotation == 0), Is.True);
        model.RedoEdit();
        model.RedoEdit();
        Assert.That(session.Pages.Count, Is.EqualTo(3));
        model.UndoEdit();
        model.ReorderSelection(0);
        Assert.That(model.Selection.Selected.Count, Is.GreaterThan(0));
    }

    [AvaloniaTest]
    public void ImportPreservesSelectionAndUndoRecoversRemovedLastPage()
    {
        using var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 2);
        model.SetSession(session);
        model.SelectPage(1);
        var selected = model.Selection.Focus;
        AddPages(session, 2);
        model.SyncPages();
        Assert.That(model.Selection.Focus, Is.EqualTo(selected));
        Assert.That(model.Selection.Selected.Single(), Is.EqualTo(selected));
        model.SelectAllPages();
        model.RemoveSelection();
        Assert.That(model.Selection.Focus, Is.Null);
        model.UndoEdit();
        Assert.That(model.Selection.Selected.Count, Is.EqualTo(4));
        model.ExportSelected = true;
        model.SelectPage(0);
        model.SelectPage(0, toggle: true);
        Assert.That(model.CanExport, Is.False);
        model.ExportSelected = false;
        Assert.That(model.CanExport, Is.True);
    }

    [AvaloniaTest]
    public void TrayKeyboardAndPointerSelectionDoNotConflictWithEditShortcuts()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 5);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            ClickPage(window, 1);
            ClickPage(window, 3, RawInputModifiers.Control);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(2));
            Assert.That(model.Selection.Focus, Is.EqualTo(model.Pages[3].Page.FileName));
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            Assert.That(model.Selection.Selected, Is.EquivalentTo(model.Pages.Skip(3).Select(page => page.Page.FileName)));
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Control);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(2));
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(5));
            Assert.That(window.FindControl<ListBox>("PageTray")!.SelectedItems!.Count, Is.EqualTo(5));
            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
            Assert.That(session.Pages.All(page => page.Rotation == 90), Is.True);
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Assert.That(session.Pages, Is.Empty);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(5));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ShiftClickToggleAndScannerPickerKeysKeepSelectionAndEditingSeparate()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 5);
        model.SetSession(session);
        model.SetDevices([ScanTestData.Device()]);
        model.SetCapabilities(model.SelectedDevice!, ScanTestData.Caps());
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            ClickPage(window, 3);
            ClickPage(window, 1, RawInputModifiers.Shift);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(3));
            ClickPage(window, 2, RawInputModifiers.Control);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(2));
            Assert.That(model.Selection.Focus, Is.EqualTo(model.Pages[2].Page.FileName));
            Assert.That(model.Selection.Selected, Does.Not.Contain(model.Selection.Focus));
            ClickPage(window, 4, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(4));
            window.FindControl<ComboBox>("DpiPicker")!.Focus();
            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Assert.That(session.Pages.Count, Is.EqualTo(5));
            Assert.That(session.Pages.All(page => page.Rotation == 0), Is.True);
            window.FindControl<CheckBox>("ExportSelectedCheckBox")!.IsChecked = true;
            Assert.That(model.ExportSelection, Is.EquivalentTo(model.Selection.Selected));
            Assert.That(model.ExportSelected, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void DragSelectedBlockToEndAndUndoRestoresOrderAndSelection()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 4);
        model.SetSession(session);
        var before = session.Pages.ToArray();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            ClickPage(window, 0);
            ClickPage(window, 1, RawInputModifiers.Control);
            var start = PagePoint(window, 0);
            var tray = window.FindControl<ListBox>("PageTray")!;
            var end = tray.TranslatePoint(new Point(tray.Bounds.Width / 2, tray.Bounds.Height - 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.That(session.Pages, Is.EqualTo(new[] { before[2], before[3], before[0], before[1] }));
            Assert.That(model.Selection.Selected.Count, Is.EqualTo(2));
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(session.Pages, Is.EqualTo(before));
            Assert.That(model.Selection.Selected, Is.EquivalentTo(before.Take(2).Select(page => page.FileName)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void DragOutsideTrayOrEscapeDoesNotReorder()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 3);
        model.SetSession(session);
        var before = session.Pages.ToArray();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            var start = PagePoint(window, 0);
            var end = PagePoint(window, 2);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.MouseUp(end, MouseButton.Left);
            Assert.That(session.Pages, Is.EqualTo(before));
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(end.X, 10), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(end.X, 10), MouseButton.Left);
            Assert.That(session.Pages, Is.EqualTo(before));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task DuplexReviewAllowsNavigationAndZoomButBlocksAllSelectionEdits()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        session.BeginManualDuplex();
        await session.ScanDuplexPass(false, _ => { AddPages(session, 2); return Task.CompletedTask; }, CancellationToken.None);
        await session.ScanDuplexPass(true, _ => { AddPages(session, 2); return Task.CompletedTask; }, CancellationToken.None);
        model.SetSession(session);
        var before = session.Pages.ToArray();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            ClickPage(window, 2);
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            model.RotateSelection(); model.RemoveSelection(); model.ReorderSelection(0);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.That(model.SelectedPage, Is.EqualTo(model.Pages[3]));
            window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
            Assert.That(model.Zoom.IsFit, Is.False);
            Assert.That(model.CanExport, Is.False);
            Assert.That(session.Pages, Is.EqualTo(before));
            Assert.Throws<InvalidOperationException>(() => session.MovePages([before[0].FileName], 4));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(760, 680)]
    [TestCase(845, 900)]
    [TestCase(1280, 900)]
    public void ZoomPanAndNarrowLayoutKeepControlsAccessibleAndCropUnchanged(int width, int height)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 5, detailed: true);
        session.SetCrop(0, new PageCrop(20, 30, 500, 700));
        model.SetSession(session);
        model.SelectPage(1, toggle: true);
        model.SelectedPage = model.Pages[0];
        var before = session.Pages.ToArray();
        var window = new MainWindow(model, discover: false) { Width = width, Height = height };
        window.Show();
        window.UpdateLayout();
        try
        {
            foreach (var name in new[] { "ZoomInButton", "FitPageButton", "RemoveButton", "CombinedExportButton", "ExportSelectedCheckBox" })
            {
                var control = window.FindControl<Control>(name)!;
                var corner = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
                Assert.That(corner.X, Is.LessThanOrEqualTo(window.ClientSize.Width), name);
                Assert.That(corner.Y, Is.LessThanOrEqualTo(window.ClientSize.Height), name);
            }
            var scroll = window.FindControl<ScrollViewer>("PreviewScroller")!;
            var image = window.FindControl<Image>("SelectedPageImage")!;
            Assert.That(image.Bounds.Width, Is.LessThanOrEqualTo(scroll.Viewport.Width + 1));
            Assert.That(image.Bounds.Height, Is.LessThanOrEqualTo(scroll.Viewport.Height + 1));
            for (var i = 0; i < 25; i++) model.Zoom.Step(1);
            window.UpdateLayout();
            Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width));
            var start = scroll.TranslatePoint(new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start - new Point(50, Math.Min(20, scroll.Viewport.Height / 4)), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start - new Point(50, Math.Min(20, scroll.Viewport.Height / 4)), MouseButton.Left);
            Assert.That(scroll.Offset.X, Is.GreaterThan(0));
            window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
            window.UpdateLayout();
            Assert.That(model.Zoom.IsFit, Is.True);
            Assert.That(scroll.Offset.X, Is.Zero);
            Assert.That(session.Pages, Is.EqualTo(before));
            var folder = Environment.GetEnvironmentVariable("ESYUSCAN_NAVIGATION_SCREENSHOT_DIR");
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(folder, $"navigation-{width}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.ArrowDown, 2)]
    [TestCase(PhysicalKey.ArrowUp, 0)]
    [TestCase(PhysicalKey.ArrowRight, 2)]
    [TestCase(PhysicalKey.ArrowLeft, 0)]
    [TestCase(PhysicalKey.PageDown, 2)]
    [TestCase(PhysicalKey.PageUp, 0)]
    [TestCase(PhysicalKey.Home, 0)]
    [TestCase(PhysicalKey.End, 4)]
    public void PreviewKeyboardBrowsesPagesWithoutFirstFocusingTheTray(PhysicalKey key, int expected)
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 5);
        model.SetSession(session);
        model.SelectPage(1);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<ScrollViewer>("PreviewScroller")!.Focus();

            window.KeyPressQwerty(key, RawInputModifiers.None);

            Assert.That(model.SelectedPage, Is.EqualTo(model.Pages[expected]));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PreviewShiftArrowExtendsSelection()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 4);
        model.SetSession(session);
        model.SelectPage(1);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<ScrollViewer>("PreviewScroller")!.Focus();

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Shift);

            Assert.That(model.Selection.Selected, Is.EquivalentTo(session.Pages.Skip(1).Take(2).Select(page => page.Identity)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PreviewControlArrowKeepsTheExistingSelection()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 3);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            window.FindControl<ScrollViewer>("PreviewScroller")!.Focus();

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Control);

            Assert.That((model.Selection.Focus, model.Selection.Selected.Single()),
                Is.EqualTo((session.Pages[1].Identity, session.Pages[0].Identity)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void PinchingWithTwoTouchContactsZoomsWithoutChangingTheDocument()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1, detailed: true);
        model.SetSession(session);
        var before = session.Pages.ToArray();
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.Zoom.SetScale(0.5);
            window.UpdateLayout();
            var scroll = window.FindControl<ScrollViewer>("PreviewScroller")!;
            var center = scroll.TranslatePoint(new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2), window)!.Value;
            using var first = window.TouchBegin(center - new Point(40, 0));
            using var second = window.TouchBegin(center + new Point(40, 0));

            window.TouchMove(second, center + new Point(80, 0));
            window.TouchMove(second, center + new Point(120, 0));
            window.TouchEnd(second, center + new Point(120, 0));
            window.TouchEnd(first, center - new Point(40, 0));

            Assert.That((model.Zoom.Scale, session.Pages.ToArray()), Is.EqualTo((1.0, before)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void NewPinchStartsFromTheCurrentScale()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1, detailed: true);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.Zoom.SetScale(0.5);
            var scroll = window.FindControl<ScrollViewer>("PreviewScroller")!;
            scroll.RaiseEvent(new PinchEventArgs(2, default));
            scroll.RaiseEvent(new PinchEndedEventArgs());

            scroll.RaiseEvent(new PinchEventArgs(1.5, default));

            Assert.That(model.Zoom.Scale, Is.EqualTo(1.5));
        }
        finally { window.Close(); }
    }

    [TestCase(0.001, PreviewZoom.Minimum)]
    [TestCase(12, PreviewZoom.Maximum)]
    [TestCase(0.75, 0.75)]
    public void GestureZoomRespectsManualLimits(double scale, double expected)
    {
        var zoom = new PreviewZoom();

        zoom.SetScale(scale);

        Assert.That(zoom.Scale, Is.EqualTo(expected));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(0)]
    [TestCase(-1)]
    public void InvalidGestureScaleLeavesZoomUnchanged(double scale)
    {
        var zoom = new PreviewZoom();

        zoom.SetScale(scale);

        Assert.That((zoom.Scale, zoom.IsFit), Is.EqualTo((1.0, true)));
    }

    [AvaloniaTest]
    public void ThumbnailWheelScrollingWorksWithoutAVisibleScrollbar()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 12);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            var tray = window.FindControl<ListBox>("PageTray")!;
            var scroll = tray.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var point = tray.TranslatePoint(new Point(tray.Bounds.Width / 2, tray.Bounds.Height / 2), window)!.Value;

            window.MouseWheel(point, new Vector(0, -3));

            Assert.That(scroll.Offset.Y, Is.GreaterThan(0));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void OverflowingThumbnailsDoNotShowScrollbarChrome()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 12);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false) { Width = 760, Height = 680 };

        window.Show();

        try
        {
            var tray = window.FindControl<ListBox>("PageTray")!;
            Assert.That(tray.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Where(bar => bar.IsVisible), Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void KeyboardNavigationScrollsTheLastThumbnailIntoView()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 12);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            var tray = window.FindControl<ListBox>("PageTray")!;
            window.FindControl<ScrollViewer>("PreviewScroller")!.Focus();

            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            window.UpdateLayout();

            var last = tray.ContainerFromIndex(11)!;
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), tray)!.Value.Y;
            Assert.That(bottom, Is.InRange(last.Bounds.Height, tray.Bounds.Height));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ThumbnailHeaderUpdatesTheTotalAfterPageRemoval()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 3);
        model.SetSession(session);
        var window = new MainWindow(model, discover: false);
        window.Show();
        try
        {
            model.RemoveSelection();

            Assert.That(window.FindControl<TextBlock>("ThumbnailPageCount")!.Text, Is.EqualTo("2 pages"));
        }
        finally { window.Close(); }
    }

    private static Point PagePoint(MainWindow window, int index)
    {
        window.UpdateLayout();
        var tray = window.FindControl<ListBox>("PageTray")!;
        tray.ScrollIntoView(index);
        window.UpdateLayout();
        var item = tray.ContainerFromIndex(index)!;
        return item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
    }

    private static void ClickPage(MainWindow window, int index, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = PagePoint(window, index);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
    }
}
