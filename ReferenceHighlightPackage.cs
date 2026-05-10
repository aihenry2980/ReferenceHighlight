using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;

namespace ReferenceHighlight;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(ReferenceHighlightOptionsPage), "Reference Highlight", "Colors", 0, 0, true)]
public sealed class ReferenceHighlightPackage : AsyncPackage
{
    public const string PackageGuidString = "9d65f6d2-6b6a-4ea2-8e65-61c503371390";

    internal static ReferenceHighlightOptionsPage? OptionsPage { get; private set; }

    private DispatcherTimer? timer;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        OptionsPage = (ReferenceHighlightOptionsPage)GetDialogPage(typeof(ReferenceHighlightOptionsPage));

        Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTimerTick, dispatcher);
        timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && timer is not null)
        {
            timer.Stop();
            timer.Tick -= OnTimerTick;
            timer = null;
        }

        base.Dispose(disposing);
    }

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            if (window.Content is DependencyObject content && LooksLikeFindAllReferencesWindow(content))
            {
                FindAllReferencesVisualStyler.ApplyToRoot(window);
            }
        }
    }

    private static bool LooksLikeFindAllReferencesWindow(DependencyObject root)
    {
        bool hasReferencesTitle = false;
        bool hasReadWriteColumn = false;

        foreach (TextBlock textBlock in EnumerateVisualDescendants<TextBlock>(root))
        {
            string text = textBlock.Text?.Trim() ?? string.Empty;
            if (text.IndexOf(" references - ", StringComparison.OrdinalIgnoreCase) >= 0
                || text.Equals("Read/Write", StringComparison.OrdinalIgnoreCase))
            {
                hasReferencesTitle = true;
            }

            if (text.Equals("Read", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Write", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Read/Write", StringComparison.OrdinalIgnoreCase))
            {
                hasReadWriteColumn = true;
            }

            if (hasReferencesTitle && hasReadWriteColumn)
            {
                return true;
            }
        }

        return false;
    }

    private static System.Collections.Generic.IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
