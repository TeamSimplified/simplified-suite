using System.Text;
using EgoEngineLibrary.Archive.Erp;
using SimplifiedChassisSwapper.Core.Util;

namespace SimplifiedChassisSwapper.Core.Swap;

/// <summary>
/// Performs a chassis swap: re-badges a donor chassis ERP under a new name and
/// grafts on the target team's <c>/wep/</c> setup, so the game loads the donor
/// car in that team's slot.
///
/// The pipeline below mirrors a hand-made reference swap, verified resource-for-
/// resource and byte-for-byte on its PKG payloads.
/// </summary>
public sealed class ChassisSwapper
{
    private const string WepSegment = "/wep/";

    public ChassisSwapResult Run(ChassisSwapOptions options, Action<string, double>? progress = null)
    {
        options.Validate();
        var result = new ChassisSwapResult { OutputPath = options.OutputPath };

        void Report(string msg, double p) => progress?.Invoke(msg, p);

        // ── Load ────────────────────────────────────────────────────────────
        Report("Loading chassis ERP…", 0.00);
        var chassis = ErpFileIO.Load(options.ChassisErpPath);
        result.ResourcesIn = chassis.Resources.Count;

        Report("Loading team ERP…", 0.03);
        var team = ErpFileIO.Load(options.TeamErpPath);

        // ── 1. Embed mipmaps (before renaming: mip paths must still resolve) ─
        Report("Embedding full-resolution textures…", 0.05);
        var mips = MipmapEmbedder.EmbedAll(chassis, options.GameDir,
            (m, p) => Report(m, 0.05 + p * 0.35));
        result.TexturesEmbedded = mips.Embedded;
        result.TexturesMipsUnreachable = mips.Unreachable;
        if (mips.Unreachable > 0)
            result.Warnings.Add(
                $"{mips.Unreachable} texture(s) had external mipmaps that couldn't be read — " +
                "they kept only their low-resolution in-ERP image. Check the game directory.");

        // ── 2. Capture the chassis /wep/ layers before they're dropped ───────
        // Their contents become the team-named layers in the output.
        Report("Reading chassis camera/model layers…", 0.42);
        var chassisLayers = CaptureLayerJson(chassis, options.OldName);

        // ── 3. Re-badge: identifiers and payloads ───────────────────────────
        Report("Renaming to the new identity…", 0.45);
        var (renamed, patched) = Rebadge(chassis, options.OldName, options.NewName);
        result.IdentifiersRenamed = renamed;
        result.FragmentsPatched = patched;

        // ── 4. Drop the chassis's own slot wiring ───────────────────────────
        // The car keeps its geometry but must not claim its old identity: the
        // handling file and every /wep/ resource are replaced by the team's.
        Report("Removing the chassis's own slot wiring…", 0.55);
        var before = chassis.Resources.Count;
        var keptResources = new List<ErpResource>(chassis.Resources.Count);
        foreach (var res in chassis.Resources)
        {
            var id = res.Identifier;
            if (id.Contains(".vtf", StringComparison.OrdinalIgnoreCase)) { result.RemovedVtf++; continue; }
            if (id.Contains(WepSegment, StringComparison.OrdinalIgnoreCase)) { result.RemovedWep++; continue; }
            keptResources.Add(res);
        }
        chassis.Resources = keptResources;
        _ = before;

        // ── 5. Graft the team's /wep/ resources ─────────────────────────────
        // .wep / event-graph come across untouched; .layer files are rebuilt
        // from the chassis so the donor's cameras and model survive.
        Report("Grafting the team's slot wiring…", 0.62);
        GraftTeamWep(chassis, team, chassisLayers, options, result);

        // ── 6. Driver in-car animations ─────────────────────────────────────
        Report("Transplanting driver animations…", 0.75);
        TransplantAnimset(chassis, options, result);

        // ── 7. Write ────────────────────────────────────────────────────────
        Report("Writing output ERP…", 0.90);
        chassis.UpdateOffsets();
        var outDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        ErpFileIO.Save(chassis, options.OutputPath);

        result.ResourcesOut = chassis.Resources.Count;
        Report("Done.", 1.0);
        return result;
    }

    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Grab each chassis <c>/wep/</c> <c>.layer</c> payload as JSON, keyed by the
    /// suffix after the chassis token (e.g. <c>-camscargame</c>) so it can be
    /// paired with the team's equivalent.
    /// </summary>
    private static Dictionary<string, string> CaptureLayerJson(ErpFile chassis, string oldName)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in chassis.Resources)
        {
            var id = res.Identifier;
            if (!id.Contains(WepSegment, StringComparison.OrdinalIgnoreCase)) continue;
            if (!id.EndsWith(".layer", StringComparison.OrdinalIgnoreCase)) continue;
            if (res.Fragments.Count == 0) continue;

            var fileName = Path.GetFileNameWithoutExtension(id);          // e.g. force_india-camscargame
            var idx = fileName.IndexOf(oldName, StringComparison.OrdinalIgnoreCase);
            var suffix = idx >= 0 ? fileName[(idx + oldName.Length)..] : fileName;   // "-camscargame"

            try { map[suffix] = PkgPatcher.ToJson(res.Fragments[0].GetDataArray(true)); }
            catch { /* not a readable PKG — the team's own layer is used instead */ }
        }
        return map;
    }

    /// <summary>
    /// Replace the chassis token with the new name in both resource identifiers
    /// and fragment payloads. Payload edits are equal-length byte replacements,
    /// which is why the two names must match in length.
    /// </summary>
    private static (int renamed, int patched) Rebadge(ErpFile erp, string oldName, string newName)
    {
        var oldBytes = Encoding.UTF8.GetBytes(oldName);
        var newBytes = Encoding.UTF8.GetBytes(newName);
        int renamed = 0, patched = 0;

        foreach (var res in erp.Resources)
        {
            if (res.Identifier.Contains(oldName, StringComparison.Ordinal))
            {
                res.Identifier = res.Identifier.Replace(oldName, newName, StringComparison.Ordinal);
                renamed++;
            }

            foreach (var frag in res.Fragments)
            {
                byte[] data;
                try { data = frag.GetDataArray(true); }
                catch { continue; }

                if (!ReplaceInPlace(data, oldBytes, newBytes)) continue;
                try { frag.SetData(data); patched++; }
                catch { /* leave the fragment untouched if it won't re-pack */ }
            }
        }
        return (renamed, patched);
    }

    /// <summary>Equal-length in-place byte replacement. True when anything changed.</summary>
    private static bool ReplaceInPlace(byte[] haystack, byte[] needle, byte[] replacement)
    {
        if (needle.Length == 0 || needle.Length != replacement.Length) return false;
        var changed = false;
        var limit = haystack.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (!match) continue;
            Buffer.BlockCopy(replacement, 0, haystack, i, replacement.Length);
            changed = true;
            i += needle.Length - 1;
        }
        return changed;
    }

    /// <summary>
    /// Copy the team's <c>/wep/</c> resources in. <c>.layer</c> payloads are
    /// rebuilt from the chassis equivalent so the donor's camera and model data
    /// carries over; everything else comes across as-is.
    /// </summary>
    private static void GraftTeamWep(
        ErpFile chassis, ErpFile team,
        Dictionary<string, string> chassisLayers,
        ChassisSwapOptions options, ChassisSwapResult result)
    {
        var teamToken = DeriveTeamToken(options.TeamErpPath);

        foreach (var teamRes in team.Resources)
        {
            var id = teamRes.Identifier;
            if (!id.Contains(WepSegment, StringComparison.OrdinalIgnoreCase)) continue;

            var isLayer = id.EndsWith(".layer", StringComparison.OrdinalIgnoreCase);
            if (isLayer && teamRes.Fragments.Count > 0)
            {
                var fileName = Path.GetFileNameWithoutExtension(id);
                var idx = fileName.IndexOf(teamToken, StringComparison.OrdinalIgnoreCase);
                var suffix = idx >= 0 ? fileName[(idx + teamToken.Length)..] : fileName;

                if (chassisLayers.TryGetValue(suffix, out var chassisJson))
                {
                    try
                    {
                        // Null keeps the chassis's own engine. For the team's, read
                        // it before its payload is overwritten — only the car-model
                        // layer declares any, so this is empty (and a no-op) elsewhere.
                        Dictionary<string, string>? audioOverrides = null;
                        if (options.EngineAudio == EngineAudioSource.Team)
                        {
                            try
                            {
                                // Take only the switch, so the layer keeps its own
                                // bundle — the switch is what selects the engine.
                                var teamAudio = PkgPatcher.ReadAudioBankValues(
                                    PkgPatcher.ToJson(teamRes.Fragments[0].GetDataArray(true)));
                                if (teamAudio.TryGetValue(PkgPatcher.SwitchNameKey, out var teamSwitch))
                                    audioOverrides = PkgPatcher.BuildAudioOverrides(
                                        PkgPatcher.StripTypeTag(teamSwitch));
                            }
                            catch
                            {
                                result.Warnings.Add(
                                    $"Couldn't read the team's engine audio from '{suffix}' — " +
                                    "the chassis's own was kept.");
                            }
                        }
                        else if (options.EngineAudio == EngineAudioSource.Explicit && options.EngineAudioPick is { } pick)
                        {
                            audioOverrides = PkgPatcher.BuildAudioOverrides(pick.Switch);
                        }

                        var patchedJson = PkgPatcher.PatchLayerJson(
                            chassisJson, options.OldName, teamToken, options.NewName, audioOverrides);
                        teamRes.Fragments[0].SetData(PkgPatcher.FromJson(patchedJson));
                        result.LayersRebuilt++;

                        RecordEngineAudio(patchedJson, result);
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Couldn't rebuild layer '{suffix}' from the chassis: {ex.Message}");
                    }
                }
                else
                {
                    result.Warnings.Add(
                        $"The chassis has no '{suffix}' layer — the team's own was kept, " +
                        "so that camera/model setup won't match the donor car.");
                }
            }
            else
            {
                result.WepCopied++;
            }

            AddOrReplace(chassis, teamRes);
        }
    }

    /// <summary>
    /// Publish the chassis's animset under the team's animset identifier: the
    /// clip list keeps pointing at the chassis's animations (so the donor's
    /// driver poses are used) while the game looks it up by the team's name.
    /// </summary>
    private static void TransplantAnimset(ErpFile chassis, ChassisSwapOptions options, ChassisSwapResult result)
    {
        // Match the chassis's season — a 2025 car needs the 2025 animations.
        var season = IncardriverLocator.InferYear(options.ChassisErpPath);
        var incPath = options.IncardriverErpPath
                      ?? IncardriverLocator.FindIncardriverErp(options.GameDir, season);
        if (string.IsNullOrWhiteSpace(incPath) || !File.Exists(incPath))
        {
            result.Warnings.Add(
                "No driver animation ERP found — the in-car driver animations were left untouched. " +
                "Set the game directory (or pick incardriver_f1.erp) to include them.");
            return;
        }

        ErpFile inc;
        try { inc = ErpFileIO.Load(incPath); }
        catch (Exception ex)
        {
            result.Warnings.Add($"Couldn't read the driver animation ERP: {ex.Message}");
            return;
        }

        var animsets = IncardriverLocator.ListAnimsets(inc);
        var chassisToken = Path.GetFileNameWithoutExtension(options.ChassisErpPath);
        var teamToken = DeriveTeamToken(options.TeamErpPath);

        // By the file's own name first, then by its team's: a special livery has
        // no driver animations of its own and wears the official car's.
        var chassisAnimset = options.ChassisAnimsetOverride
                             ?? IncardriverLocator.MatchAnimsetForCar(animsets, options.ChassisErpPath);
        var teamAnimset = options.TeamAnimsetOverride
                          ?? IncardriverLocator.MatchAnimsetForCar(animsets, options.TeamErpPath);

        result.ChassisAnimset = chassisAnimset;
        result.TeamAnimset = teamAnimset;

        if (chassisAnimset is null || teamAnimset is null)
        {
            result.Warnings.Add(
                $"Couldn't match driver animations (chassis '{chassisToken}' → {chassisAnimset ?? "none"}, " +
                $"team '{teamToken}' → {teamAnimset ?? "none"}). Driver animations were left untouched.");
            return;
        }

        var chassisId = IncardriverLocator.FindAnimsetIdentifier(inc, chassisAnimset);
        var teamId = IncardriverLocator.FindAnimsetIdentifier(inc, teamAnimset);
        if (chassisId is null || teamId is null) return;

        var source = inc.FindResource(chassisId);
        var target = inc.FindResource(teamId);
        if (source.Fragments.Count == 0 || target.Fragments.Count == 0) return;

        try
        {
            var json = PkgPatcher.ToJson(source.Fragments[0].GetDataArray(true));
            var renamed = PkgPatcher.RenameAnimsetJson(json, chassisAnimset, teamAnimset);
            target.Fragments[0].SetData(PkgPatcher.FromJson(renamed));

            AddOrReplace(chassis, target);
            result.AnimsetTransplanted = true;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Couldn't transplant driver animations: {ex.Message}");
        }
    }

    /// <summary>
    /// Note the engine the finished layer ended up with, so the summary can report
    /// what the car will actually sound like rather than what was asked for.
    /// </summary>
    private static void RecordEngineAudio(string patchedLayerJson, ChassisSwapResult result)
    {
        var audio = PkgPatcher.ReadAudioBankValues(patchedLayerJson);
        if (audio.TryGetValue(PkgPatcher.SwitchNameKey, out var value))
            result.EngineAudioSwitch = PkgPatcher.StripTypeTag(value);
    }

    /// <summary>The team's token — its ERP file name, e.g. <c>sauber</c>.</summary>
    private static string DeriveTeamToken(string teamErpPath) =>
        Path.GetFileNameWithoutExtension(teamErpPath);

    /// <summary>Add a resource, replacing any existing one with the same identifier.</summary>
    private static void AddOrReplace(ErpFile erp, ErpResource incoming)
    {
        for (var i = 0; i < erp.Resources.Count; i++)
        {
            if (string.Equals(erp.Resources[i].Identifier, incoming.Identifier, StringComparison.OrdinalIgnoreCase))
            {
                erp.Resources[i] = incoming;
                return;
            }
        }
        incoming.ParentFile = erp;
        foreach (var frag in incoming.Fragments) frag.ParentFile = erp;
        erp.Resources.Add(incoming);
    }
}
