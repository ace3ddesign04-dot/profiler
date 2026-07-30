using System.Collections.Generic;
using UnityEngine;

public class Minesweeper3DGame : MonoBehaviour
{
    public enum GameState
    {
        Ready,
        Playing,
        Won,
        Lost
    }

    [Header("Board")]
    [Min(2)]
    [SerializeField] private int columns = 9;

    [Min(2)]
    [SerializeField] private int rows = 9;

    [Min(1)]
    [SerializeField] private int mineCount = 10;

    [Min(0.01f)]
    [SerializeField] private float tileSpacing = 1.05f;

    [SerializeField] private bool centerBoard = true;

    [Header("Generation")]
    [SerializeField] private Minesweeper3DTile tilePrefab;
    [SerializeField] private Transform boardRoot;

    [Header("First Click")]
    [SerializeField] private bool firstClickSafe = true;
    [SerializeField] private bool protectFirstClickNeighbours = true;

    private Minesweeper3DTile[,] tiles;

    private bool minesGenerated;
    private int activeMineCount;
    private int revealedSafeTiles;
    private int flagsPlaced;

    public GameState State { get; private set; } = GameState.Ready;

    public int Columns => columns;
    public int Rows => rows;
    public int RemainingMines => activeMineCount - flagsPlaced;

    private void Start()
    {
        NewGame();
    }

    private void OnValidate()
    {
        columns = Mathf.Max(2, columns);
        rows = Mathf.Max(2, rows);
        tileSpacing = Mathf.Max(0.01f, tileSpacing);

        int maximumMines = Mathf.Max(1, columns * rows - 1);
        mineCount = Mathf.Clamp(mineCount, 1, maximumMines);
    }

    [ContextMenu("New Game")]
    public void NewGame()
    {
        if (tilePrefab == null)
        {
            Debug.LogError("Minesweeper3DGame: Tile Prefab is not assigned.");
            return;
        }

        if (boardRoot == null)
            boardRoot = transform;

        ClearBoard();

        activeMineCount = Mathf.Clamp(
            mineCount,
            1,
            columns * rows - 1
        );

        minesGenerated = false;
        revealedSafeTiles = 0;
        flagsPlaced = 0;
        State = GameState.Ready;

        CreateBoard();

        Debug.Log(
            $"Minesweeper ready. Board: {columns}x{rows}, Mines: {activeMineCount}"
        );
    }

