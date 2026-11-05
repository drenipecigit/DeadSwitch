namespace DeadSwitch.Tests;

// A made-up address with the right form. It must never be replaced by a real one.
internal static class TestWebhook
{
    public const string Token = "TESTTOKEN_NOT_REAL";
    public const string Url = "https://discord.com/api/webhooks/000000000000000000/" + Token;
}
