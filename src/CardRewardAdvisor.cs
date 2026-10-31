using BuildAdvisor.Core;
using BuildAdvisor.UI;

namespace BuildAdvisor.Integration;

public sealed class CardRewardAdvisor
{
    private readonly RecommendationEngine engine;
    private readonly IAdvicePanel panel;

    public CardRewardAdvisor(RecommendationEngine engine, IAdvicePanel panel)
    {
        this.engine = engine;
        this.panel = panel;
    }

    public void OnCardRewardOpened(CurrentRunState state, IEnumerable<RunSnapshot> history) =>
        panel.Show(engine.Recommend(state, history));

    public void OnCardRewardClosed() => panel.Hide();
}
