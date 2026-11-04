using Avalonia.Media;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

internal static class PriorityAppearance
{
    public static readonly IBrush Neutral = AppearanceService.Brush("TextPrimaryBrush");
    public static readonly IBrush Critical = AppearanceService.Brush("DangerTextBrush");
    public static readonly IBrush Required = AppearanceService.Brush("WarningTextBrush");
    public static readonly IBrush Conditional = AppearanceService.Brush("InfoTextBrush");
    public static readonly IBrush Review = AppearanceService.Brush("ReviewTextBrush");

    public static string ClassName(ChecklistPriority priority) => priority switch
    {
        ChecklistPriority.Critical => "critical",
        ChecklistPriority.Required => "required",
        ChecklistPriority.Conditional => "conditional",
        _ => "review"
    };

    public static IBrush BrushFor(ChecklistPriority priority) => priority switch
    {
        ChecklistPriority.Critical => Critical,
        ChecklistPriority.Required => Required,
        ChecklistPriority.Conditional => Conditional,
        _ => Review
    };
}
