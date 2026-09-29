namespace com.lifepixer.mangapixer.Core.Api;

// Cover layer (1.29.0, lane C) - DTOs beyond the contract skeletons in VolumeDtos.cs.

/// <summary>Result of "Delete stored volume covers": how many covers and automatic decisions went.</summary>
public sealed record DeleteVolumeCoversResult
{
    public required int CoversDeleted { get; init; }
    public required int DecisionsReset { get; init; }
    public required int ChoicesReset { get; init; }
}
