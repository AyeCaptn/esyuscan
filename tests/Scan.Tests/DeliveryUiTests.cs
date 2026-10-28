using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using NUnit.Framework;

namespace Scan.Tests;

[TestFixture]
public sealed class DeliveryUiTests : TestFiles
{
    [AvaloniaTest]
    public void DeliveryButtonsFollowSessionBusyAndDuplexGuardsAndFitNarrowWindows()
    {
        var model = new MainViewModel();
        var session = CreateSession();
        AddPages(session, 1);
        model.SetSession(session);
        var window = new MainWindow(model, false, new FakeDesktopCommand()) { Width = 760, Height = 680 };
        window.Show();
        try
        {
            var print = window.FindControl<Button>("PrintButton")!;
            var email = window.FindControl<Button>("EmailButton")!;
            Assert.That(print.IsEffectivelyEnabled && email.IsEffectivelyEnabled, Is.True);
            foreach (var name in new[] { "PrintButton", "EmailButton", "CancelPrintButton", "CombinedExportButton" })
            {
                var control = window.FindControl<Button>(name)!;
                var corner = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
                Assert.That(corner.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
                Assert.That(corner.Y, Is.LessThanOrEqualTo(window.ClientSize.Height));
            }
            model.Busy = true;
            Assert.That(print.IsEffectivelyEnabled || email.IsEffectivelyEnabled, Is.False);
            model.Busy = false;
            session.BeginManualDuplex();
            model.RefreshDuplex();
            Assert.That(print.IsEffectivelyEnabled || email.IsEffectivelyEnabled, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task PrintDialogReturnsChosenBasicOptionsAndCancelDoesNotSubmit()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new PrintWindow(["Office", "Studio"]);
        var task = dialog.ShowDialog<PrintOptions?>(owner);
        dialog.FindControl<ComboBox>("PrintPrinterPicker")!.SelectedIndex = 1;
        dialog.FindControl<NumericUpDown>("PrintCopies")!.Value = 3;
        dialog.FindControl<ComboBox>("PrintSidesPicker")!.SelectedIndex = 2;
        dialog.FindControl<ComboBox>("PrintMediaPicker")!.SelectedIndex = 1;
        dialog.FindControl<CheckBox>("PrintFitCheck")!.IsChecked = false;
        dialog.FindControl<Button>("SubmitPrintButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(await task, Is.EqualTo(new PrintOptions("Studio", 3, PrintSides.ShortEdge, "A4", false)));
        var cancelled = new PrintWindow(["Office"]);
        var cancelTask = cancelled.ShowDialog<PrintOptions?>(owner);
        cancelled.Close();
        Assert.That(await cancelTask, Is.Null);
        owner.Close();
    }

    [AvaloniaTest]
    public void EmptyPrinterDialogCannotSubmit()
    {
        var dialog = new PrintWindow([]);
        dialog.Show();
        try { Assert.That(dialog.FindControl<Button>("SubmitPrintButton")!.IsEnabled, Is.False); }
        finally { dialog.Close(); }
    }
}
