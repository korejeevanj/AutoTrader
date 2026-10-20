using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayRunIdentityTests
{
    [Fact]
    public void A_changed_engine_configuration_changes_the_input_hash_and_an_unchanged_one_does_not()
    {
        using var fixture = new ReplayFixture(); var dataset = fixture.Load();
        var changed = fixture.Engine with { Strategies = [fixture.Engine.Strategies[0] with { SessionExit = new(9, 36) }] };

        var hash = ReplayRunIdentity.InputHash(dataset, fixture.Configuration, fixture.Engine);

        Assert.Equal(hash, ReplayRunIdentity.InputHash(dataset, fixture.Configuration, fixture.Engine));
        Assert.NotEqual(hash, ReplayRunIdentity.InputHash(dataset, fixture.Configuration, changed));
    }

    [Fact]
    public void A_changed_downloaded_file_or_replay_configuration_changes_the_input_hash()
    {
        using var fixture = new ReplayFixture(); var dataset = fixture.Load();
        var hash = ReplayRunIdentity.InputHash(dataset, fixture.Configuration, fixture.Engine);
        var file = dataset.Files[0];
        var otherFiles = dataset with { Files = [file with { Sha256 = "different-download" }, .. dataset.Files.Skip(1)] };
        var otherSettings = fixture.Configuration with { Presets = [new(), new() { Id = "adverse" }] };

        Assert.NotEqual(hash, ReplayRunIdentity.InputHash(otherFiles, fixture.Configuration, fixture.Engine));
        Assert.NotEqual(hash, ReplayRunIdentity.InputHash(dataset, otherSettings, fixture.Engine));
    }

    [Fact]
    public void Code_versions_name_every_assembly_that_can_change_replay_behaviour_and_exclude_the_host()
    {
        var names = ReplayRunIdentity.CodeVersions.Select(version => version.Name).ToArray();

        Assert.Equal(["Trading.Replay", "Trading.Engine", "Trading.Risk", "Trading.Analytics", "Trading.Data", "Trading.Domain"], names);
        Assert.All(ReplayRunIdentity.CodeVersions, version => Assert.NotEqual(Guid.Empty, version.Mvid));
    }
}