    private void CreateBoard()
    {
        tiles = new Minesweeper3DTile[columns, rows];

        float xOffset = centerBoard
            ? (columns - 1) * tileSpacing * 0.5f
            : 0f;

        float zOffset = centerBoard
            ? (rows - 1) * tileSpacing * 0.5f
            : 0f;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Vector3 localPosition = new Vector3(
                    x * tileSpacing - xOffset,
                    0f,
                    -y * tileSpacing + zOffset
                );

                Minesweeper3DTile tile = Instantiate(
                    tilePrefab,
                    boardRoot
                );

                tile.transform.localPosition = localPosition;
                tile.transform.localRotation = Quaternion.identity;
                tile.name = $"Tile_{x}_{y}";

                tile.Initialize(this, x, y);
                tiles[x, y] = tile;
            }
        }
    }

    private void ClearBoard()
    {
        if (boardRoot == null)
            return;

        for (int i = boardRoot.childCount - 1; i >= 0; i--)
        {
            Transform child = boardRoot.GetChild(i);

            if (child.GetComponent<Minesweeper3DTile>() == null)
                continue;

            child.SetParent(null);

            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
    }

    public void RevealTile(Minesweeper3DTile tile)
    {
        if (tile == null)
            return;

        if (State == GameState.Won || State == GameState.Lost)
            return;

        if (tile.IsRevealed || tile.IsFlagged)
            return;

        if (!minesGenerated)
        {
            GenerateMines(tile.X, tile.Y);
            State = GameState.Playing;
        }

        if (tile.IsMine)
        {
            LoseGame(tile);
            return;
        }

        RevealSafeArea(tile);
        CheckForWin();
    }

    public void ToggleFlag(Minesweeper3DTile tile)
    {
        if (tile == null)
            return;

        if (State == GameState.Won || State == GameState.Lost)
            return;

        if (tile.IsRevealed)
            return;

        if (!tile.IsFlagged && flagsPlaced >= activeMineCount)
            return;

        bool newFlagState = !tile.IsFlagged;
        tile.SetFlagged(newFlagState);

        flagsPlaced += newFlagState ? 1 : -1;
        flagsPlaced = Mathf.Max(0, flagsPlaced);
    }

    private void GenerateMines(int firstX, int firstY)
    {
        minesGenerated = true;

        List<Vector2Int> availablePositions = BuildAvailablePositions(
            firstX,
            firstY,
            firstClickSafe && protectFirstClickNeighbours
        );

        // Small boards may not have enough positions when the whole
        // first-click neighbourhood is protected.
        if (availablePositions.Count < activeMineCount)
        {
            availablePositions = BuildAvailablePositions(
                firstX,
                firstY,
                false
            );
        }

        activeMineCount = Mathf.Min(
            activeMineCount,
            availablePositions.Count
        );

        Shuffle(availablePositions);

        for (int i = 0; i < activeMineCount; i++)
        {
            Vector2Int position = availablePositions[i];
            tiles[position.x, position.y].SetMine(true);
        }

        CalculateAdjacentMineCounts();
    }

    private List<Vector2Int> BuildAvailablePositions(
        int firstX,
        int firstY,
        bool protectNeighbours)
    {
        List<Vector2Int> positions = new List<Vector2Int>();

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (firstClickSafe)
                {
                    int xDistance = Mathf.Abs(x - firstX);
                    int yDistance = Mathf.Abs(y - firstY);

                    if (protectNeighbours)
                    {
                        if (xDistance <= 1 && yDistance <= 1)
                            continue;
                    }
                    else if (x == firstX && y == firstY)
                    {
                        continue;
                    }
                }

                positions.Add(new Vector2Int(x, y));
            }
        }

        return positions;
    }

    private static void Shuffle(List<Vector2Int> positions)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            int randomIndex = Random.Range(i, positions.Count);

            Vector2Int temporary = positions[i];
            positions[i] = positions[randomIndex];
            positions[randomIndex] = temporary;
        }
    }

    private void CalculateAdjacentMineCounts()
    {
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (tiles[x, y].IsMine)
                    continue;

                tiles[x, y].SetAdjacentMineCount(
                    CountAdjacentMines(x, y)
                );
            }
        }
    }

    private int CountAdjacentMines(int centerX, int centerY)
    {
        int count = 0;

        for (int yOffset = -1; yOffset <= 1; yOffset++)
        {
            for (int xOffset = -1; xOffset <= 1; xOffset++)
            {
                if (xOffset == 0 && yOffset == 0)
                    continue;

                int x = centerX + xOffset;
                int y = centerY + yOffset;

                if (!IsInsideBoard(x, y))
                    continue;

                if (tiles[x, y].IsMine)
                    count++;
            }
        }

        return count;
    }

    private void RevealSafeArea(Minesweeper3DTile startingTile)
    {
        Queue<Minesweeper3DTile> revealQueue =
            new Queue<Minesweeper3DTile>();

        revealQueue.Enqueue(startingTile);

        while (revealQueue.Count > 0)
        {
            Minesweeper3DTile current = revealQueue.Dequeue();

            if (current.IsRevealed ||
                current.IsFlagged ||
                current.IsMine)
            {
                continue;
            }

            current.RevealSafeTile();
            revealedSafeTiles++;

            if (current.AdjacentMineCount != 0)
                continue;

            AddNeighboursToQueue(
                current.X,
                current.Y,
                revealQueue
            );
        }
    }

    private void AddNeighboursToQueue(
        int centerX,
        int centerY,
        Queue<Minesweeper3DTile> queue)
    {
        for (int yOffset = -1; yOffset <= 1; yOffset++)
        {
            for (int xOffset = -1; xOffset <= 1; xOffset++)
            {
                if (xOffset == 0 && yOffset == 0)
                    continue;

                int x = centerX + xOffset;
                int y = centerY + yOffset;

                if (!IsInsideBoard(x, y))
                    continue;

                Minesweeper3DTile neighbour = tiles[x, y];

                if (!neighbour.IsRevealed &&
                    !neighbour.IsFlagged &&
                    !neighbour.IsMine)
                {
                    queue.Enqueue(neighbour);
                }
            }
        }
    }

    private void CheckForWin()
    {
        int safeTileCount =
            columns * rows - activeMineCount;

        if (revealedSafeTiles < safeTileCount)
            return;

        State = GameState.Won;
        flagsPlaced = activeMineCount;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Minesweeper3DTile tile = tiles[x, y];

                if (tile.IsMine)
                    tile.SetFlagged(true);

                tile.SetInteraction(false);
            }
        }

        Debug.Log("Minesweeper: You win.");
    }

    private void LoseGame(Minesweeper3DTile explodedTile)
    {
        State = GameState.Lost;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Minesweeper3DTile tile = tiles[x, y];

                if (tile.IsMine)
                    tile.RevealMine(tile == explodedTile);

                tile.SetInteraction(false);
            }
        }

        Debug.Log("Minesweeper: Game over.");
    }

    private bool IsInsideBoard(int x, int y)
    {
        return x >= 0 &&
               x < columns &&
               y >= 0 &&
               y < rows;
    }
}
