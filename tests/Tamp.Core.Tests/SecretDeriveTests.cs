using System;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #tamp-ado-git#5 / systemic: <see cref="Secret.Derive"/> produces a derived secret (the transmitted
/// form of a secret — a base64 auth header, signed URL, hashed token) so a wrapper can register both
/// the raw and the transformed literal. Redaction matches literally, so registering only the raw value
/// leaves the transmitted form unredacted; both-in-the-table is the fix.
/// </summary>
public sealed class SecretDeriveTests
{
    [Fact]
    public void Derive_Applies_The_Transform_And_Defaults_The_Name()
    {
        var pat = new Secret("ado-pat", "hunter2");
        var header = pat.Derive(v => "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + v)));

        Assert.Equal("ado-pat.derived", header.Name);
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":hunter2")), header.Reveal());
        Assert.Equal("<Secret:ado-pat.derived>", header.ToString());   // still redacts
    }

    [Fact]
    public void Derive_Honors_A_Custom_Name()
    {
        var pat = new Secret("ado-pat", "hunter2");
        var header = pat.Derive(v => v.ToUpperInvariant(), name: "ado-pat-header");
        Assert.Equal("ado-pat-header", header.Name);
        Assert.Equal("HUNTER2", header.Reveal());
    }

    [Fact]
    public void Derive_Null_Transform_Throws()
        => Assert.Throws<ArgumentNullException>(() => new Secret("x", "v").Derive(null!));

    [Fact]
    public void Derive_Transform_Returning_Null_Throws()
        => Assert.Throws<InvalidOperationException>(() => new Secret("x", "v").Derive(_ => null!));

    [Fact]
    public void Registering_Both_Raw_And_Derived_Redacts_Both_Forms()
    {
        // The scenario from tamp-ado-git#5: the raw PAT is registered but the base64 header (a
        // different literal) is what's transmitted. Deriving + registering both scrubs either.
        var pat = new Secret("ado-pat", "hunter2");
        var header = pat.Derive(v => "AUTHORIZATION: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + v)));

        var table = new RedactionTable();
        var plan = new CommandPlan
        {
            Executable = "git",
            Arguments = new[] { "-c", "http.extraHeader=" + header.Reveal(), "fetch" },
            Secrets = new[] { pat, header },
        };
        table.RegisterAll(plan);

        var line = $"trace: git -c http.extraHeader={header.Reveal()} fetch (pat={pat.Reveal()})";
        var redacted = table.Redact(line);

        Assert.DoesNotContain("hunter2", redacted);                        // raw scrubbed
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":hunter2")), redacted); // derived scrubbed
        Assert.Contains("<Secret:ado-pat>", redacted);
        Assert.Contains("<Secret:ado-pat.derived>", redacted);
    }
}
