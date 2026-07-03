using System.Linq;
using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.Decals;
using Content.Shared.Tiles;
using Robust.Shared.Configuration;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Maps;

/// <summary>
/// Handles server-side tile manipulation like prying/deconstructing tiles.
/// </summary>
public sealed partial class TileSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private IRobustRandom _robustRandom = default!;
    [Dependency] private ITileDefinitionManager _tileDefinitionManager = default!;
    [Dependency] private SharedDecalSystem _decal = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private IGameTiming _timing = default!;

    public const int ChunkSize = 16;

    private int _tileStackLimit;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GridInitializeEvent>(OnGridStartup);
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
        SubscribeLocalEvent<TileHistoryComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<TileHistoryComponent, ComponentHandleState>(OnHandleState);
        SubscribeLocalEvent<TileHistoryComponent, FloorTileAttemptEvent>(OnFloorTileAttempt);

        _cfg.OnValueChanged(CCVars.TileStackLimit, t => _tileStackLimit = t, true);
    }

    private bool _internalTileUpdate;

    private void OnTileChanged(ref TileChangedEvent ev)
    {
        if (_internalTileUpdate)
            return;

        if (!TryComp<TileHistoryComponent>(ev.Entity, out var history))
            return;

        foreach (var change in ev.Changes)
        {
            // If the new tile is the same as the old one (e.g. only variant changed), we don't care.
            if (change.NewTile.TypeId == change.OldTile.TypeId)
                continue;

            var chunkIndices = change.ChunkIndex;

            // If a system calls SetTile, it intends to REPLACE whatever it was there before, erasing the history.
            if (history.ChunkHistory.TryGetValue(chunkIndices, out var c) &&
                c.History.Remove(change.GridIndices))
            {
                c.LastModified = _timing.CurTick;
                if (c.History.Count == 0)
                {
                    history.ChunkHistory.Remove(chunkIndices);
                }
                Dirty(ev.Entity, history);
            }
        }
    }


    /// <summary>
    /// Tries to add a Tile to the history. Fails if stack would pass the limit, or if trying to place on a
    /// non-whitelisted tile with the flag set to false.
    /// </summary>
    public bool ReplaceTile(TileRef tileRef,
        ContentTileDefinition replacementTile,
        bool ignoreWhitelist = false,
        bool replaceTopMost = true,
        byte? variant = null)
    {
        var gridUid = tileRef.GridUid;
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        return ReplaceTile(tileRef, replacementTile, tileRef.GridUid, grid, ignoreWhitelist, replaceTopMost, variant);
    }

    /// <summary>
    /// Tries to add a Tile to the history. Fails if stack would pass the limit, or if trying to place on a
    /// non-whitelisted tile with the flag set to false.
    /// </summary>
    public bool ReplaceTile(TileRef tileRef,
        ContentTileDefinition replacementTile,
        EntityUid gridEntityUid,
        MapGridComponent? component = null,
        bool ignoreWhitelist = false,
        bool replaceTopMost = true,
        byte? variant = null)
    {
        if (!Resolve(gridEntityUid, ref component, false))
            return false;

        var currentTile = tileRef.Tile;
        var currentDef = (ContentTileDefinition) _tileDefinitionManager[currentTile.TypeId];
        var gridUid = tileRef.GridUid;

        if (!ignoreWhitelist)
        {
            var allowed = false;
            if (replacementTile.BaseTurf?.Id == currentDef.ID)
            {
                allowed = true;
            }
            else
            {
                if (replacementTile.BaseWhitelist.Any(whitelist => whitelist.Id == currentDef.ID))
                {
                    allowed = true;
                }
            }

            if (!allowed)
            {
                Log.Error($"Tile {replacementTile.ID} is not allowed to be placed on {currentDef.ID} at {tileRef.GridIndices} on grid {gridUid}.");
                return false;
            }
        }

        var history = EnsureComp<TileHistoryComponent>(gridUid);
        var chunkIndices = SharedMapSystem.GetChunkIndices(tileRef.GridIndices, 16);
        var chunk = history.ChunkHistory.GetOrNew(chunkIndices);
        var stack = chunk.History.GetOrNew(tileRef.GridIndices);

        if (!replaceTopMost)
        {
            if (stack.Count >= _tileStackLimit)
            {
                Log.Error($"Tile stack limit reached at {tileRef.GridIndices} on grid {gridUid}. Cannot stack {replacementTile.ID} on top.");
                return false;
            }

            stack.Add((ushort) currentTile.TypeId);
            chunk.LastModified = _timing.CurTick;
            Dirty(gridUid, history);
        }


        //TODO VELKEN FIX THIS TOMORROW: NEED TO REPLACE TILE WHILE KEEPING THE STACK
        if (stack.Count >= _tileStackLimit)
        {
            Log.Error($"Tile stack limit reached at {tileRef.GridIndices} on grid {gridUid}. Cannot stack {replacementTile.ID} on top.");
            return false;
        }

        stack.Add((ushort) currentTile.TypeId);
        chunk.LastModified = _timing.CurTick;

        //Destroy any decals on the tile
        var center = _turf.GetTileCenter(tileRef).Position;
        var decals = _decal.GetDecalsInRange(gridUid, center, 0.5f);
        foreach (var (id, _) in decals)
        {
            _decal.RemoveDecal(gridUid, id);
        }

        _internalTileUpdate = true;
        try
        {
            var actualVariant = variant ?? PickVariant(replacementTile, tileRef.GridIndices);
            _maps.SetTile(gridUid, component, tileRef.GridIndices, new Tile(replacementTile.TileId, 0, actualVariant, tileRef.Tile.RotationMirroring));
        }
        finally
        {
            _internalTileUpdate = false;
        }

        Dirty(gridUid, history);
        return true;
    }

    private void OnHandleState(EntityUid uid, TileHistoryComponent component, ref ComponentHandleState args)
    {
        if (args.Current is not TileHistoryState state && args.Current is not TileHistoryDeltaState)
            return;

        if (args.Current is TileHistoryState fullState)
        {
            component.ChunkHistory.Clear();
            foreach (var (key, value) in fullState.ChunkHistory)
            {
                component.ChunkHistory[key] = new TileHistoryChunk(value);
            }

            return;
        }

        if (args.Current is TileHistoryDeltaState deltaState)
        {
            deltaState.ApplyToComponent(component);
        }
    }

    private void OnGetState(EntityUid uid, TileHistoryComponent component, ref ComponentGetState args)
    {
        if (args.FromTick <= component.CreationTick)
        {
            var fullHistory = new Dictionary<Vector2i, TileHistoryChunk>(component.ChunkHistory.Count);
            foreach (var (key, value) in component.ChunkHistory)
            {
                fullHistory[key] = new TileHistoryChunk(value);
            }
            args.State = new TileHistoryState(fullHistory);
            return;
        }

        var data = new Dictionary<Vector2i, TileHistoryChunk>();
        foreach (var (index, chunk) in component.ChunkHistory)
        {
            if (chunk.LastModified >= args.FromTick)
                data[index] = new TileHistoryChunk(chunk);
        }

        args.State = new TileHistoryDeltaState(data, new(component.ChunkHistory.Keys));
    }

    /// <summary>
    /// On grid startup, ensure that we have Tile History.
    /// </summary>
    private void OnGridStartup(GridInitializeEvent ev)
    {
        if (HasComp<MapComponent>(ev.EntityUid))
            return;

        EnsureComp<TileHistoryComponent>(ev.EntityUid);
    }

    /// <summary>
    /// Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile, Vector2i? indices = null)
    {
        if (indices == null)
            return PickVariant(tile, _robustRandom);

        var seed = indices.Value.X * 31 + indices.Value.Y;
        return PickVariant(tile, seed);
    }

    /// <summary>
    /// Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile, IRobustRandom random)
    {
        var variants = tile.PlacementVariants;

        var sum = variants.Sum();
        var accumulated = 0f;
        var rand = random.NextFloat() * sum;

        for (byte i = 0; i < variants.Length; ++i)
        {
            accumulated += variants[i];

            if (accumulated >= rand)
                return i;
        }

        // Shouldn't happen
        throw new InvalidOperationException($"Invalid weighted variantize tile pick for {tile.ID}!");
    }

    /// <summary>
    /// Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile, int seed)
    {
        var rand = new System.Random(seed);
        return PickVariant(tile, rand);
    }

    /// <summary>
    /// Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile, System.Random random)
    {
        var variants = tile.PlacementVariants;

        var sum = variants.Sum();
        var accumulated = 0f;
        var rand = (float) random.NextDouble() * sum;

        for (byte i = 0; i < variants.Length; ++i)
        {
            accumulated += variants[i];

            if (accumulated >= rand)
                return i;
        }

        // Shouldn't happen
        throw new InvalidOperationException($"Invalid weighted variantize tile pick for {tile.ID}!");
    }

    /// <summary>
    /// Returns a tile with a weighted random variant.
    /// </summary>
    public Tile GetVariantTile(ContentTileDefinition tile, Vector2i indices)
    {
        return new Tile(tile.TileId, variant: PickVariant(tile, indices));
    }

    /// <summary>
    /// Returns a tile with a weighted random variant.
    /// </summary>
    public Tile GetVariantTile(ContentTileDefinition tile, IRobustRandom random)
    {
        return new Tile(tile.TileId, variant: PickVariant(tile, random));
    }

    /// <summary>
    /// Returns a tile with a weighted random variant.
    /// </summary>
    public Tile GetVariantTile(ContentTileDefinition tile, System.Random random)
    {
        return new Tile(tile.TileId, variant: PickVariant(tile, random));
    }

    /// <summary>
    /// Returns a tile with a weighted random variant.
    /// </summary>
    public Tile GetVariantTile(ContentTileDefinition tile, int seed)
    {
        var rand = new System.Random(seed);
        return new Tile(tile.TileId, variant: PickVariant(tile, rand));
    }

    /// <summary>
    /// Attempts to pry a tile at the specified indices on a grid.
    /// </summary>
    public bool PryTile(Vector2i indices, EntityUid gridId)
    {
        var grid = Comp<MapGridComponent>(gridId);
        var tileRef = _maps.GetTileRef(gridId, grid, indices);
        return PryTile(tileRef);
    }

    /// <summary>
    /// Attempts to pry the specified tile.
    /// </summary>
    public bool PryTile(TileRef tileRef)
    {
        return PryTile(tileRef, false);
    }

    /// <summary>
    /// Attempts to pry the specified tile, optionally prying plating as well.
    /// </summary>
    public bool PryTile(TileRef tileRef, bool pryPlating)
    {
        var tile = tileRef.Tile;

        if (tile.IsEmpty)
            return false;

        var tileDef = (ContentTileDefinition)_tileDefinitionManager[tile.TypeId];

        if (!tileDef.CanCrowbar)
            return false;

        return DeconstructTile(tileRef);
    }

    /// <summary>
    /// Deconstructs a tile, restoring the previous tile from history if available.
    /// </summary>
    public bool DeconstructTile(TileRef tileRef, bool spawnItem = true)
    {
        if (tileRef.Tile.IsEmpty)
            return false;

        var tileDef = (ContentTileDefinition)_tileDefinitionManager[tileRef.Tile.TypeId];

        //Can't deconstruct anything that doesn't have a base turf.
        if (tileDef.BaseTurf == null)
            return false;

        var gridUid = tileRef.GridUid;
        var mapGrid = Comp<MapGridComponent>(gridUid);

        const float margin = 0.1f;
        var bounds = mapGrid.TileSize - margin * 2;
        var indices = tileRef.GridIndices;
        var coordinates = _maps.GridTileToLocal(gridUid, mapGrid, indices)
            .Offset(new Vector2(
                (_robustRandom.NextFloat(-0.5f, 0.5f)) * bounds,
                (_robustRandom.NextFloat(-0.5f, 0.5f)) * bounds));

        var historyComp = EnsureComp<TileHistoryComponent>(gridUid);

        var chunkIndices = SharedMapSystem.GetChunkIndices(indices, ChunkSize);

        //Pop from stack if we have history
        if (historyComp.ChunkHistory.TryGetValue(chunkIndices, out var chunk) &&
            chunk.History.TryGetValue(indices, out var stack) && stack.Count > 0)
        {
            chunk.LastModified = _timing.CurTick;
            Dirty(gridUid, historyComp);

            var previousTileId = stack.Last();
            stack.RemoveAt(stack.Count - 1);

            //Clean up empty stacks to avoid memory buildup
            if (stack.Count == 0)
            {
                chunk.History.Remove(indices);
            }

            // Clean up empty chunks
            if (chunk.History.Count == 0)
            {
                historyComp.ChunkHistory.Remove(chunkIndices);
            }

            //Replace tile with the one it was placed on
            _internalTileUpdate = true;
            try
            {
                _maps.SetTile(gridUid, mapGrid, indices, new Tile(previousTileId));
            }
            finally
            {
                _internalTileUpdate = false;
            }
        }
        else
        {
            //No stack? Assume BaseTurf was the layer below
            if (tileDef.BaseTurf == null)
                return false;

            var previousDef = (ContentTileDefinition)_tileDefinitionManager[tileDef.BaseTurf.Value];
            _internalTileUpdate = true;
            try
            {
                _maps.SetTile(gridUid, mapGrid, indices, new Tile(previousDef.TileId));
            }
            finally
            {
                _internalTileUpdate = false;
            }
        }

        //Actually spawn the relevant tile item at the right position and give it some random offset.
        if (spawnItem)
        {
            var tileItem = Spawn(tileDef.ItemDropPrototypeName, coordinates);
            Transform(tileItem).LocalRotation = _robustRandom.NextDouble() * Math.Tau;
        }

        //Destroy any decals on the tile
        var decals = _decal.GetDecalsInRange(gridUid, coordinates.SnapToGrid(EntityManager, _mapManager).Position, 0.5f);
        foreach (var (id, _) in decals)
        {
            _decal.RemoveDecal(tileRef.GridUid, id);
        }

        return true;
    }

    private void OnFloorTileAttempt(Entity<TileHistoryComponent> ent, ref FloorTileAttemptEvent args)
    {
        if (_tileStackLimit == 0)
            return;
        var chunkIndices = SharedMapSystem.GetChunkIndices(args.GridIndices, ChunkSize);
        if (!ent.Comp.ChunkHistory.TryGetValue(chunkIndices, out var chunk) ||
            !chunk.History.TryGetValue(args.GridIndices, out var stack))
            return;
        args.Cancelled = stack.Count >= _tileStackLimit; // greater or equals because the attempt itself counts as a tile we're trying to place
    }
}
