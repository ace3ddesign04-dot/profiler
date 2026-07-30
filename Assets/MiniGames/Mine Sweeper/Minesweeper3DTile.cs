using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

[RequireComponent(typeof(Collider))]
public class Minesweeper3DTile : MonoBehaviour, IPointerClickHandler {
    [Header("References")]
    [SerializeField] private Renderer tileRenderer;
    [SerializeField] private TMP_Text numberText;
    [SerializeField] private GameObject mineVisual;
    [SerializeField] private GameObject flagVisual;

    [Header("Colors")]
    [SerializeField] private Color coveredColor = new Color(0.35f, 0.35f, 0.35f);
    [SerializeField] private Color revealedColor = new Color(0.75f, 0.75f, 0.75f);
    [SerializeField] private Color flaggedColor = new Color(0.9f, 0.65f, 0.15f);
    [SerializeField] private Color mineColor = new Color(0.15f, 0.15f, 0.15f);
    [SerializeField] private Color explodedMineColor = new Color(0.9f, 0.1f, 0.1f);

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private MaterialPropertyBlock propertyBlock;
    private Minesweeper3DGame game;
    private Collider tileCollider;

    private bool isMine;
    private bool isRevealed;
    private bool isFlagged;
    private bool interactionEnabled = true;
    private bool isExplodedMine;
    private int adjacentMineCount;

    public int X { get; private set; }
    public int Y { get; private set; }

    public bool IsMine => isMine;
    public bool IsRevealed => isRevealed;
    public bool IsFlagged => isFlagged;
    public int AdjacentMineCount => adjacentMineCount;

    private void Reset() {
        tileRenderer = GetComponentInChildren<Renderer>();
        numberText = GetComponentInChildren<TMP_Text>();
    }

    private void Awake() {
        propertyBlock = new MaterialPropertyBlock();
        tileCollider = GetComponent<Collider>();

        if (tileRenderer == null)
            tileRenderer = GetComponentInChildren<Renderer>();

        if (numberText == null)
            numberText = GetComponentInChildren<TMP_Text>();
    }

    public void Initialize(Minesweeper3DGame gameController, int gridX, int gridY) {
        game = gameController;
        X = gridX;
        Y = gridY;

        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();

        if (tileCollider == null)
            tileCollider = GetComponent<Collider>();

        if (tileRenderer == null)
            tileRenderer = GetComponentInChildren<Renderer>();

        if (numberText == null)
            numberText = GetComponentInChildren<TMP_Text>();

        isMine = false;
        isRevealed = false;
        isFlagged = false;
        isExplodedMine = false;
        adjacentMineCount = 0;
        interactionEnabled = true;

        if (tileCollider != null)
            tileCollider.enabled = true;

        RefreshVisual();
    }

    public void OnPointerClick(PointerEventData eventData) {
        if (!interactionEnabled || game == null)
            return;

        if (eventData.button == PointerEventData.InputButton.Left)
            game.RevealTile(this);
        else if (eventData.button == PointerEventData.InputButton.Right)
            game.ToggleFlag(this);
    }

    public void SetMine(bool value) {
        isMine = value;
    }

    public void SetAdjacentMineCount(int count) {
        adjacentMineCount = Mathf.Clamp(count, 0, 8);
    }

    public void SetFlagged(bool flagged) {
        if (isRevealed)
            return;

        isFlagged = flagged;
        RefreshVisual();
    }

    public void RevealSafeTile() {
        if (isMine || isRevealed)
            return;

        isFlagged = false;
        isRevealed = true;
        RefreshVisual();
    }

    public void RevealMine(bool exploded) {
        if (!isMine)
            return;

        isFlagged = false;
        isRevealed = true;
        isExplodedMine = exploded;
        RefreshVisual();
    }

    public void SetInteraction(bool enabled) {
        interactionEnabled = enabled;

        if (tileCollider != null)
            tileCollider.enabled = enabled;
    }

    private void RefreshVisual() {
        if (numberText != null) {
            numberText.text = string.Empty;
            numberText.gameObject.SetActive(false);
        }

        if (mineVisual != null)
            mineVisual.SetActive(false);

        if (flagVisual != null)
            flagVisual.SetActive(false);

        if (isRevealed) {
            if (isMine) {
                SetTileColor(isExplodedMine ? explodedMineColor : mineColor);

                if (mineVisual != null)
                    mineVisual.SetActive(true);
                else
                    ShowText("X", Color.white);
            }
            else {
                SetTileColor(revealedColor);

                if (adjacentMineCount > 0)
                    ShowText(adjacentMineCount.ToString(), GetNumberColor(adjacentMineCount));
            }

            return;
        }

        if (isFlagged) {
            SetTileColor(flaggedColor);

            if (flagVisual != null)
                flagVisual.SetActive(true);
            else
                ShowText("F", Color.black);
        }
        else {
            SetTileColor(coveredColor);
        }
    }

    private void ShowText(string value, Color color) {
        if (numberText == null)
            return;

        numberText.gameObject.SetActive(true);
        numberText.text = value;
        numberText.color = color;
    }

    private void SetTileColor(Color color) {
        if (tileRenderer == null) {
            Debug.LogError($"Minesweeper3DTile: No Renderer assigned on {name}.", this);
            return;
        }

        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();

        tileRenderer.GetPropertyBlock(propertyBlock);

        Material material = tileRenderer.sharedMaterial;

        if (material != null && material.HasProperty(BaseColorId))
            propertyBlock.SetColor(BaseColorId, color);

        if (material != null && material.HasProperty(ColorId))
            propertyBlock.SetColor(ColorId, color);

        tileRenderer.SetPropertyBlock(propertyBlock);
    }

    private static Color GetNumberColor(int number) {
        switch (number) {
            case 1: return Color.blue;
            case 2: return new Color(0f, 0.5f, 0f);
            case 3: return Color.red;
            case 4: return new Color(0f, 0f, 0.5f);
            case 5: return new Color(0.5f, 0f, 0f);
            case 6: return Color.cyan;
            case 7: return Color.black;
            case 8: return Color.gray;
            default: return Color.white;
        }
    }
}