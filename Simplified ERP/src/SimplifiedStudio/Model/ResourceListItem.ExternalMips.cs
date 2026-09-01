namespace SimplifiedStudio.Model;

/// <summary>
/// Where the row's archive came from, which is what lets this app find a
/// texture's external mipmaps without a configured game folder.
///
/// ERP's own half of the shared row: Studio has no equivalent, because a
/// project's ERPs are extracted from a .simplified zip and have no position
/// inside a game install to reason from.
/// </summary>
public abstract partial class ResourceListItem
{
    /// <summary>
    /// Absolute path of the ERP file this row was loaded from, stamped after
    /// the list is built. Empty when the ERP came from somewhere other than a
    /// file on disk.
    /// </summary>
    public string ParentPath { get; set; } = "";
}
