using LinguaCue.Models;

namespace LinguaCue.ViewModels;

public sealed class ComponentStatusViewModel(ComponentStatus status)
{
    public string Name { get; } = status.Name;

    public bool IsReady { get; } = status.IsReady;

    public string StateText { get; } = status.IsReady ? "就绪" : "缺失";

    public string Detail { get; } = status.Path ?? status.Detail;
}

