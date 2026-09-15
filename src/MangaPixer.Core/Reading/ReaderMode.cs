namespace com.lifepixer.mangaplex.Core.Reading;

/// <summary>
/// Reader display mode.
/// </summary>
public enum ReaderMode
{
    PagedLtr = 0,
    PagedRtl = 1,
    DoubleSpread = 2,
    VerticalWebtoon = 3
}

/// <summary>
/// Reading progress state for an item.
/// </summary>
public enum ReadingState
{
    Unread = 0,
    InProgress = 1,
    Completed = 2
}
