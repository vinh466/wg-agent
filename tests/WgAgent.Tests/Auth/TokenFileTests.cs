using WgAgent.Platform;
using WgAgent.Platform.Linux;

namespace WgAgent.Tests.Auth;

public sealed class TokenFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-token-" + Guid.NewGuid().ToString("N"));
    public TokenFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private TokenFile File(string name = "token") => new(Path.Combine(_dir, name));

    [Fact]
    public void Secret_RedactsEveryTextualForm_REQ_SEC_050()
    {
        var secret = Secret.From("s3cr3t-value");
        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.Equal("[REDACTED]", $"{secret}");
        Assert.Equal("s3cr3t-value", secret.Reveal());   // the one deliberate way out
    }

    [Fact]
    public void Secret_ComparesByValue_REQ_SEC_073()
    {
        var secret = Secret.From("abcdef");
        Assert.True(secret.Matches("abcdef"));
        Assert.False(secret.Matches("abcdeg"));
        Assert.False(secret.Matches("abcde"));    // a shorter candidate is not a prefix match
        Assert.False(secret.Matches("abcdefg"));
    }

    [Fact]
    public void TokenFile_WritesOwnerOnlyAndReadsBack_REQ_CLI_013()
    {
        var file = File();
        file.Write(Secret.From("the-token"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(file.Path));
        Assert.True(file.Read()!.Matches("the-token"));
    }

    [Fact]
    public void TokenFile_ReplacesAtomicallyLeavingOneFile_REQ_CLI_014()
    {
        var file = File();
        file.Write(Secret.From("first"));
        file.Write(Secret.From("second"));

        Assert.True(file.Read()!.Matches("second"));
        Assert.Equal([Path.GetFileName(file.Path)], Directory.GetFiles(_dir).Select(Path.GetFileName));   // no temp left behind
    }

    [Fact]
    public void TokenFile_AbsentOrEmpty_IsNoToken_REQ_SEC_072()
    {
        Assert.Null(File("absent").Read());
        var empty = File("empty");
        System.IO.File.WriteAllText(empty.Path, "\n");
        System.IO.File.SetUnixFileMode(empty.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(empty.Read());
    }

    [Fact]
    public void TokenFile_RejectsModeBeyondItsOwner_REQ_SEC_074()
    {
        var file = File();
        file.Write(Secret.From("the-token"));
        System.IO.File.SetUnixFileMode(file.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var refused = Assert.Throws<TokenFile.Invalid>(() => file.Read());
        Assert.Contains("beyond its owner", refused.Message);
    }

    [Fact]
    public void TokenFile_AcceptsAFileItOwns_REQ_SEC_082()
    {
        // The writer owns what it creates; rejection of a file owned by another needs root — the
        // integration tier chowns one and asserts the refusal.
        var file = File();
        file.Write(Secret.From("the-token"));
        Assert.NotNull(file.Read());
    }
}
