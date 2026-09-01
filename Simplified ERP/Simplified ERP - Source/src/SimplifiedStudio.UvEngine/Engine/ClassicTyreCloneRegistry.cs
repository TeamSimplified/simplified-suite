// ClassicTyreCloneRegistry.cs
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace F1modding3dtoolV1
{
    /// <summary>
    /// Tracks the "extra" classic-tyre wrapper groups that the loader
    /// builds but does not initially add to the scene root.
    ///
    /// Background: the classic tyre loader produces 8 submeshes (the
    /// union of 4 sidewalls + 4 treads, all at the tyre-local origin)
    /// representing one tyre. To put a tyre at every wheel position
    /// of the assembled car we need four copies, but at LOAD time we
    /// only know we want ONE visible (the assembler hasn't been told
    /// to run yet -- the user has to flip the "Assemble car" toggle
    /// for the wheel-position matrices to come into play).
    ///
    /// Solution: the loader makes all four wheel-tagged wrappers
    /// upfront (each tagged via ModelPartNameRegistry with
    /// wheel_fl / wheel_fr / wheel_bl / wheel_br), but only attaches
    /// the FIRST one to the scene root. The other three get parked
    /// here, keyed by the visible one, so MainWindow's
    /// OnAssembleCarToggle can splice them in (when the toggle goes
    /// on) and out (when it goes off). This produces:
    ///
    ///   * Toggle OFF:  one tyre at origin (raw loader output, same
    ///                  shape as every other classic part when off).
    ///   * Toggle ON:   four tyres, one per wheel slot, each placed
    ///                  by the body's render-graph world transform.
    ///
    /// The keying-by-visible-wrapper avoids needing to expose new
    /// fields on F1ModelLoaderClassic (which has a static API) and
    /// keeps the registry lookup local to the toggle handler.
    /// </summary>
    public static class ClassicTyreCloneRegistry
    {
        // Maps the visible "anchor" wrapper (the one the loader added
        // to the scene root) to the list of additional wrappers that
        // should be added when assemble is on.
        private static readonly Dictionary<Model3D, List<Model3DGroup>> _clones =
            new Dictionary<Model3D, List<Model3DGroup>>();

        public static void Register(Model3D anchor, List<Model3DGroup> clones)
        {
            if (anchor == null || clones == null) return;
            _clones[anchor] = clones;
        }

        public static List<Model3DGroup> GetClones(Model3D anchor)
        {
            if (anchor == null) return null;
            return _clones.TryGetValue(anchor, out var list) ? list : null;
        }

        /// <summary>
        /// Snapshot of the anchor -> clones map. Used by MainWindow's
        /// assemble toggle to walk every registered tyre and either
        /// add or remove its sibling wrappers.
        /// </summary>
        public static IEnumerable<KeyValuePair<Model3D, List<Model3DGroup>>> All()
        {
            // Materialize a copy so callers can mutate the underlying
            // collection while iterating.
            return new List<KeyValuePair<Model3D, List<Model3DGroup>>>(_clones);
        }

        /// <summary>
        /// Removes all entries. Call before loading a new car so
        /// stale Model3D references from the previous load don't
        /// linger.
        /// </summary>
        public static void Clear()
        {
            _clones.Clear();
        }
    }
}
