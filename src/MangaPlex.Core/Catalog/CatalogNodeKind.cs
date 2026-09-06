namespace com.lifepixer.mangaplex.Core.Catalog;

/// <summary>
/// Kind of a catalog node in the folder-native library tree.
/// </summary>
public enum CatalogNodeKind
{
    Folder = 0,
    Archive = 1
}

/// <summary>
/// Availability state of a catalog node.
/// </summary>
public enum CatalogNodeAvailability
{
    Available = 0,
    Preparing = 1,
    Unsupported = 2,
    Corrupt = 3,
    Unavailable = 4,
    Tombstoned = 5
}
