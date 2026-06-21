using Avalonia;
using Avalonia.Controls;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Mac.Chat.Plugin;
using Everywhere.Mac.Common;
using Everywhere.Mac.Interop;
using Everywhere.Mac.Mcp;
using Everywhere.Mcp;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using Everywhere.StrategyEngine;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Mac;

public static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        if (args.Contains("--mcp"))
        {
            await Everywhere.Mcp.Server.EverywhereMcpServer.RunStdioAsync(
                args,
                services => services
                    .AddSingleton<IVisualElementContext, VisualElementContext>()
                    .AddSingleton<IInputSimulator, MacInputSimulator>()
                    .AddSingleton<IFocusBackend, MacFocusBackend>()
                    .AddSingleton<IClipboardReader, MacClipboardReader>()
                    .AddSingleton<IIdleTimeReader, MacIdleTimeReader>()
                    .AddSingleton<IBrowserUrlReader, MacBrowserUrlReader>()
                    .AddSingleton<IAppleScriptRunner, MacAppleScriptRunner>()
                    .AddSingleton<IFinderReader, MacFinderReader>()
                    .AddSingleton<IBrowserTabsReader, MacBrowserTabsReader>()
                    .AddSingleton<IAppActivator, MacAppActivator>()
                    .AddSingleton<Everywhere.Interop.Whiteboard.IOcrEngine, MacVisionOcrEngine>());
            return;
        }

#if IsMacOS
        NativeMessageBox.MacOSMessageBoxHandler = MessageBoxHandler;
