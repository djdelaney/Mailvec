namespace Mailvec.DevCorpus.Tests;

/// <summary>
/// The claims worth holding the generator to beyond its content: it never
/// writes where a real pipeline would ingest it, and the same options are the
/// same bytes every run.
/// </summary>
public sealed class WriterTests : IDisposable
{
    private readonly string _temp = TempDir.Create("mailvec-devcorpus-w-");
    private static readonly CorpusOptions Small = new(Filler: 5);

    public void Dispose() => TempDir.Delete(_temp);

    private string Write(string target, string? sharedConfig = null) =>
        CorpusWriter.Write(target, Small, sharedConfig ?? TempDir.NoSharedConfig);

    private void ShouldRefuse(string target, string? sharedConfig = null, string? because = null)
    {
        var before = Snapshot();
        Should.Throw<RefusedException>(() => Write(target, sharedConfig), because);
        Snapshot().ShouldBe(before, "a refused run must write nothing");
    }

    private List<string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(_temp, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Two_runs_produce_identical_corpora()
    {
        var a = Write(Path.Combine(_temp, "a"));
        var b = Write(Path.Combine(_temp, "b"));

        static Dictionary<string, byte[]> Files(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != "env.sh") // holds the absolute path, by design
                .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);

        var fa = Files(a);
        var fb = Files(b);
        fb.Keys.Order(StringComparer.Ordinal).ShouldBe(fa.Keys.Order(StringComparer.Ordinal));
        foreach (var (rel, bytes) in fa) fb[rel].ShouldBe(bytes, $"{rel} differs between runs");
    }

    [Fact]
    public void Env_sh_points_a_run_at_this_corpus_and_nowhere_else()
    {
        var root = Write(Path.Combine(_temp, "c"));
        var env = File.ReadAllText(Path.Combine(root, "env.sh"));
        env.ShouldContain($"export Archive__DatabasePath='{Path.Combine(root, "state", "archive.sqlite")}'");
        env.ShouldContain($"export Ingest__MaildirRoot='{Path.Combine(root, "Mail")}'");
        env.ShouldContain("export Fastmail__AccountId=''");
    }

    [Fact]
    public void An_existing_empty_directory_is_accepted()
    {
        var dir = Path.Combine(_temp, "empty");
        Directory.CreateDirectory(dir);
        Write(dir);
        Directory.Exists(Path.Combine(dir, "Mail", "INBOX", "cur")).ShouldBeTrue();
    }

    [Fact]
    public void A_non_empty_directory_is_refused()
    {
        var dir = Path.Combine(_temp, "full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mine.txt"), "keep");
        ShouldRefuse(dir);
    }

    [Fact]
    public void A_file_or_a_missing_parent_is_refused()
    {
        var file = Path.Combine(_temp, "afile");
        File.WriteAllText(file, "x");
        ShouldRefuse(file);
        ShouldRefuse(Path.Combine(_temp, "no", "such", "parent"));
    }

    [Fact]
    public void Anywhere_inside_a_maildir_is_refused()
    {
        // A Maildir root: its child folder has cur/. Generating beside that
        // folder would put the corpus in the next scan.
        var root = Path.Combine(_temp, "Mail");
        Directory.CreateDirectory(Path.Combine(root, "INBOX", "cur"));
        ShouldRefuse(Path.Combine(root, "dev"));
        // Inside the folder itself, and further down.
        ShouldRefuse(Path.Combine(root, "INBOX", "dev"));
        Directory.CreateDirectory(Path.Combine(root, "Other"));
        ShouldRefuse(Path.Combine(root, "Other", "dev"));
    }

    [Theory]
    [InlineData(".frozen-corpus")]
    [InlineData("archive.sqlite")]
    [InlineData(".mbsyncstate")]
    [InlineData(".mailvec-mbsync-heartbeat")]
    public void A_directory_marked_as_real_mail_is_refused_all_the_way_down(string marker)
    {
        var real = Path.Combine(_temp, "real-" + marker.Trim('.'));
        Directory.CreateDirectory(Path.Combine(real, "deep"));
        File.WriteAllText(Path.Combine(real, marker), "");
        ShouldRefuse(Path.Combine(real, "deep", "dev"));
    }

    [Fact]
    public void The_live_maildir_named_by_the_shared_config_is_refused()
    {
        var live = Path.Combine(_temp, "live-mail");
        Directory.CreateDirectory(live);
        var config = Path.Combine(_temp, "cfg", "appsettings.Local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, $$"""{ "Ingest": { "MaildirRoot": "{{live.Replace("\\", "\\\\")}}" } }""");

        ShouldRefuse(Path.Combine(live, "dev"), config);
        ShouldRefuse(Path.Combine(_temp, "cfg", "dev"), config, "the shared config's own directory holds the live database");
        Write(Path.Combine(_temp, "elsewhere"), config); // and it is not a blanket refusal
    }

    [Fact]
    public void An_unreadable_shared_config_is_a_refusal_not_a_pass()
    {
        var config = Path.Combine(_temp, "broken.json");
        File.WriteAllText(config, "{ not json");
        ShouldRefuse(Path.Combine(_temp, "dev"), config);
    }

    [Fact]
    public void A_symlink_cannot_disguise_a_target_inside_a_maildir()
    {
        var root = Path.Combine(_temp, "Mail");
        Directory.CreateDirectory(Path.Combine(root, "INBOX", "cur"));
        var link = Path.Combine(_temp, "innocent");
        Directory.CreateSymbolicLink(link, root);
        ShouldRefuse(Path.Combine(link, "dev"));
    }

    [Fact]
    public void Hazards_are_only_written_when_asked_for()
    {
        var plain = Write(Path.Combine(_temp, "plain"));
        Directory.Exists(Path.Combine(plain, "outside")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plain, "Mail", "Projects")).ShouldBeFalse();

        var root = CorpusWriter.Write(Path.Combine(_temp, "haz"), new CorpusOptions(Hazards: true, Filler: 0), TempDir.NoSharedConfig);
        var links = Directory.EnumerateFiles(Path.Combine(root, "Mail"), "*", SearchOption.AllDirectories)
            .Where(f => new FileInfo(f).LinkTarget is not null).ToList();
        links.Count.ShouldBe(1);
        var target = new FileInfo(links[0]).ResolveLinkTarget(returnFinalTarget: true).ShouldNotBeNull();
        target.FullName.ShouldStartWith(Path.Combine(root, "outside"), Case.Sensitive, "the symlink must leave the Maildir");
        Directory.Exists(Path.Combine(root, "Mail", "Projects", "tmp", "cur")).ShouldBeTrue();
    }
}
