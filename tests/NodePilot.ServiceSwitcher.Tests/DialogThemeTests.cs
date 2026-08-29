using System.Runtime.ExceptionServices;
using System.Windows.Media;
using FluentAssertions;
using NodePilot.ServiceSwitcher.Localization;
using NodePilot.ServiceSwitcher.Models;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class DialogThemeTests
{
    [Fact]
    public void CustomDialogsBindTheirWindowSurfaceAndTextToTheActiveTheme()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var surface = new SolidColorBrush(Color.FromRgb(11, 20, 34));
                var text = new SolidColorBrush(Color.FromRgb(234, 241, 255));
                var strings = new StringCatalog();
                var confirmation = new SwitchConfirmationWindow(
                    strings,
                    SwitchTarget.NodePilot,
                    ["omanagement"],
                    ["NodePilot"]);
                var login = new NodePilotLoginWindow(strings, "service-switcher", null);

                AssertThemeBindings(confirmation, surface, text);
                AssertThemeBindings(login, surface, text);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AssertThemeBindings(
        System.Windows.Window dialog,
        SolidColorBrush surface,
        SolidColorBrush text)
    {
        dialog.Resources["PageBrush"] = surface;
        dialog.Resources["TextBrush"] = text;

        dialog.Background.Should().BeSameAs(surface);
        dialog.Foreground.Should().BeSameAs(text);
    }
}
