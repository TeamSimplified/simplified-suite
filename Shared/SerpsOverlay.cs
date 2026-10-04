using System.IO;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;

namespace Simplified.Shared;

/// <summary>
/// The two resources the game expects to find in every mod that ships a shader
/// package: a surface descriptor at <c>tools_package/default/serps</c> and the
/// vehicle shadow material that points at
/// <c>shader_package/vehicle_carbon_shadowcast.nefx3</c>.
///
/// 489 bytes, carried as an embedded resource rather than a file beside the exe,
/// so only the assembly ships and there is nothing loose for anyone to delete.
/// The one copy lives in Shared and every packager that needs it embeds that
/// same file: four apps write shader packages, and four copies of a binary is
/// four chances for three of them to be right.
///
/// <c>LogicalName</c> is pinned in each csproj so the lookup name does not
/// depend on the assembly's namespace, and this reads from its own assembly —
/// so each packager finds the copy embedded in its own Core.
///
/// Internal, and that matters. Linked into several Cores it would otherwise be
/// several public types of the same name, and the moment one of those Cores
/// referenced another — which the Grid Editor's already does, for the Team
/// Editor's — anything using it would stop compiling on an ambiguous reference.
/// Internal keeps each copy invisible to the others. It is packaging's own
/// business anyway, not something another assembly should be reaching for.
/// </summary>
internal static class SerpsOverlay
{
    /// <summary>What the resource is called inside the assembly, and on disk.</summary>
    public const string FileName = "serps.erp";

    /// <summary>
    /// Fold the two resources into <paramref name="erp"/>, replacing anything
    /// already under the same identifier.
    ///
    /// Called on whatever is about to become part of a shader package: the Grid
    /// and Team editors hand it their logo overlay, the Sponsor editor hands it
    /// the archive itself. Either way the pair ends up in what ships.
    ///
    /// Written once here rather than three times, because three copies of a
    /// twenty-line reparenting loop is how two of them end up subtly different.
    /// </summary>
    internal static void AddTo(ErpFile erp)
    {
        var work = Path.Combine(Path.GetTempPath(), "simplified-serps", Guid.NewGuid().ToString("N"));

        try
        {
            var serps = ErpFileIO.Load(WriteTo(work));

            // Snapshot: adding to Resources mutates the list being walked.
            foreach (var resource in serps.Resources.ToList())
            {
                var existing = erp.Resources.Find(r => r.Identifier == resource.Identifier);
                if (existing is not null) erp.Resources.Remove(existing);

                resource.ParentFile = erp;
                foreach (var fragment in resource.Fragments) fragment.ParentFile = erp;
                erp.Resources.Add(resource);
            }
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Put it in <paramref name="folder"/> and return the path, ready to be
    /// loaded and merged like any other source archive.
    /// </summary>
    internal static string WriteTo(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);

        using var source = typeof(SerpsOverlay).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException(
                $"The embedded '{FileName}' is missing from {typeof(SerpsOverlay).Assembly.GetName().Name}. "
                + "Check the <EmbeddedResource> entry in its csproj.");

        using var file = File.Create(path);
        source.CopyTo(file);

        return path;
    }
}
