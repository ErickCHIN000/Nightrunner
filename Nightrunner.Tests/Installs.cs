using Nightrunner.Core.Games;

namespace Nightrunner.Tests;

/// <summary>
/// Install-backed tests call <see cref="Require"/> first: it returns the detected install
/// (<c>NIGHTRUNNER_GAME_ROOT</c>, then Steam) or skips the test cleanly when there is none.
/// <c>NIGHTRUNNER_TESTS_NO_INSTALL=1</c> makes every install look absent, to exercise that path on a machine that has one.
/// </summary>
public static class Installs
{
    private static readonly Lazy<Dictionary<string, GameInstall>> Found = new(() => GameInstall.FindInstalls());

    public static GameInstall Require(string gameId)
    {
        GameInstall? gi = null;
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_TESTS_NO_INSTALL") == "1" || !Found.Value.TryGetValue(gameId, out gi))
            Assert.Skip($"no {gameId} install detected");
        return gi!;
    }
}
