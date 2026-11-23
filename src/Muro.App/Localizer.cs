using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muro.Core;

namespace Muro.App;

/// Puts a page's on-screen words into the chosen language (see <see cref="Loc"/>). The XAML stays in English: this walks
/// the tree and swaps each string it has a translation for, remembering the English each element started with, so
/// switching language again, at any time, translates from the English and not from the last translation. Text the
/// code sets later is translated where it is set (<c>Loc.T</c>); a string with no translation is left as it is.
internal static class Localizer
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> Originals = new();

    /// Translates a page once it is on screen, and the cards of its lists as they are made (a list builds its items as
    /// they scroll into view, after the page has loaded).
    public static void Attach(Page page, Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        var hooked = false;
        page.Loaded += (_, _) =>
        {
            Apply(page);
            if (hooked) return;
            hooked = true;
            foreach (var list in Find<ListViewBase>(page))
                list.ContainerContentChanging += (_, args) =>
                {
                    if (!args.InRecycleQueue) queue.TryEnqueue(() => Apply(args.ItemContainer));   // after its template is built
                };
        };
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var inner in Find<T>(child)) yield return inner;
        }
    }

    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;
        Visit(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) Apply(VisualTreeHelper.GetChild(root, i));
    }

    private static void Visit(DependencyObject e)
    {
        switch (e)
        {
            case TextBlock t: Set(e, "Text", t.Text, v => t.Text = v); break;
            case TextBox box:
                Set(e, "Placeholder", box.PlaceholderText, v => box.PlaceholderText = v);
                if (box.Header is string boxHeader) Set(e, "Header", boxHeader, v => box.Header = v);
                break;
            case ComboBox combo:
                Set(e, "Placeholder", combo.PlaceholderText, v => combo.PlaceholderText = v);
                if (combo.Header is string comboHeader) Set(e, "Header", comboHeader, v => combo.Header = v);
                break;
            case ToggleSwitch toggle:
                if (toggle.Header is string toggleHeader) Set(e, "Header", toggleHeader, v => toggle.Header = v);
                if (toggle.OnContent is string on) Set(e, "On", on, v => toggle.OnContent = v);
                if (toggle.OffContent is string off) Set(e, "Off", off, v => toggle.OffContent = v);
                break;
            case RadioButtons radios:
                if (radios.Header is string radiosHeader) Set(e, "Header", radiosHeader, v => radios.Header = v);
                break;
            case ContentControl control:   // buttons, toggle buttons, radio buttons, navigation items
                if (control.Content is string content) Set(e, "Content", content, v => control.Content = v);
                break;
        }
        if (e is UIElement or FrameworkElement)
        {
            if (ToolTipService.GetToolTip(e) is string tip) Set(e, "Tip", tip, v => ToolTipService.SetToolTip(e, v));
            if (AutomationProperties.GetName(e) is { Length: > 0 } name) Set(e, "AutoName", name, v => AutomationProperties.SetName(e, v));
        }
    }

    private static void Set(DependencyObject owner, string property, string? current, Action<string> apply)
    {
        if (string.IsNullOrEmpty(current)) return;
        var map = Originals.GetOrCreateValue(owner);
        if (!map.TryGetValue(property, out var english) || (current != english && current != Loc.T(english)))
            map[property] = english = current;   // first sight, or the code put something new there
        var translated = Loc.T(english);
        if (translated != current) apply(translated);
    }
}
