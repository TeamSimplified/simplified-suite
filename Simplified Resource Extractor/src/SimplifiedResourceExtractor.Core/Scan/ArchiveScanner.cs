using EgoEngineLibrary.Archive.Erp;
using SimplifiedResourceExtractor.Core.Patterns;

namespace SimplifiedResourceExtractor.Core.Scan;

/// <summary>One archive that holds something wanted, and what it holds.</summary>
public sealed record ScannedArchive(string Path, int Version, IReadOnlyList<string> Identifiers);

/// <summary>How far along a scan is.</summary>
public sealed record ScanProgress(int Scanned, int Total, int Matches);

/// <summary>What a scan found.</summary>
public sealed record ScanResult(
    IReadOnlyList<ScannedArchive> Matched,
    int ArchivesSeen,
    int ArchivesUnreadable,
    long ResourcesSeen)
{
    /// <summary>Distinct resources wanted, across every archive.</summary>
    public int MatchedResources { get; } =
        Matched.SelectMany(a => a.Identifiers).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// The archive version to write results as. Taking the highest is what the
    /// tool this replaces did; in practice every F1 25 archive is version 3, so
    /// the question only arises for a folder mixing games.
    /// </summary>
    public int Version { get; } = Matched.Count == 0 ? 4 : Matched.Max(a => a.Version);
}

/// <summary>
/// Finding which archives, out of everything under a folder, hold resources the
/// rules want.
///
/// The whole point is that this never opens an archive properly.
/// <see cref="ErpIndex"/> reads the resource table and stops, so a full F1 25
/// install — 8,166 archives, 28 GB, 2.3 million resources — is a question about
/// a few tens of megabytes of tables rather than about 28 GB of payloads. Only
/// the archives that turn out to matter are opened for real, later, by the job
/// that writes the output.
/// </summary>
public static class ArchiveScanner
{
    /// <summary>
    /// Reports often enough to look alive and rarely enough not to be the
    /// bottleneck. A warm scan gets through the whole install in under a
    /// second, so one update per file would be thousands of dispatches to the
    /// UI thread for a progress line nobody can read that fast.
    /// </summary>
    private const int ReportEvery = 64;

    /// <summary>Search <paramref name="folder"/> and everything under it.</summary>
    public static ScanResult Scan(string folder, ReferenceList rules,
                                  IProgress<ScanProgress>? progress = null,
                                  CancellationToken cancel = default)
    {
        var files = Directory.GetFiles(folder, "*.erp", SearchOption.AllDirectories);

        var matched = new List<ScannedArchive>();
        var scanned = 0;
        var matches = 0;
        var unreadable = 0;
        long resources = 0;

        Parallel.ForEach(
            files,
            new ParallelOptions { CancellationToken = cancel },
            path =>
            {
                // A folder holds whatever it holds, and a file that is not an
                // archive is an ordinary answer rather than a failure - the
                // scan is a question asked of everything, not an instruction.
                var index = ErpIndex.TryRead(path);

                if (index is null)
                {
                    Interlocked.Increment(ref unreadable);
                }
                else
                {
                    Interlocked.Add(ref resources, index.Resources.Count);

                    List<string>? wanted = null;
                    foreach (var resource in index.Resources)
                    {
                        if (rules.Matches(resource.Identifier)) (wanted ??= []).Add(resource.Identifier);
                    }

                    if (wanted is not null)
                    {
                        lock (matched)
                        {
                            matched.Add(new ScannedArchive(path, index.Version, wanted));
                            matches += wanted.Count;
                        }
                    }
                }

                var done = Interlocked.Increment(ref scanned);
                if (progress is not null && (done % ReportEvery == 0 || done == files.Length))
                {
                    progress.Report(new ScanProgress(done, files.Length, Volatile.Read(ref matches)));
                }
            });

        // Parallel work finishes in whatever order it finishes in, and a job
        // built from it writes files named after these. Sorting is what makes
        // two runs over the same folder produce the same output.
        matched.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new ScanResult(matched, files.Length, unreadable, resources);
    }
}
