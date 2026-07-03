using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared.Maps;

/// <summary>
/// Component for tracking the history of tiles on a grid.
/// Used for tile stacking, allowing tiles to be deconstructed back to their previous state.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class TileHistoryComponent : Component
{
    /// <summary>
    ///     History of tiles for each grid chunk, mapped by chunk indices.
    /// </summary>
    [DataField]
    public Dictionary<Vector2i, TileHistoryChunk> ChunkHistory = new();
}

/// <summary>
/// Full state for <see cref="TileHistoryComponent"/>.
/// </summary>
[Serializable, NetSerializable]
public sealed class TileHistoryState : ComponentState
{
    public Dictionary<Vector2i, TileHistoryChunk> ChunkHistory;

    public TileHistoryState(Dictionary<Vector2i, TileHistoryChunk> chunkHistory)
    {
        ChunkHistory = chunkHistory;
    }
}

/// <summary>
/// Delta state for <see cref="TileHistoryComponent"/> to optimize networking.
/// </summary>
[Serializable, NetSerializable]
public sealed class TileHistoryDeltaState : ComponentState, IComponentDeltaState<TileHistoryState>
{
    public Dictionary<Vector2i, TileHistoryChunk> ChunkHistory;
    public HashSet<Vector2i> AllHistoryChunks;

    public TileHistoryDeltaState(Dictionary<Vector2i, TileHistoryChunk> chunkHistory, HashSet<Vector2i> allHistoryChunks)
    {
        ChunkHistory = chunkHistory;
        AllHistoryChunks = allHistoryChunks;
    }

    public void ApplyToFullState(TileHistoryState state)
    {
        var toRemove = new List<Vector2i>();
        foreach (var key in state.ChunkHistory.Keys)
        {
            if (!AllHistoryChunks.Contains(key))
                toRemove.Add(key);
        }

        foreach (var key in toRemove)
        {
            state.ChunkHistory.Remove(key);
        }

        foreach (var (indices, chunk) in ChunkHistory)
        {
            state.ChunkHistory[indices] = new TileHistoryChunk(chunk);
        }
    }

    public void ApplyToComponent(TileHistoryComponent component)
    {
        var toRemove = new List<Vector2i>();
        foreach (var key in component.ChunkHistory.Keys)
        {
            if (!AllHistoryChunks.Contains(key))
                toRemove.Add(key);
        }

        foreach (var key in toRemove)
        {
            component.ChunkHistory.Remove(key);
        }

        foreach (var (indices, chunk) in ChunkHistory)
        {
            component.ChunkHistory[indices] = new TileHistoryChunk(chunk);
        }
    }

    public TileHistoryState CreateNewFullState(TileHistoryState state)
    {
        var chunks = new Dictionary<Vector2i, TileHistoryChunk>(state.ChunkHistory.Count);

        foreach (var (indices, chunk) in ChunkHistory)
        {
            chunks[indices] = new TileHistoryChunk(chunk);
        }

        foreach (var (indices, chunk) in state.ChunkHistory)
        {
            if (AllHistoryChunks.Contains(indices))
                chunks.TryAdd(indices, new TileHistoryChunk(chunk));
        }

        return new TileHistoryState(chunks);
    }
}

/// <summary>
/// Data for a single chunk's tile history.
/// </summary>
[DataDefinition, Serializable, NetSerializable]
public sealed partial class TileHistoryChunk
{
    /// <summary>
    /// History of tiles for each tile index in the chunk.
    /// The list represents a stack of tile IDs, where the last element is the tile directly below the current one.
    /// </summary>
    [DataField]
    public Dictionary<Vector2i, List<ushort>> History = new();

    /// <summary>
    /// The tick this chunk was last modified. Used for delta networking.
    /// </summary>
    [ViewVariables]
    public GameTick LastModified;

    public TileHistoryChunk()
    {
    }

    public TileHistoryChunk(TileHistoryChunk other)
    {
        History = new Dictionary<Vector2i, List<ushort>>(other.History.Count);
        foreach (var (key, value) in other.History)
        {
            History[key] = new List<ushort>(value);
        }
        LastModified = other.LastModified;
    }
}
