namespace SteamAuth
{
    public static class APIEndpoints
    {
        public const string STEAMAPI_BASE = "https://api.steampowered.com";
        public const string COMMUNITY_BASE = "https://steamcommunity.com";
        private const string mobileauth_base = STEAMAPI_BASE + "/IMobileAuthService/%s/v0001";
        public static string MOBILEAUTH_GETWGTOKEN() => mobileauth_base.Replace("%s", "GetWGToken");
        private const string two_factor_base = STEAMAPI_BASE + "/ITwoFactorService/%s/v0001";
        public static string TWO_FACTOR_TIME_QUERY() => two_factor_base.Replace("%s", "QueryTime");
    }
}
