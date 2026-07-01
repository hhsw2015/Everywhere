namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// SPEC §Phase 4 G3 — `browser: true` implies `async (page, args)`;
/// `browser: false` (or unset) implies `async (args)`. Mismatch fails
/// SIGNATURE_FORM_MISMATCH.
/// </summary>
public static class SignatureGuard
{
    public static GateResult Check(string source)
    {
        var r = GateResult.Empty();
        var declared = AdapterSourceScan.DeclaredBrowser(source);
        var actual = AdapterSourceScan.SignatureForm(AdapterSourceScan.StripCommentsAndStrings(source));
        if (actual is null)
        {
            r.Errors.Add(new GateFinding("G3", "SIGNATURE_FORM_MISMATCH",
                "no async arrow function found in adapter body"));
            return r;
        }
        if (declared == true && actual != "async (page, args)")
            r.Errors.Add(new GateFinding("G3", "SIGNATURE_FORM_MISMATCH",
                $"browser:true requires async (page, args) — found {actual}"));
        else if ((declared is null or false) && actual != "async (args)")
            r.Errors.Add(new GateFinding("G3", "SIGNATURE_FORM_MISMATCH",
                $"non-browser adapter requires async (args) — found {actual}"));
        return r;
    }
}
