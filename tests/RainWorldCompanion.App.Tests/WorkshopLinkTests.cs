using RainWorldCompanion.Views;

namespace RainWorldCompanion.App.Tests;

public class WorkshopLinkTests
{
    [Fact]
    public void A_Steam_launch_url_is_not_wrapped_as_a_web_page()
    {
        Assert.Equal(
            "steam://rungameid/312520",
            WorkshopLink.UrlForSteam("steam://rungameid/312520"));
    }

    [Fact]
    public void A_workshop_page_still_opens_in_Steam()
    {
        Assert.Equal(
            "steam://openurl/https://steamcommunity.com/sharedfiles/filedetails/?id=1",
            WorkshopLink.UrlForSteam("https://steamcommunity.com/sharedfiles/filedetails/?id=1"));
    }
}
