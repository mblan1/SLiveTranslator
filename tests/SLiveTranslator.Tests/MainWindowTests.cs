using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SLiveTranslator.UI;
using Xunit;
using NumberBox = Wpf.Ui.Controls.NumberBox;

namespace SLiveTranslator.Tests;

/// <summary>What the main window does around its pages, with WPF on a thread of its own.</summary>
public class MainWindowTests
{
    static void OnStaThread(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void A_number_typed_but_not_entered_is_taken_before_the_window_asks_about_unsaved_edits()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        OnStaThread(() =>
        {
            var vm = new SettingsViewModel(host, new FakeScheduler().Delay);
            var box = new NumberBox { Minimum = 10, Maximum = 80, MaxDecimalPlaces = 0 };
            box.SetBinding(NumberBox.ValueProperty, new Binding(nameof(vm.Overlay.FontSize)) { Source = vm.Overlay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            var window = new Window { Content = new StackPanel { Children = { box, new TextBox() } }, ShowActivated = false, ShowInTaskbar = false, Left = -2000, Top = -2000 };
            try
            {
                window.Show();
                FocusManager.SetFocusedElement(window, box);
                box.Text = "30"; // typed: the box keeps it to itself until Enter or leaving it
                Assert.Equal(SLiveTranslator.Models.OverlaySettings.DefaultFontSize, vm.Working.Overlay.FontSize);
                Assert.False(vm.HasChanges);

                MainWindow.CommitTyping(window); // Alt+F4 or the title bar's close button moves no focus

                Assert.Equal(30, vm.Working.Overlay.FontSize);
                Assert.True(vm.HasChanges);
                Assert.Null(FocusManager.GetFocusedElement(window));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
