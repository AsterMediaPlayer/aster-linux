using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Aster.Desktop.ViewModels;

namespace Aster.Desktop;

/// <summary>
/// Given a view model, returns its view: FooViewModel's is FooView. Each
/// view model keeps its view, so a page gone back to is as it was left,
/// scrolled where it was.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    private static readonly ConditionalWeakTable<object, Control> views = new();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        if (views.TryGetValue(param, out var kept) && kept.Parent is null)
            return kept;

        // A page without a view of its own uses its base page's, as the
        // browse pages all use BrowsePageView.
        string name = "";
        Type? type = null;
        for (var model = param.GetType(); type is null && model is not null && model != typeof(ViewModelBase); model = model.BaseType)
        {
            name = model.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            type = Type.GetType(name);
        }

        if (type != null)
        {
            var view = (Control)Activator.CreateInstance(type)!;
            views.AddOrUpdate(param, view);
            return view;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
