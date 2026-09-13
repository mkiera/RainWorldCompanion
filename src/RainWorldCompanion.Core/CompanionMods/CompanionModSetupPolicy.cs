namespace RainWorldCompanion.Core.CompanionMods;

public enum CompanionModSetupAction { None, InstallOrRepair, Remove }

public static class CompanionModSetupPolicy
{
    public static CompanionModSetupAction Decide(bool automaticSetup, CompanionModStatus status)
    {
        if (!automaticSetup)
            return status.Installed || status.Enabled ? CompanionModSetupAction.Remove : CompanionModSetupAction.None;
        if (!status.Installed || status.Enabled && !status.Compatible)
            return CompanionModSetupAction.InstallOrRepair;
        return CompanionModSetupAction.None;
    }
}
