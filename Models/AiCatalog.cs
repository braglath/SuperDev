using System.Collections.Generic;
using System.Linq;

namespace SuperDev.Models;

public static class AiCatalog
{
    public static IReadOnlyList<AiChoice<DevPlatform>> Platforms { get; } =
    [
        Choice(DevPlatform.CrossPlatform, "Cross-platform"),
        Choice(DevPlatform.Android, "Android"),
        Choice(DevPlatform.Ios, "iOS"),
        Choice(DevPlatform.Windows, "Windows"),
        Choice(DevPlatform.MacOs, "macOS"),
        Choice(DevPlatform.Web, "Web"),
        Choice(DevPlatform.Backend, "Backend / Server")
    ];

    public static IReadOnlyList<AiChoice<AiWorkKind>> WorkKinds { get; } =
    [
        Choice(AiWorkKind.BugFix, "Bug Fix"),
        Choice(AiWorkKind.NewFeature, "New Feature"),
        Choice(AiWorkKind.Development, "Development"),
        Choice(AiWorkKind.Testing, "Testing"),
        Choice(AiWorkKind.CodeReview, "Code Review / PR"),
        Choice(AiWorkKind.Performance, "Performance Optimization"),
        Choice(AiWorkKind.Refactoring, "Refactoring"),
        Choice(AiWorkKind.Release, "Release / Deployment"),
        Choice(AiWorkKind.SecurityReview, "Security Review")
    ];

    public static IReadOnlyList<AiChoice<DevFramework>> FrameworksFor(DevPlatform platform) => platform switch
    {
        DevPlatform.CrossPlatform =>
        [
            Framework(DevFramework.Maui),
            Framework(DevFramework.Flutter),
            Framework(DevFramework.ReactNative),
            Framework(DevFramework.Custom)
        ],
        DevPlatform.Android =>
        [
            Framework(DevFramework.Maui),
            Framework(DevFramework.Flutter),
            Framework(DevFramework.NativeAndroid),
            Framework(DevFramework.ReactNative),
            Framework(DevFramework.Custom)
        ],
        DevPlatform.Ios =>
        [
            Framework(DevFramework.Maui),
            Framework(DevFramework.Flutter),
            Framework(DevFramework.NativeIos),
            Framework(DevFramework.ReactNative),
            Framework(DevFramework.Custom)
        ],
        DevPlatform.Windows or DevPlatform.MacOs =>
        [
            Framework(DevFramework.Maui),
            Framework(DevFramework.Flutter),
            Framework(DevFramework.ReactNative),
            Framework(DevFramework.Custom)
        ],
        DevPlatform.Web =>
        [
            Framework(DevFramework.React),
            Framework(DevFramework.Angular),
            Framework(DevFramework.Custom)
        ],
        DevPlatform.Backend =>
        [
            Framework(DevFramework.AspNetCore),
            Framework(DevFramework.Node),
            Framework(DevFramework.Custom)
        ],
        _ => [Framework(DevFramework.Custom)]
    };

    public static string PlatformLabel(DevPlatform platform) =>
        Platforms.First(choice => choice.Value == platform).Label;

    public static string FrameworkLabel(DevFramework framework) => framework switch
    {
        DevFramework.Maui => ".NET MAUI",
        DevFramework.Flutter => "Flutter",
        DevFramework.NativeAndroid => "Native Android (Kotlin)",
        DevFramework.NativeIos => "Native iOS (Swift)",
        DevFramework.ReactNative => "React Native",
        DevFramework.React => "React",
        DevFramework.Angular => "Angular",
        DevFramework.AspNetCore => "ASP.NET Core",
        DevFramework.Node => "Node.js",
        _ => "Custom / Other"
    };

    public static string WorkLabel(AiWorkKind workKind) =>
        WorkKinds.First(choice => choice.Value == workKind).Label;

    private static AiChoice<DevFramework> Framework(DevFramework framework) =>
        Choice(framework, FrameworkLabel(framework));

    private static AiChoice<T> Choice<T>(T value, string label) where T : notnull => new(value, label);
}
