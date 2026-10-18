namespace RaidReviewOverlay
{
    /// <summary>
    /// Duck-typed attribute bag read via reflection by BepInEx.ConfigurationManager
    /// (the in-game F12 settings menu). No reference to that assembly is needed;
    /// ConfigurationManager looks for fields with these exact names on any object
    /// passed through <see cref="BepInEx.Configuration.ConfigDescription"/>'s Tags.
    /// Lets the settings menu show a friendly name and ordering while the
    /// underlying .cfg key/section stays stable for existing config files.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public string DispName;
        public int? Order;
    }
}
