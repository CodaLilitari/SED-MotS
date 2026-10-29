using Sed.Core.IO;

namespace Sed.Core.Tests;

public sealed class AtomicFilePromotionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-promotion-" + Guid.NewGuid().ToString("N"));

    public AtomicFilePromotionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CompetingCreatorsPublishOnlyOneCompleteDistinctOutput()
    {
        // A check followed by rename can let several creators report success,
        // replacing the first winner. Different contents expose that loss even
        // when a journal's matching-output check would otherwise hide it.
        for (int round = 0; round < 32; round++)
        {
            string destination = Path.Combine(_directory, "output.bin");
            var contents = Enumerable.Range(0, 4).Select(index =>
                Enumerable.Repeat((byte)(index + 1), 131_072 + index).ToArray()).ToArray();
            var sources = Enumerable.Range(0, contents.Length)
                .Select(index => Path.Combine(_directory, "stage-" + index + ".tmp")).ToArray();
            for (int index = 0; index < sources.Length; index++) File.WriteAllBytes(sources[index], contents[index]);
            using var start = new Barrier(sources.Length);
            var attempts = Enumerable.Range(0, sources.Length).Select(index => Task.Factory.StartNew(() =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                try
                {
                    AtomicFilePromotion.MoveNew(sources[index], destination);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            bool[] succeeded = await Task.WhenAll(attempts);
            int winner = Assert.Single(Enumerable.Range(0, succeeded.Length), index => succeeded[index]);
            Assert.Equal(contents[winner], File.ReadAllBytes(destination));
            Assert.False(File.Exists(sources[winner]));
            foreach (int loser in Enumerable.Range(0, sources.Length).Where(index => !succeeded[index]))
                Assert.Equal(contents[loser], File.ReadAllBytes(sources[loser]));
            foreach (string source in sources) File.Delete(source);
            File.Delete(destination);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingFileOrDirectoryCannotBeReplaced(bool directory)
    {
        string staged = Path.Combine(_directory, "stage.tmp"), destination = Path.Combine(_directory, "output");
        File.WriteAllText(staged, "new output");
        if (directory) Directory.CreateDirectory(destination);
        else File.WriteAllText(destination, "existing output");

        Assert.ThrowsAny<IOException>(() => AtomicFilePromotion.MoveNew(staged, destination));

        Assert.Equal("new output", File.ReadAllText(staged));
        if (directory) Assert.True(Directory.Exists(destination));
        else Assert.Equal("existing output", File.ReadAllText(destination));
    }

    [Fact]
    public void DanglingDestinationLinkIsNotReplacedOrFollowed()
    {
        if (!OperatingSystem.IsLinux()) return;
        string staged = Path.Combine(_directory, "stage.tmp"), destination = Path.Combine(_directory, "output");
        string missing = Path.Combine(_directory, "missing");
        File.WriteAllText(staged, "new output");
        File.CreateSymbolicLink(destination, missing);

        Assert.Throws<IOException>(() => AtomicFilePromotion.MoveNew(staged, destination));

        Assert.Equal(missing, new FileInfo(destination).LinkTarget);
        Assert.False(File.Exists(missing));
        Assert.Equal("new output", File.ReadAllText(staged));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
