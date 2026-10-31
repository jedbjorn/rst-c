// rst-c's own (not vendored): the Commands pane's tree holds two kinds of item (feature #18, spec #19,
// WU4): the Custom Tools group first, then the catalogue's nodes, so both scroll as one region
// (decision #25) while the catalogue keeps its virtualizing tree (spec #16, AC7). These pick each
// item's template and container style; a catalogue node's children inherit them.

using System.Windows;
using System.Windows.Controls;

namespace RST.UI.Builder;

/// <summary>The Custom Tools group's template, or the catalogue node's.</summary>
public sealed class CatalogItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Catalog { get; set; }

    public DataTemplate? CustomTools { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is CustomToolsGroupViewModel ? CustomTools : Catalog;
}

/// <summary>The Custom Tools group's container style (it draws itself), or the catalogue's tree row.</summary>
public sealed class CatalogItemStyleSelector : StyleSelector
{
    public Style? Catalog { get; set; }

    public Style? CustomTools { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is CustomToolsGroupViewModel ? CustomTools : Catalog;
}
