// rst-c's own (not vendored). SC-045 (issue #163): the launchers' safety net. A window that throws on
// its UI thread while open through WindowGuard is logged at Error and closed, and the exception does not
// escape to the caller (in Revit: the command, then Revit). Outside the window's lifetime, for an
// exception someone else already handled, and for another add-in's exception, the net stays out of the way.

using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RST.Core.Logging;
using RST.RuntimeTests.Builder;
using RST.RuntimeTests.Support;
using RST.UI.Builder;
using RST.UI.Dialogs;
using Xunit;

namespace RST.RuntimeTests.Controls;

// The theme collection runs alone, so the process-wide Log.Logger swap below sees only these tests.
[Collection(WpfThemeCollection.Name)]
public class WindowGuardTests
{
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(15);

    private static void Sta(Action action) => Screenshots.RunSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            action();
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static List<LogEvent> CaptureLog(Action action)
    {
        var events = new List<LogEvent>();
        var previous = Log.Logger;
        Log.Logger = new Logger(LogLevel.Verbose, new ListSink(events));
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
        }
        return events;
    }

    /// <summary>
    /// Puts <paramref name="window"/> off-screen and arms a failure backstop: once the window is shown
    /// (SourceInitialized, which comes even when its first layout throws), it is hidden after
    /// <see cref="Backstop"/> if still up, which ends ShowDialog, so a broken net fails the test rather than
    /// hangs it. No test relies on the backstop to pass; each records whether it fired.
    /// </summary>
    private static T OffScreen<T>(T window, Action onBackstop) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        DispatcherTimer? timer = null;
        window.SourceInitialized += (_, _) =>
        {
            timer = new DispatcherTimer(Backstop, DispatcherPriority.Normal, (_, _) =>
            {
                timer!.Stop();
                onBackstop();
                if (window.IsVisible) window.Hide();
            }, window.Dispatcher);
            timer.Start();
        };
        window.Closed += (_, _) => timer?.Stop();
        return window;
    }

    private static Window Small(Action onBackstop) => OffScreen(new Window { Width = 120, Height = 80 }, onBackstop);

    private static List<LogEvent> Faults(List<LogEvent> events) =>
        events.Where(e => e.MessageTemplate == WindowGuard.FaultTemplate).ToList();

    /// <summary>Pumps the dispatcher for <paramref name="time"/> (layout, render and timers run).</summary>
    private static void PumpFor(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [Fact]
    public void LoadedThrows_IsLoggedAtError_TheWindowCloses_AndNothingEscapes() => Sta(() =>
    {
        var boom = new InvalidOperationException("SC-045 test: Loaded threw");
        var backstopFired = false;
        var closed = false;
        var result = true;
        Exception? escaped = null;

        var events = CaptureLog(() =>
        {
            var window = Small(() => backstopFired = true);
            window.Loaded += (_, _) => throw boom;
            window.Closed += (_, _) => closed = true;
            escaped = Record.Exception(() => result = WindowGuard.ShowDialog(window, "Test"));
        });

        Assert.Null(escaped);
        Assert.False(result);
        Assert.True(closed);
        Assert.False(backstopFired, "the window went only on the test's backstop, not on the safety net");
        var fault = Assert.Single(Faults(events));
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Same(boom, fault.Exception);
        Assert.Equal("Test", fault.PropertyValues[0]);
        Assert.Equal(WindowGuard.RstFrame, fault.PropertyValues[1]); // the throwing handler is in an RST assembly
    });

    [Fact]
    public void AWindowThatRefusesToClose_StillClosesAfterAFault() => Sta(() =>
    {
        var backstopFired = false;
        var result = true;
        var closed = false;
        var events = CaptureLog(() =>
        {
            var window = Small(() => backstopFired = true);
            window.Closing += (_, e) => e.Cancel = true; // refuses every close, as the Loader does while it works
            window.Loaded += (_, _) => throw new InvalidOperationException("SC-045 test");
            window.Closed += (_, _) => closed = true;
            result = WindowGuard.ShowDialog(window, "Test");
        });

        Assert.False(result);
        Assert.True(closed, "the guard's close was refused");
        Assert.False(backstopFired);
        Assert.Single(Faults(events));
    });

    [Fact]
    public void ANormalClose_ReturnsTrue_AndLogsNothing() => Sta(() =>
    {
        var result = false;
        var events = CaptureLog(() =>
        {
            var window = Small(() => { });
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, window.Close);
            result = WindowGuard.ShowDialog(window, "Test");
        });

        Assert.True(result);
        Assert.DoesNotContain(events, e => e.Level >= LogLevel.Error);
    });

    [Fact]
    public void AnExceptionAlreadyHandled_IsLeftAlone() => Sta(() =>
    {
        var earlier = 0;
        var result = false;
        var backstopFired = false;
        var events = CaptureLog(() =>
        {
            var window = Small(() => backstopFired = true);
            // Subscribed first, as Revit or another add-in would be: it handles the exception itself, then the
            // test closes the window normally.
            void Earlier(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                earlier++;
                e.Handled = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, window.Close);
            }
            Dispatcher.CurrentDispatcher.UnhandledException += Earlier;
            try
            {
                window.Loaded += (_, _) => throw new InvalidOperationException("SC-045 test");
                result = WindowGuard.ShowDialog(window, "Test");
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= Earlier;
            }
        });

        Assert.Equal(1, earlier);
        Assert.True(result, "the net claimed an exception someone else had handled");
        Assert.False(backstopFired);
        Assert.Empty(Faults(events));
    });

    [Fact]
    public void AfterTheWindowCloses_TheNetIsGone() => Sta(() =>
    {
        bool? handledWhenSeen = null;
        var events = CaptureLog(() =>
        {
            var window = Small(() => { });
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, window.Close);
            WindowGuard.ShowDialog(window, "Test");

            // Subscribed after the net: had the net still been subscribed, it would have run first and handled it.
            void Later(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                handledWhenSeen = e.Handled;
                e.Handled = true;
            }
            Dispatcher.CurrentDispatcher.UnhandledException += Later;
            try
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => throw new InvalidOperationException("SC-045 test: after close")));
                WindowCapture.DoEvents();
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= Later;
            }
        });

        Assert.False(handledWhenSeen);
        Assert.Empty(Faults(events));
    });

    [Fact]
    public void RepeatsWhileClosing_AreHandled_AndCountedInOneLine() => Sta(() =>
    {
        var boom = new InvalidOperationException("SC-045 test: first");
        var result = true;
        Exception? escaped = null;
        var events = CaptureLog(() =>
        {
            var window = Small(() => { });
            window.Loaded += (_, _) =>
            {
                // Queued ahead of the net's close (same priority, first in).
                for (var i = 0; i < 3; i++)
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => throw new InvalidOperationException("SC-045 test: repeat")));
                throw boom;
            };
            escaped = Record.Exception(() => result = WindowGuard.ShowDialog(window, "Test"));
        });

        Assert.Null(escaped);
        Assert.False(result);
        var fault = Assert.Single(Faults(events));
        Assert.Same(boom, fault.Exception);
        var repeats = Assert.Single(events, e => e.MessageTemplate == WindowGuard.RepeatsTemplate);
        Assert.Equal(LogLevel.Error, repeats.Level);
        Assert.Equal(3, repeats.PropertyValues[1]);
        Assert.Equal(2, events.Count(e => e.Level >= LogLevel.Error)); // the first in full, the repeats as a count
    });

    /// <summary>A panel whose layout always fails: WPF marks the tree dirty and tries again on every pass.</summary>
    private sealed class AlwaysThrowsPanel : Panel
    {
        public int Measures { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            throw new InvalidOperationException("SC-045 test: layout always fails");
        }
    }

    [Fact]
    public void ARecurringLayoutFault_DoesNotSpin_TheWindowCloses_AndNothingOutlivesIt() => Sta(() =>
    {
        var panel = new AlwaysThrowsPanel();
        var backstopFired = false;
        var closed = false;
        var result = true;
        Exception? escaped = null;
        var afterClose = new List<Exception>();
        var measuresAtReturn = 0;

        var events = CaptureLog(() =>
        {
            var window = Small(() => backstopFired = true);
            window.Content = panel;
            window.Closed += (_, _) => closed = true;
            escaped = Record.Exception(() => result = WindowGuard.ShowDialog(window, "Test"));
            measuresAtReturn = panel.Measures;

            // The net is gone now: anything the faulted tree still raises would reach Revit unhandled.
            void Later(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                afterClose.Add(e.Exception);
                e.Handled = true; // keep the test host alive; the assertion below reports it
            }
            Dispatcher.CurrentDispatcher.UnhandledException += Later;
            try
            {
                PumpFor(TimeSpan.FromMilliseconds(600));
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= Later;
            }
        });

        Assert.Null(escaped);
        Assert.False(result);
        Assert.True(closed);
        Assert.False(backstopFired, "the window went only on the test's backstop: the layout fault kept it up");
        Assert.InRange(measuresAtReturn, 1, 10); // closed after the first failure, not spinning on it
        Assert.Equal(measuresAtReturn, panel.Measures);
        Assert.Empty(afterClose);
        Assert.Single(Faults(events));
    });

    [Fact]
    public void AnotherAddInsException_IsLeftAlone() => Sta(() =>
    {
        bool? handledWhenSeen = null;
        Exception? seen = null;
        var result = false;
        var backstopFired = false;
        var events = CaptureLog(() =>
        {
            var window = Small(() => backstopFired = true);
            // Subscribed after the net, so it sees whether the net took the exception; then the test closes the window.
            void Later(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                handledWhenSeen ??= e.Handled;
                seen ??= e.Exception;
                e.Handled = true; // keep the test host alive
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, window.Close);
            }
            window.Loaded += (_, _) =>
            {
                Dispatcher.CurrentDispatcher.UnhandledException += Later;
                // Posted straight to the add-in's method, so no RST frame sits between it and the dispatcher.
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, ForeignAddIn.Run(ForeignAddIn.SampleName));
            };
            try
            {
                result = WindowGuard.ShowDialog(window, "Test");
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= Later;
            }
        });

        Assert.NotNull(seen);
        Assert.Equal(ForeignAddIn.SampleName, new System.Diagnostics.StackTrace(seen!, false).GetFrame(0)!.GetMethod()!.DeclaringType!.Assembly.GetName().Name);
        Assert.False(handledWhenSeen);
        Assert.True(result, "the net closed the window over another add-in's exception");
        Assert.False(backstopFired);
        Assert.Empty(Faults(events));
    });

    [Fact]
    public void AFrameworkOnlyException_TheShapeOf163_IsHandled() => Sta(() =>
    {
        // The pre-fix BusySpinner trigger, on a collapsed (never templated) control: WPF throws from its
        // Loaded broadcast with no RST frame on the stack, exactly as Health did in Revit.
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
              <Rectangle x:Name="Arc" Width="4" Height="4">
                <Rectangle.RenderTransform><RotateTransform /></Rectangle.RenderTransform>
              </Rectangle>
              <ControlTemplate.Triggers>
                <EventTrigger RoutedEvent="FrameworkElement.Loaded">
                  <BeginStoryboard>
                    <Storyboard RepeatBehavior="Forever">
                      <DoubleAnimation Storyboard.TargetName="Arc" Storyboard.TargetProperty="(UIElement.RenderTransform).(RotateTransform.Angle)"
                                       From="0" To="360" Duration="0:0:0.9" />
                    </Storyboard>
                  </BeginStoryboard>
                </EventTrigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        var backstopFired = false;
        var result = true;
        Exception? escaped = null;
        var events = CaptureLog(() =>
        {
            var window = Small(() => backstopFired = true);
            window.Content = new ProgressBar
            {
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml),
                Visibility = Visibility.Collapsed,
            };
            escaped = Record.Exception(() => result = WindowGuard.ShowDialog(window, "Test"));
        });

        Assert.Null(escaped);
        Assert.False(result);
        Assert.False(backstopFired);
        var fault = Assert.Single(Faults(events));
        Assert.IsType<InvalidOperationException>(fault.Exception);
        Assert.Contains("'Arc' name cannot be found", fault.Exception!.Message);
        Assert.DoesNotContain("   at RST.", fault.Exception.StackTrace ?? "");
        Assert.Equal(WindowGuard.FrameworkOnly, fault.PropertyValues[1]);
    });

    [Fact]
    public void TheOwnershipRule_ReadsTheFramesByAssembly()
    {
        Assert.Equal(WindowGuard.FaultOwner.Rst, WindowGuard.Classify(Thrown(() => throw new InvalidOperationException("RST frame"))));
        Assert.Equal(WindowGuard.FaultOwner.Foreign, WindowGuard.Classify(ForeignAddIn.Capture(ForeignAddIn.SampleName, null)));
        // A System.*-named library an add-in ships is not the shared framework: it is judged by where it loaded from.
        Assert.Equal(WindowGuard.FaultOwner.Foreign, WindowGuard.Classify(ForeignAddIn.Capture("System.Reactive", null)));
        // Someone else's exception wrapping one of RST's is still RST's.
        Assert.Equal(WindowGuard.FaultOwner.Rst,
            WindowGuard.Classify(ForeignAddIn.Capture(ForeignAddIn.SampleName, Thrown(() => throw new FormatException()))));
        // Never thrown: no frames at all, nothing points elsewhere.
        Assert.Equal(WindowGuard.FaultOwner.FrameworkOnly, WindowGuard.Classify(new InvalidOperationException("no stack")));
    }

    [Fact]
    public void ABuilderWithUnsavedChanges_ClosesAfterAFault_WithoutThePrompt_AndDiscardsTheEdits() => Sta(() =>
    {
        var h = BuilderHarness.Create(deferPosts: true);
        h.Model.ProfileName = "Draft"; // unsaved
        Assert.True(h.Model.IsDirty);
        var backstopFired = false;
        var closed = false;
        var result = true;
        var events = CaptureLog(() =>
        {
            var window = OffScreen(new ProfileBuilderWindow(h.Model), () => backstopFired = true);
            window.Loaded += (_, _) => throw new InvalidOperationException("SC-045 test: Builder fault");
            window.Closed += (_, _) => closed = true;
            result = WindowGuard.ShowDialog(window, "Builder");
        });

        Assert.False(result);
        Assert.True(closed);
        Assert.False(backstopFired, "the Builder went only on the test's backstop");
        Assert.Empty(h.Dialogs.ChooseCalls); // no Save & Exit / Discard & Exit / Cancel prompt
        Assert.Empty(h.Backend.Saved);
        h.RunPosted(); // whatever the view-model queued: a "Save & Exit" would save here, after the window is gone
        Assert.Empty(h.Backend.Saved);
        Assert.Contains(events, e => e.Level == LogLevel.Warning && e.MessageTemplate.Contains("unsaved changes were discarded"));
        Assert.Single(Faults(events));
    });

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException("did not throw");
    }

    /// <summary>
    /// Stands in for another add-in: a type emitted into its own dynamic assembly (not an RST assembly, not the
    /// shared framework), whose methods throw, and catch, in its own code.
    /// </summary>
    private static class ForeignAddIn
    {
        public const string SampleName = "ThirdParty.SampleAddIn";

        private static readonly Dictionary<string, Type> Types = [];

        public static Action Run(string assemblyName) =>
            (Action)Delegate.CreateDelegate(typeof(Action), Build(assemblyName).GetMethod("Run")!);

        public static Exception Capture(string assemblyName, Exception? inner) =>
            (Exception)Build(assemblyName).GetMethod("Capture")!.Invoke(null, [inner])!;

        private static Type Build(string assemblyName)
        {
            lock (Types)
            {
                if (Types.TryGetValue(assemblyName, out var built)) return built;
                var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
                var module = assembly.DefineDynamicModule(assemblyName);
                var type = module.DefineType(assemblyName + ".IdleWork", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var message = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
                var messageInner = typeof(InvalidOperationException).GetConstructor([typeof(string), typeof(Exception)])!;

                var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
                run.Emit(OpCodes.Ldstr, "another add-in's work failed");
                run.Emit(OpCodes.Newobj, message);
                run.Emit(OpCodes.Throw);

                var capture = type.DefineMethod("Capture", MethodAttributes.Public | MethodAttributes.Static, typeof(Exception), [typeof(Exception)]).GetILGenerator();
                var caught = capture.DeclareLocal(typeof(Exception));
                capture.BeginExceptionBlock();
                capture.Emit(OpCodes.Ldstr, "another add-in's work failed");
                capture.Emit(OpCodes.Ldarg_0);
                capture.Emit(OpCodes.Newobj, messageInner);
                capture.Emit(OpCodes.Throw);
                capture.BeginCatchBlock(typeof(Exception));
                capture.Emit(OpCodes.Stloc, caught);
                capture.EndExceptionBlock();
                capture.Emit(OpCodes.Ldloc, caught);
                capture.Emit(OpCodes.Ret);

                built = type.CreateType();
                Types[assemblyName] = built;
                return built;
            }
        }
    }

    private sealed class ListSink(List<LogEvent> events) : ILogSink
    {
        public void Emit(LogEvent logEvent, IReadOnlyDictionary<string, object?> properties)
        {
            lock (events) events.Add(logEvent);
        }

        public void Dispose()
        {
        }
    }
}
