using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Host.Desktop.Models;

namespace OwnerAI.Host.Desktop.Views;

public sealed class SkillTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? CardTemplate { get; set; }
    public DataTemplate? GapTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item switch
        {
            SkillCategoryHeader => HeaderTemplate!,
            SkillItem => CardTemplate!,
            EvolutionGapItem => GapTemplate!,
            _ => base.SelectTemplateCore(item),
        };
    }
}
