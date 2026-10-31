using BuildAdvisor.Core;

namespace BuildAdvisor.UI;

/// <summary>Game-specific code renders this in the card reward screen; core logic stays testable.</summary>
public interface IAdvicePanel
{
    void Show(IReadOnlyList<CardAdvice> advice);
    void Hide();
}
