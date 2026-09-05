using RainWorldCompanion.Core.System;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.App.Tests;

// Starting the game is the one thing these cannot do, so what is pinned here is that a start the
// Core layer refused never reaches Process.Start, and that the invite link is handed to Steam whole.
public class JoinLobbyTests
{
    private const string LobbyId = "109775241234567890";

    [Fact]
    public void A_refused_start_reports_and_launches_nothing()
    {
        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.WithPassword("hunter2").Start(null);
        string? reported = null;

        bool ran = MeadowLauncher.Start(start, problem => reported = problem);

        Assert.False(ran);
        Assert.Equal(start.Problem, reported);
    }

    [Fact]
    public void An_invite_link_is_handed_to_Steam_unchanged()
    {
        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.Start(null);

        Assert.Equal(start.SteamUrl, WorkshopLink.UrlForSteam(start.SteamUrl));
    }

    [Fact]
    public void A_start_that_can_run_says_which_way_it_goes()
    {
        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.Start(null);

        Assert.True(start.CanRun);
        Assert.Contains("through Steam", start.Headline);
    }
}
