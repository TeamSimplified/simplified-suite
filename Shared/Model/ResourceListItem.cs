using System.ComponentModel;
using EgoEngineLibrary.Archive.Erp;

namespace SimplifiedStudio.Model;

/// <summary>Discriminator for the row's underlying resource kind.</summary>
public enum ResourceKind { Texture, Pkg, Xml, Material, Context, Vtf }

/// <summary>
/// Base for everything that can appear in the main resource list. The list
/// historically held only <see cref="TextureVm"/>; once PKG and XML support
/// landed we needed a shared type so the ItemsControl can render a mix.
/// </summary>
public abstract partial class ResourceListItem : INotifyPropertyChanged
{
    public ErpResource Resource          { get; }
    public ErpFile     ParentErp         { get; }
    public string      ParentDisplayName { get; }

    protected ResourceListItem(ErpResource resource, ErpFile parentErp, string parentDisplayName)
    {
        Resource = resource;
        ParentErp = parentErp;
        ParentDisplayName = parentDisplayName;
    }

    public abstract ResourceKind Kind { get; }

    /// <summary>Three-letter tag used by the row template pill (TEX/PKG/XML/MAT/CON/VTF).</summary>
    public string KindTag => TagFor(Kind);

    /// <summary>Same tag, for callers that have a kind but no row — the bulk picker.</summary>
    public static string TagFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Pkg      => "PKG",
        ResourceKind.Xml      => "XML",
        ResourceKind.Material => "MAT",
        ResourceKind.Context  => "CON",
        ResourceKind.Vtf      => "VTF",
        _                     => "TEX",
    };

    /// <summary>Plural human name for the bulk picker, e.g. "Materials".</summary>
    public static string PluralFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Pkg      => "PKG resources",
        ResourceKind.Xml      => "XML resources",
        ResourceKind.Material => "Materials",
        ResourceKind.Context  => "Contexts",
        ResourceKind.Vtf      => "Vehicle handling (VTF)",
        _                     => "Textures",
    };

    public string FileName   => Resource.FileName;
    public string Folder     => Resource.Folder;
    public string Identifier => Resource.Identifier;

    /// <summary>
    /// Total number of rows that came from the same parent ERP. Stamped after
    /// every <c>RebuildTextureList</c> so the group header can display a count
    /// that doesn't shrink when the filter narrows the visible rows.
    /// </summary>
    public int ParentGroupCount { get; set; }

    /// <summary>One-line summary rendered under the file name.</summary>
    public abstract string SummaryLine { get; }

    private bool _isModified;

    /// <summary>
    /// True when this resource has been edited since the last save — drives the
    /// dot on the row.
    ///
    /// The window holds the master set of modified identifiers and stamps this
    /// on rebuild, because rows are thrown away and rebuilt from the ERPs often
    /// enough (after a bulk import, after undo) that a flag living only here
    /// would quietly disappear.
    /// </summary>
    public bool IsModified
    {
        get => _isModified;
        set
        {
            if (_isModified == value) return;
            _isModified = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModified)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
