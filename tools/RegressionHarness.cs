using BuildAdvisor.Integration;

internal static class RegressionHarness
{
    public static int Main()
    {
        var passed = BuildAdvisorRegression.Run();
        Console.WriteLine(passed ? "Regression=True" : "Regression=False: " + BuildAdvisorRegression.LastError);
        return passed ? 0 : 1;
    }
}
