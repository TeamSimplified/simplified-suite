using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Xml;

namespace SimplifiedStudio.Model;

/// <summary>
/// A vehicle-handling resource — <c>.vtf</c>, e.g.
/// <c>f1_2026_vehicle_package/teams/ferrari/handling/ferrari.vtf</c>. These
/// define car performance, which makes them worth finding on their own rather
/// than buried among every other XML resource in a 1000-entry archive.
///
/// Structurally they are binary XML like any other, so this derives from
/// <see cref="XmlVm"/> and every read/write path treats it as XML — only the
/// category and its label differ. The archive tags these resources
/// <c>VTF</c> itself, so no guessing from the file name is involved.
///
/// Note a VTF resource carries two fragments: the XML in fragment 0 and a
/// small trailing blob in fragment 1. Import replaces fragment 0 only, which
/// leaves the second untouched.
/// </summary>
public sealed class VtfVm : XmlVm
{
    public VtfVm(ErpResource resource, ErpFile parentErp, string parentDisplayName, XmlFile? parsed)
        : base(resource, parentErp, parentDisplayName, parsed) { }

    public override ResourceKind Kind => ResourceKind.Vtf;

    /// <summary>
    /// Spelled out in the row summary because the resource type is itself
    /// "VTF" — the tag alone would render as "VTF — VTF". The list pill still
    /// reads VTF.
    /// </summary>
    protected override string KindPrefix => "Vehicle handling";
}
