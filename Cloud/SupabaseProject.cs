namespace HappyBot.Cloud;

/// <summary>
/// Public client settings for the DANBOT Supabase project. The publishable key
/// is meant to ship inside apps; row-level security on <c>bot_profiles</c>
/// limits every signed-in account to its own rows. Never put the secret
/// (service role) key here.
/// </summary>
internal static class SupabaseProject
{
    public const string Url = "https://fzvvbtqyvcofoeixxwfn.supabase.co";
    public const string PublishableKey = "sb_publishable_TCq5xBYeufft9Xk4_YaSCg_Brvh5gio";
}
