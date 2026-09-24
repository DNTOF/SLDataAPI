using SLDataAPI.Auth;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class ControlAuditViewTests
{
    [Fact]
    public void Admin_SeesOtherActorsBody()
    {
        string body = ControlAuditView.BodyForViewer(
            "{\"command\":\"ban 1\"}", "ops", "duty1", "admin");
        Assert.Contains("ban 1", body);
        Assert.DoesNotContain(ControlAuditView.RedactedBody, body);
    }

    [Fact]
    public void Duty_CannotSeeOtherKeyPayload()
    {
        string body = ControlAuditView.BodyForViewer(
            "{\"command\":\"ban 1\"}", "admin-key", "duty1", "duty");
        Assert.Equal(ControlAuditView.RedactedBody, body);
    }

    [Fact]
    public void Duty_CanSeeOwnPayload()
    {
        string body = ControlAuditView.BodyForViewer(
            "{\"action\":\"list\"}", "duty1", "duty1", "duty");
        Assert.Contains("list", body);
    }

    [Fact]
    public void UnknownViewer_CannotSeeBodies()
    {
        Assert.Equal(ControlAuditView.RedactedBody,
            ControlAuditView.BodyForViewer("{\"x\":1}", "ops", null, "duty"));
    }

    [Fact]
    public void RedactSecrets_MasksApiKeysAndJsonFields()
    {
        string raw = "{\"command\":\"sldataapi\",\"token\":\"abc\",\"api_key\":\"sld_live_ABCDEF123456\"} Bearer sld_duty_ZZZ extra sld_live_PLAIN";
        string redacted = ControlAuditView.RedactSecrets(raw);
        Assert.DoesNotContain("sld_live_ABCDEF123456", redacted);
        Assert.DoesNotContain("sld_duty_ZZZ", redacted);
        Assert.DoesNotContain("sld_live_PLAIN", redacted);
        Assert.Contains("sld_***", redacted);
        Assert.Contains("Bearer ***", redacted);
        Assert.Contains("\"***\"", redacted);
    }
}