#endif

        await Entrance.InitializeAsync(args);

        ServiceLocator.Build(x => x

                #region Basic

                .AddApplicationLogging()
                .AddSingleton<IVisualElementContext, VisualElementContext>()
                .AddSingleton<IShortcutListener, CGEventShortcutListener>()
                .AddSingleton<INativeHelper, NativeHelper>()
                .AddSingleton<IWindowHelper, WindowHelper>()
                .AddSingleton<IPlatformUpdateHandler, MacUpdateHandler>()
                .AddSingleton<ISoftwareUpdater, SoftwareUpdater>()
                .AddSettings()
                .AddWatchdogManager()
                .ConfigureNetwork()
                .AddAvaloniaBasicServices()
                .AddViewsAndViewModels()
                .AddDatabaseAndStorage()
                .AddCloudClient()
                .AddChatEssentials()
                .AddSingleton<IInputSimulator, MacInputSimulator>()
                .AddSingleton<IFocusBackend, MacFocusBackend>()
                .AddSingleton<IClipboardReader, MacClipboardReader>()
                .AddSingleton<IIdleTimeReader, MacIdleTimeReader>()
                .AddSingleton<IBrowserUrlReader, MacBrowserUrlReader>()
                .AddSingleton<IAppleScriptRunner, MacAppleScriptRunner>()
                .AddSingleton<IFinderReader, MacFinderReader>()
                .AddSingleton<IBrowserTabsReader, MacBrowserTabsReader>()
                .AddSingleton<IAppActivator, MacAppActivator>()
                // Register Mac OCR BEFORE AddEverywhereMcp so the latter's
                // TryAddSingleton<NullOcrEngine> doesn't shadow it.
                .AddSingleton<Everywhere.Interop.Whiteboard.IOcrEngine, MacVisionOcrEngine>()
                .AddEverywhereMcp()

                #endregion

                #region Chat Plugins

                .AddTransient<BuiltInChatPlugin, SystemPlugin>()

                #endregion
                
                #region Strategy Engine

                .AddStrategyEngine()

                #endregion

                #region Initialize

                .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
                .AddTransient<IAsyncInitializer, UpdaterInitializer>()
                .AddTransient<IAsyncInitializer, EverywhereMcpInitializer>()
                .AddTransient<IAsyncInitializer, SnapshotContextHotkeyInitializer>()
                .AddTransient<IAsyncInitializer, ClearContextStashHotkeyInitializer>()
                .AddTransient<IAsyncInitializer, WhiteboardHotkeyInitializer>()
                .AddTransient<IAsyncInitializer>(sp => sp.GetRequiredService<Everywhere.Mcp.Snapshot.AutoCaptureService>())

            #endregion

        );

        NSApplication.CheckForIllegalCrossThreadCalls = false;
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new AppDelegate();

        BuildAvaloniaApp(ServiceLocator.Resolve<IServiceProvider>()).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static NativeMessageBoxResult MessageBoxHandler(string title, string message, NativeMessageBoxButtons buttons, NativeMessageBoxIcon icon)
    {
        using var alert = new NSAlert();
        alert.AlertStyle = icon switch
        {
            NativeMessageBoxIcon.Error or NativeMessageBoxIcon.Hand or NativeMessageBoxIcon.Stop => NSAlertStyle.Critical,
            NativeMessageBoxIcon.Warning => NSAlertStyle.Warning,
            _ => NSAlertStyle.Informational
        };
        alert.MessageText = title;
        alert.InformativeText = message;
        switch (buttons)
        {
            case NativeMessageBoxButtons.OkCancel:
            {
                alert.AddButton(CoreLocaleResolver.Common_OK);
                alert.AddButton(CoreLocaleResolver.Common_Cancel);
                break;
            }
            case NativeMessageBoxButtons.YesNo:
            {
                alert.AddButton(CoreLocaleResolver.Common_Yes);
                alert.AddButton(CoreLocaleResolver.Common_No);
                break;
            }
            case NativeMessageBoxButtons.YesNoCancel:
            {
                alert.AddButton(CoreLocaleResolver.Common_Yes);
                alert.AddButton(CoreLocaleResolver.Common_No);
                alert.AddButton(CoreLocaleResolver.Common_Cancel);
                break;
            }
            case NativeMessageBoxButtons.RetryCancel:
            {
                alert.AddButton(CoreLocaleResolver.Common_Retry);
                alert.AddButton(CoreLocaleResolver.Common_Cancel);
                break;
            }
            case NativeMessageBoxButtons.AbortRetryIgnore:
            {
                alert.AddButton(CoreLocaleResolver.Common_Abort);
                alert.AddButton(CoreLocaleResolver.Common_Retry);
                alert.AddButton(CoreLocaleResolver.Common_Ignore);
                break;
            }
            default:
            {
                alert.AddButton(CoreLocaleResolver.Common_OK);
                break;
            }
        }
        var result = (NSAlertButtonReturn)alert.RunModal();
        return result switch
        {
            NSAlertButtonReturn.First => buttons switch
            {
                NativeMessageBoxButtons.Ok => NativeMessageBoxResult.Ok,
                NativeMessageBoxButtons.OkCancel => NativeMessageBoxResult.Ok,
                NativeMessageBoxButtons.YesNo => NativeMessageBoxResult.Yes,
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.Yes,
                NativeMessageBoxButtons.RetryCancel => NativeMessageBoxResult.Retry,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Cancel,
                _ => NativeMessageBoxResult.None
            },
            NSAlertButtonReturn.Second => buttons switch
            {
                NativeMessageBoxButtons.OkCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.YesNo => NativeMessageBoxResult.No,
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.No,
                NativeMessageBoxButtons.RetryCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Retry,
                _ => NativeMessageBoxResult.None
            },
            NSAlertButtonReturn.Third => buttons switch
            {
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Ignore,
                _ => NativeMessageBoxResult.None
            },
            _ => NativeMessageBoxResult.None
        };
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider serviceProvider) =>
        AppBuilder.Configure(() => new App(serviceProvider))
            .UsePlatformDetect()
            .With(
                new AvaloniaNativePlatformOptions
                {
                    AppSandboxEnabled = false
                })
            .With(
                new MacOSPlatformOptions
                {
                    // These settings are important for showing chat window over other fullscreen apps
                    ShowInDock = false,
                    DisableAvaloniaAppDelegate = true
                })
            .WithInterFont()
            .LogToTrace();
}
